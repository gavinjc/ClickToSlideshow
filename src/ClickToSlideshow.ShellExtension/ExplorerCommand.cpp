#include "pch.h"
#include "ExplorerCommand.h"
#include "SupportedExtensions.h"

#include <appmodel.h>
#include <objbase.h>
#include <shlobj.h>

using Microsoft::WRL::ComPtr;

namespace
{
    // This DLL's own path — used both to find the sibling app exe and, during registration, to
    // point InprocServer32 at the right file. Explorer loads this DLL in-proc, so
    // GetModuleHandleExW against a static address inside this module always resolves to it.
    std::filesystem::path GetThisModulePath()
    {
        HMODULE module = nullptr;
        GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&GetThisModulePath),
            &module);

        wchar_t path[MAX_PATH];
        DWORD length = GetModuleFileNameW(module, path, static_cast<DWORD>(std::size(path)));
        return std::filesystem::path(std::wstring(path, length));
    }

    // Two valid layouts: the M6 unpackaged dev-test convention copies the app exe directly
    // alongside this DLL (flat); the packaged MSIX layout (M7) puts each project reference in
    // its own subfolder under a shared package root, so the exe is a sibling *folder* away
    // (ClickToSlideshow.App\ClickToSlideshow.App.exe), not a sibling file. Try the flat layout
    // first since it's the cheaper check, then fall back to the packaged layout.
    std::filesystem::path GetAppExePath()
    {
        std::filesystem::path dllDir = GetThisModulePath().parent_path();

        std::filesystem::path flatSibling = dllDir / L"ClickToSlideshow.App.exe";
        std::error_code ec;
        if (std::filesystem::exists(flatSibling, ec))
        {
            return flatSibling;
        }

        return dllDir.parent_path() / L"ClickToSlideshow.App" / L"ClickToSlideshow.App.exe";
    }

    // Empty when unpackaged (M6 dev-testing) — GetCurrentPackageFamilyName returns
    // APPMODEL_ERROR_NO_PACKAGE in that case, which is expected, not a failure.
    std::wstring GetPackageFamilyNameIfPackaged()
    {
        UINT32 length = 0;
        if (GetCurrentPackageFamilyName(&length, nullptr) != ERROR_INSUFFICIENT_BUFFER)
        {
            return L"";
        }

        std::wstring name(length, L'\0');
        if (GetCurrentPackageFamilyName(&length, name.data()) != ERROR_SUCCESS)
        {
            return L"";
        }

        name.resize(length - 1); // length includes the null terminator
        return name;
    }

    // A packaged full-trust app cannot be started via a raw ShellExecuteW/CreateProcess on its
    // exe path — that fails with ERROR_NOT_SUPPORTED (confirmed by testing, in ClickTo: PDF). It
    // has to be activated through the AppModel activation service using its AUMID instead. "App"
    // matches <Application Id="App"> in Package.appxmanifest.
    HRESULT ActivatePackagedApp(const std::wstring& packageFamilyName, const std::wstring& args)
    {
        std::wstring aumid = packageFamilyName + L"!App";

        ComPtr<IApplicationActivationManager> activationManager;
        HRESULT hr = CoCreateInstance(CLSID_ApplicationActivationManager, nullptr, CLSCTX_LOCAL_SERVER,
            IID_PPV_ARGS(&activationManager));
        if (FAILED(hr))
        {
            return hr;
        }

        // Without this, the activated app's window opens behind Explorer — activation through a
        // COM surrogate doesn't inherit the "user just clicked" foreground rights the way a
        // direct ShellExecuteW from Explorer's own process would.
        CoAllowSetForegroundWindow(activationManager.Get(), nullptr);

        DWORD processId = 0;
        return activationManager->ActivateApplication(aumid.c_str(), args.c_str(), AO_NONE, &processId);
    }

    std::vector<std::wstring> GetSelectedPaths(IShellItemArray* items)
    {
        std::vector<std::wstring> paths;

        DWORD count = 0;
        THROW_IF_FAILED(items->GetCount(&count));
        paths.reserve(count);

        for (DWORD i = 0; i < count; ++i)
        {
            ComPtr<IShellItem> item;
            THROW_IF_FAILED(items->GetItemAt(i, &item));

            wil::unique_cotaskmem_string path;
            THROW_IF_FAILED(item->GetDisplayName(SIGDN_FILESYSPATH, &path));
            paths.emplace_back(path.get());
        }

        return paths;
    }

    bool IsSupportedFile(const std::filesystem::path& path)
    {
        std::error_code ec;
        if (path.empty() || std::filesystem::is_directory(path, ec))
        {
            return false;
        }

        return ClickToSlideshow::ShellExtension::IsSupportedExtension(path.extension().wstring());
    }

    struct FolderContents
    {
        std::vector<std::wstring> supportedFiles;
        std::vector<std::wstring> skippedFileNames;
    };

    // Non-recursive — matches MainViewModel.LoadFolderAsync's existing folder-launch behaviour,
    // so a directly-selected folder behaves the same whether opened via the shell extension or
    // the app's own folder-path launch path. Unsupported files directly inside are reported as
    // skipped, same as an unsupported file picked directly in the selection — nested
    // subfolders are simply not part of this non-recursive listing at all, not "skipped".
    FolderContents ExpandFolder(const std::filesystem::path& folder)
    {
        FolderContents contents;
        std::error_code ec;
        for (const std::filesystem::directory_entry& entry : std::filesystem::directory_iterator(folder, ec))
        {
            if (entry.is_directory(ec))
            {
                continue;
            }

            if (IsSupportedFile(entry.path()))
            {
                contents.supportedFiles.push_back(entry.path().wstring());
            }
            else
            {
                contents.skippedFileNames.push_back(entry.path().filename().wstring());
            }
        }

        return contents;
    }

    // Spec §10 requires GetState to return quickly whatever the folder, so the look inside is
    // bounded: it ends at the first supported file, and a folder with no match in its first
    // entries is shown anyway (Invoke does the full check) rather than risk a slow scan of a huge
    // or network folder. Directory enumeration order is arbitrary, so this can only ever err
    // towards showing the entry, never towards hiding it for a folder that has something usable.
    constexpr int MaxFolderEntriesScanned = 100;

    bool FolderMayContainSupportedFile(const std::filesystem::path& folder)
    {
        std::error_code ec;
        std::filesystem::directory_iterator entries(folder, ec);
        if (ec)
        {
            return true;
        }

        int scanned = 0;
        for (; entries != std::filesystem::directory_iterator(); entries.increment(ec))
        {
            if (IsSupportedFile(entries->path()) || ++scanned >= MaxFolderEntriesScanned)
            {
                return true;
            }
        }

        // An enumeration cut short by an error (folder became unavailable) fails open too.
        return static_cast<bool>(ec);
    }

    // Used by GetState only. A folder counts as usable when something supported may be directly
    // inside it (non-recursive, matching Invoke's ExpandFolder), so right-clicking a folder with
    // no photos or music doesn't offer a menu item that would do nothing.
    bool IsUsableSelection(const std::wstring& path)
    {
        std::filesystem::path p(path);
        if (IsSupportedFile(p))
        {
            return true;
        }

        std::error_code ec;
        return std::filesystem::is_directory(p, ec) && FolderMayContainSupportedFile(p);
    }

    std::filesystem::path GetTempDir()
    {
        wil::unique_cotaskmem_string localAppData;
        THROW_IF_FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &localAppData));

        std::filesystem::path dir = std::filesystem::path(localAppData.get()) / L"Temp" / L"ClickToSlideshow";
        std::filesystem::create_directories(dir);
        return dir;
    }

    std::wstring NewGuidText()
    {
        GUID guid;
        THROW_IF_FAILED(CoCreateGuid(&guid));
        wil::unique_cotaskmem_string guidString;
        THROW_IF_FAILED(StringFromCLSID(guid, &guidString));

        // StringFromCLSID formats as "{...}" — strip the braces for a plain filename.
        std::wstring text = guidString.get();
        if (text.size() >= 2)
        {
            text = text.substr(1, text.size() - 2);
        }

        return text;
    }

    // %LOCALAPPDATA%\Temp\ClickToSlideshow\{guid}.<extension> — per spec §4.1, paths are never
    // passed on the command line (Explorer selections routinely blow past the ~32k char limit).
    // Used for both the supported-files list and the skipped-files list.
    std::filesystem::path WriteLinesFile(const std::vector<std::wstring>& lines, const wchar_t* extension)
    {
        std::filesystem::path file = GetTempDir() / (NewGuidText() + extension);

        std::ofstream stream(file, std::ios::binary | std::ios::trunc);
        THROW_HR_IF(E_UNEXPECTED, !stream.is_open());

        for (const std::wstring& line : lines)
        {
            int utf8Length = WideCharToMultiByte(CP_UTF8, 0, line.c_str(), -1, nullptr, 0, nullptr, nullptr);
            THROW_HR_IF(E_UNEXPECTED, utf8Length <= 0);

            std::string utf8(static_cast<size_t>(utf8Length) - 1, '\0');
            WideCharToMultiByte(CP_UTF8, 0, line.c_str(), -1, utf8.data(), utf8Length, nullptr, nullptr);

            stream << utf8 << '\n';
        }

        return file;
    }
}

