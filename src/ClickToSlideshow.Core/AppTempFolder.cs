namespace ClickToSlideshow.Core;

// The one place that names the app's temp folder. Renders, the PPTX soundtrack and the waveform
// extractor delete their own files when done; only the decoded-Ogg cache (Root\audio) outlives
// a session, and OggAudioTranscoder.PruneStaleCache trims it at launch. Nothing sweeps Root
// itself, so a crash mid-render can leave one orphaned file here.
internal static class AppTempFolder
{
    public static readonly string Root = AppDataFolders.TempRoot;

    // Creates Root so callers can write to the returned path straight away.
    public static string NewFilePath(string extension)
    {
        Directory.CreateDirectory(Root);
        return Path.Combine(Root, $"{Guid.NewGuid()}{extension}");
    }
}
