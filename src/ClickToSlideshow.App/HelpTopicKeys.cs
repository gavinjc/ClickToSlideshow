namespace ClickToSlideshow.App;

// Kept out of MainWindow so the tests can read it: touching MainWindow runs its static
// ResourceLoader, which fails outside a packaged or WinUI-hosted process.
public static class HelpTopicKeys
{
    // Each key needs "{key}Title" and "{key}Body" in Strings/en-us/Resources.resw.
    public static readonly IReadOnlyList<string> InOrder =
    [
        "HelpGettingStarted",
        "HelpSupportedFiles",
        "HelpSkippedFiles",
        "HelpArrangingSlides",
        "HelpDurations",
        "HelpTransitions",
        "HelpMusic",
        "HelpRuntimeBar",
        "HelpOptions",
        "HelpPlaying",
        "HelpSaving",
        "HelpReopening",
        "HelpKeyboardShortcuts",
        "HelpPrivacy",
    ];
}