IFACEMETHODIMP ExplorerCommandHandler::GetTitle(IShellItemArray* /*items*/, PWSTR* name)
{
    // Fixed wording for any selection count (spec §4.1) — unlike ClickTo: PDF there is no
    // meaningful single-item variant; a one-photo slideshow is a degenerate case, not a
    // different verb.
    return SHStrDupW(L"Create slideshow", name);
}

IFACEMETHODIMP ExplorerCommandHandler::GetIcon(IShellItemArray* /*items*/, PWSTR* icon)
{
    try
    {
        return SHStrDupW(GetAppExePath().c_str(), icon);
    }
    catch (...)
    {
        *icon = nullptr;
        return E_FAIL;
    }
}

IFACEMETHODIMP ExplorerCommandHandler::GetToolTip(IShellItemArray* /*items*/, PWSTR* infoTip)
{
    *infoTip = nullptr;
    return E_NOTIMPL;
}

IFACEMETHODIMP ExplorerCommandHandler::GetCanonicalName(GUID* guidCommandName)
{
    *guidCommandName = GUID_NULL;
    return S_OK;
}

IFACEMETHODIMP ExplorerCommandHandler::GetState(IShellItemArray* items, BOOL /*okToBeSlow*/, EXPCMDSTATE* cmdState)
{
    // Never throw out of a shell extension (spec §10) — any failure here degrades to
    // "hidden", not a crashed Explorer.
    try
    {
        if (!items)
        {
            *cmdState = ECS_HIDDEN;
            return S_OK;
        }

        std::vector<std::wstring> paths = GetSelectedPaths(items);

        // Show as soon as at least one selected item is usable (a supported image, a supported
        // audio file, or a folder) — the rest are silently skipped and reported by the app on
        // launch, rather than hiding the whole command for one bad file. An audio-only selection
        // is a legitimate starting point (spec §4.1): the editor opens with the track loaded and
        // an empty grid.
        bool anySupported = std::any_of(paths.begin(), paths.end(), IsUsableSelection);
        *cmdState = anySupported ? ECS_ENABLED : ECS_HIDDEN;
        return S_OK;
    }
    catch (...)
    {
        *cmdState = ECS_HIDDEN;
        return S_OK;
    }
}

