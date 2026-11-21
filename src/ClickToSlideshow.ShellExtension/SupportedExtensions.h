#pragma once

#include <algorithm>
#include <array>
#include <cwctype>
#include <string_view>

namespace ClickToSlideshow::ShellExtension
{
    // Combined image + audio set — must be kept in lockstep with
    // SupportedFiles.ImageExtensions/AudioExtensions (ClickToSlideshow.App); SupportedFilesTests
    // fails if they differ.
    // This is the visibility gate for the context menu entry only; the extension does not
    // classify image-vs-audio (spec §4.1) — the App owns that, since files added from inside
    // the app go through the same classification and duplicating the audio table into C++
    // would be a near-duplicate.
    inline constexpr std::array<std::wstring_view, 35> SupportedExtensions =
    {
        L".jpg", L".jpeg", L".jfif", L".png", L".bmp", L".tif", L".tiff", L".jxr", L".wdp",
        L".webp", L".avif", L".heic", L".heif",
        L".cr2", L".cr3", L".crw", L".nef", L".nrw", L".arw", L".srf", L".sr2", L".dng",
        L".orf", L".raf", L".rw2", L".pef", L".srw",
        L".mp3", L".wav", L".flac", L".m4a", L".aac", L".wma", L".ogg", L".opus"
    };

    inline bool EqualsCaseInsensitive(std::wstring_view a, std::wstring_view b)
    {
        return a.size() == b.size() && std::equal(a.begin(), a.end(), b.begin(),
            [](wchar_t left, wchar_t right)
            {
                return std::towlower(left) == std::towlower(right);
            });
    }

    inline bool IsSupportedExtension(std::wstring_view extension)
    {
        for (std::wstring_view candidate : SupportedExtensions)
        {
            if (EqualsCaseInsensitive(candidate, extension))
            {
                return true;
            }
        }

        return false;
    }
}
