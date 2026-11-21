using ClickToSlideshow.Core.Model;

namespace ClickToSlideshow.Core.Audio;

/// <summary>
/// Attaches <see cref="AudioTrack"/>s to an already-rendered video-only MP4. Kept behind an
/// interface (decisions.md Decision 4) so a single-pass implementation can replace the
/// MediaComposition-based muxer later without touching callers, if the extra re-encode's
/// generation loss proves visible in practice.
/// </summary>
public interface IAudioMuxer
{
    Task MuxAsync(
        string videoOnlyPath,
        SlideshowProject project,
        string outputPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
