namespace ClickToSlideshow.App.ViewModels;

// Camera RAW formats Windows decodes only once Microsoft's free "Raw Image Extension" is installed
// from the Store — the same optional-WIC-codec situation as HEIC, so they share its missing-codec
// handling (CodecFixLinks). Kept apart from MainViewModel so unit tests can reach it without that
// class's ResourceLoader static initialiser. Also listed in ShellExtension/SupportedExtensions.h.
internal static class RawImageExtensions
{
    public static readonly string[] All =
    [
        ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".dng", ".orf", ".raf", ".rw2", ".pef", ".srw",
    ];
}
