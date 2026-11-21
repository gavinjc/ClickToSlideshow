using ClickToSlideshow.Core.Model;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;

namespace ClickToSlideshow.Core.Audio;

/// <summary>
/// Renders a project's audio tracks into one MP3 exactly as long as the show, applying the same
/// Loop/Trim/FitSlides sequencing as the MP4 exporter (via <see cref="FadedSoundtrackRenderer"/>,
/// which also bakes in the spec §4.2 fade-out). Used by the PPTX writer so the embedded audio is
/// always a format PowerPoint (and other viewers) can play, whatever the source tracks were —
/// embedding a FLAC or WMA as-is leaves the recipient with silence if they lack a decoder.
/// </summary>
internal static class SoundtrackRenderer
{
    public static Task RenderMp3Async(
        SlideshowProject project, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var composition = new MediaComposition();

        // MediaComposition refuses to render with zero duration, and background audio tracks
        // don't count towards it — a black clip of the show's length supplies the duration. Its
        // video is discarded by the audio-only profile FadedSoundtrackRenderer renders with below.
        composition.Clips.Add(MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 0, 0, 0), project.Runtime));

        return FadedSoundtrackRenderer.RenderWithFadedAudioAsync(
            project, composition, outputPath, MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High),
            progress, cancellationToken);
    }
}
