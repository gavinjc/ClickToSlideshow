using System.Runtime.InteropServices;
using Windows.Storage;

namespace ClickToSlideshow.Core;

/// <summary>
/// Where the app keeps its settings, logs and temp files. Packaged, these are the MSIX package's
/// own LocalState/TempState folders, which Windows deletes on uninstall. A plain
/// %LOCALAPPDATA% path is NOT redirected into the package for this app (verified: settings.json
/// landed in the real profile folder and survived as leftover garbage), so it has to be asked for
/// explicitly. Unpackaged runs (dotnet run, CLI, tests) have no package identity and
/// <see cref="ApplicationData.Current"/> throws there, hence the fallback to ordinary folders.
/// </summary>
public static class AppDataFolders
{
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, nint packageFullName);

    // The roots below are computed at type initialisation, so if ApplicationData.Current ever
    // threw while packaged, AppLog would fail before it could record why. Accepted rather than
    // guarded: it worked in the installed package and the failure would be storage-level.
    private static readonly bool IsPackaged = HasPackageIdentity();

    public static readonly string LocalRoot = IsPackaged
        ? ApplicationData.Current.LocalFolder.Path
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClickTo Slideshow");

    public static readonly string TempRoot = IsPackaged
        ? ApplicationData.Current.TemporaryFolder.Path
        : Path.Combine(Path.GetTempPath(), "ClickToSlideshow");

    // A null buffer of length 0 makes the call report ERROR_INSUFFICIENT_BUFFER when a package
    // name exists and APPMODEL_ERROR_NO_PACKAGE when it doesn't — no allocation needed to ask.
    private static bool HasPackageIdentity()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, nint.Zero) != AppModelErrorNoPackage;
    }
}
