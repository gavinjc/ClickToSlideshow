namespace ClickToSlideshow.App.ViewModels;

// The photo and music types the app takes, kept in step with the shell extension's
// SupportedExtensions.h (SupportedFilesTests checks). Apart from MainViewModel so ExpandedSelection
// doesn't depend on the view model, and because its static ResourceLoader is unreliable outside a
// WinUI-hosted process: in the test host its construction failed on first touch from one test
// class while others built a MainViewModel fine, and GetString always fails there.
public static class SupportedFiles
{
    // §4.4's in-box + free-Store-codec-pack + HEIF tiers, plus camera RAW. Spec §5 predicted RAW
    // would break the thumbnail budget for lack of a usable embedded thumbnail; measured against
    // Canon/Nikon/Sony files, the embedded preview loads in ~15-40 ms, so it shares the JPEG
    // concurrency limit. .gif is deliberately excluded per spec §4.4 (silently
    // taking frame 0 is worse than not offering it). HEIC/WEBP/AVIF/RAW are included even though
    // their codecs may not be installed — MainViewModel.DescribeLoadError turns that into an
    // actionable per-tile message rather than a load failure, so there's no reason to pre-filter
    // based on what happens to be installed on the current machine.
    public static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".tif", ".tiff", ".jxr", ".wdp", ".webp", ".avif", ".heic", ".heif",
            .. RawImageExtensions.All];

    // §4.4 lists .m4a/.aac/.wma as needing a MediaTranscoder transcode-on-import pass before
    // BackgroundAudioTrack (the muxer's API) would accept them — verified against real files that
    // this isn't true: BackgroundAudioTrack.CreateFromFileAsync already accepts all three directly
    // via Media Foundation's in-box AAC/WMA decoders, no pre-transcode needed. .ogg/.opus genuinely
    // need it: no Media Foundation decoder exists for them (BackgroundAudioTrack fails with
    // MF_E_UNSUPPORTED_BYTESTREAM_TYPE, even with Microsoft's Web Media Extensions installed), so
    // OggAudioTranscoder decodes them in managed code and AudioTrack.PlayablePath points at the result.
    public static readonly string[] AudioExtensions =
        [".mp3", ".wav", ".flac", ".m4a", ".aac", ".wma", ".ogg", ".opus"];

    public static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsAudio(string path) =>
        AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
