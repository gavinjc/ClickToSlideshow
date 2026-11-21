namespace ClickToSlideshow.App.ViewModels;

// One key per kind of message (Bureau.Common app-conventions.md → Message bar), so a second
// report of the same kind replaces the first row rather than piling up.
public static class MessageKeys
{
    public const string Error = "error";
    public const string Saved = "saved";
    public const string SkippedFiles = "skipped";
    public const string UnreadableFiles = "unreadable";
    public const string MissingAudio = "missing-audio";
    public const string UnreadableAudio = "unreadable-audio";
}