IFACEMETHODIMP ExplorerCommandHandler::GetFlags(EXPCMDFLAGS* flags)
{
    *flags = ECF_DEFAULT;
    return S_OK;
}

IFACEMETHODIMP ExplorerCommandHandler::EnumSubCommands(IEnumExplorerCommand** enumCommands)
{
    *enumCommands = nullptr;
    return E_NOTIMPL;
}

IFACEMETHODIMP ExplorerCommandHandler::Invoke(IShellItemArray* items, IBindCtx* /*bindCtx*/)
{
    try
    {
        if (!items)
        {
            return S_OK;
        }

        std::vector<std::wstring> paths = GetSelectedPaths(items);

        std::vector<std::wstring> supported;
        std::vector<std::wstring> skippedNames;
        for (const std::wstring& pathText : paths)
        {
            std::filesystem::path path(pathText);
            std::error_code ec;
            if (IsSupportedFile(path))
            {
                supported.push_back(pathText);
            }
            else if (std::filesystem::is_directory(path, ec))
            {
                FolderContents contents = ExpandFolder(path);
                if (!contents.supportedFiles.empty())
                {
                    supported.insert(supported.end(), contents.supportedFiles.begin(), contents.supportedFiles.end());
                    skippedNames.insert(skippedNames.end(), contents.skippedFileNames.begin(), contents.skippedFileNames.end());
                }
                else
                {
                    skippedNames.push_back(path.filename().wstring());
                }
            }
            else
            {
                skippedNames.push_back(path.filename().wstring());
            }
        }

        if (supported.empty())
        {
            return S_OK;
        }

        // Both images and audio, mixed in one list (spec §4.1) — the App owns image-vs-audio
        // classification, since files added from inside the app go through the same path.
        std::filesystem::path listFile = WriteLinesFile(supported, L".list");

        std::wstring args = L"--list \"" + listFile.wstring() + L"\"";

        if (!skippedNames.empty())
        {
            std::filesystem::path skippedFile = WriteLinesFile(skippedNames, L".skipped");
            args += L" --skipped \"" + skippedFile.wstring() + L"\"";
        }

        std::wstring packageFamilyName = GetPackageFamilyNameIfPackaged();
        if (!packageFamilyName.empty())
        {
            return ActivatePackagedApp(packageFamilyName, args);
        }

        std::filesystem::path appExe = GetAppExePath();
        HINSTANCE result = ShellExecuteW(nullptr, L"open", appExe.c_str(), args.c_str(), nullptr, SW_SHOW);
        if (reinterpret_cast<INT_PTR>(result) <= 32)
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        return S_OK;
    }
    catch (...)
    {
        return E_FAIL;
    }
}
