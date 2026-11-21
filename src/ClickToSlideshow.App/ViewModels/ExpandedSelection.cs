using ClickToSlideshow.App.Diagnostics;

namespace ClickToSlideshow.App.ViewModels;

// What a set of dropped files and folders comes to: the paths to add, the names of files of a type
// the app doesn't take, and the names of photos or music it couldn't reach. The last two are
// apart because only an unreachable file holds back the rating prompt (app-conventions.md →
// Rating prompt).
public sealed record ExpandedSelection(
    IReadOnlyList<string> SupportedPaths, IReadOnlyList<string> SkippedNames, IReadOnlyList<string> UnreadableNames)
{
    // The right-click menu's rules (ExplorerCommand.cpp's Invoke and ExpandFolder), so a drop and
    // the menu agree (app-conventions.md → Add button): a folder gives the supported files
    // directly inside it and its other files are skipped, subfolders are ignored rather than
    // listed, and a folder with nothing usable directly inside is skipped by its own name.
    // A photo or music file that isn't on disk (no path, so the caller passes its name) is
    // unreadable rather than unsupported. Repeats are kept:
    // Slideshow is the family's one app that allows them.
    public static ExpandedSelection Of(IEnumerable<string> paths)
    {
        List<string> supported = [];
        List<string> skippedNames = [];
        List<string> unreadableNames = [];
        foreach (string path in paths)
        {
            if (IsSupportedFileOnDisk(path))
            {
                supported.Add(path);
            }
            else if (Path.IsPathFullyQualified(path) && Directory.Exists(path))
            {
                List<string> files = FilesDirectlyInside(path);
                List<string> usable = files.Where(IsSupportedFileOnDisk).ToList();
                if (usable.Count > 0)
                {
                    supported.AddRange(usable);
                    skippedNames.AddRange(files.Except(usable).Select(DisplayName));
                }
                else
                {
                    skippedNames.Add(DisplayName(path));
                }
            }
            else if (IsSupportedType(path))
            {
                unreadableNames.Add(DisplayName(path));
            }
            else
            {
                skippedNames.Add(DisplayName(path));
            }
        }

        return new ExpandedSelection(supported, skippedNames, unreadableNames);
    }

    private static bool IsSupportedType(string path) => SupportedFiles.IsImage(path) || SupportedFiles.IsAudio(path);

    // Fully qualified so a bare name can't resolve against the app's working folder.
    private static bool IsSupportedFileOnDisk(string path) =>
        IsSupportedType(path) && Path.IsPathFullyQualified(path) && File.Exists(path);

    // A folder that can't be listed counts as empty, as the shell extension's error_code
    // iteration does, so it is reported as skipped rather than failing the whole drop.
    private static List<string> FilesDirectlyInside(string folderPath)
    {
        try
        {
            return Directory.EnumerateFiles(folderPath).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.LogWarning("ExpandedSelection", $"{folderPath}: {ex.Message}");
            return [];
        }
    }

    // A drive root has no file name, so it is listed by its path.
    private static string DisplayName(string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.Length > 0 ? name : path;
    }
}
