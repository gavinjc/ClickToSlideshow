namespace ClickToSlideshow.App.ViewModels;

// "Skipped notes.txt" for one file, "2 files skipped: a.txt, b.png" for more (Bureau.Common
// app-conventions.md → Message bar): a resw string for one and another for many, never "(s)".
// Takes the strings rather than a ResourceLoader, so tests can run it against the resw itself
// outside a WinUI-hosted process.
public static class SkippedFilesText
{
    // oneFormat takes the name; manyFormat the count and the comma-separated names.
    public static string Format(IReadOnlyList<string> names, string oneFormat, string manyFormat) =>
        names.Count == 1
            ? string.Format(oneFormat, names[0])
            : string.Format(manyFormat, names.Count, string.Join(", ", names));
}
