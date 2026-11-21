using ClickToSlideshow.Core.Model;
using ClickToSlideshow.Core.Video;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace ClickToSlideshow.Core.Audio;

/// <summary>
/// Second pass of the two-pass pipeline (decisions.md Decision 4): muxes <see cref="AudioTrack"/>s
/// onto an already-rendered video-only MP4 via <see cref="MediaComposition"/>, used only as a
/// muxer here — never as the frame source (spec §4.5). <see cref="AddBackgroundAudioAsync"/>
/// places tracks sequentially via each <see cref="BackgroundAudioTrack.Delay"/> —
/// <see cref="MediaComposition.BackgroundAudioTracks"/> does not auto-concatenate them, confirmed
/// by reading the real member list (no InsertionTimeSpan or auto-sequencing exists on the type;
/// <c>Delay</c> is the only positioning property it has).
///
/// The spec's "fade out over the final 2s" (§4.2) isn't done here: <see cref="MuxAsync"/> hands
/// the built (video-only) composition straight to
/// <see cref="FadedSoundtrackRenderer.RenderWithFadedAudioAsync"/>, which sequences the tracks via
/// this class's own <see cref="AddBackgroundAudioAsync"/>, bakes the fade into that sequenced
/// audio's raw PCM, attaches the result, and renders — so this class only ever supplies the
/// sequencing step, never the fade or the final render.
/// </summary>
public sealed class MediaCompositionAudioMuxer : IAudioMuxer
{
    public async Task MuxAsync(
        string videoOnlyPath,
        SlideshowProject project,
        string outputPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (project.AudioTracks.Count == 0)
        {
            throw new InvalidOperationException("Nothing to mux — project has no audio tracks.");
        }

        StorageFile videoFile = await StorageFile.GetFileFromPathAsync(videoOnlyPath);
        MediaClip videoClip = await MediaClip.CreateFromFileAsync(videoFile);

        var composition = new MediaComposition();
        composition.Clips.Add(videoClip);

        // Sized from the video pass's own output rather than worked out again from the project,
        // so the two passes can't disagree (for Original, a source photo could change in between).
        VideoEncodingProperties videoProperties = videoClip.GetVideoEncodingProperties();
        MediaEncodingProfile profile = Mp4Profiles.ForFrameSize(videoProperties.Width, videoProperties.Height);

        await FadedSoundtrackRenderer.RenderWithFadedAudioAsync(
            project, composition, outputPath, profile, progress, cancellationToken);
    }

    /// <summary>
    /// Loop/Trim/FitSlides per spec §4.2. FitSlides needs no special casing here — by the time
    /// rendering reaches this pass, <see cref="SlideshowRenderer"/> has already scaled slide
    /// durations so the show and the audio are the same length, so it behaves exactly like
    /// Trim's matched-length case. The composition's own duration (governed by the video clip)
    /// truncates any audio placed past the runtime boundary, so "Trim at runtime" needs no
    /// explicit trimming code either — only Loop's shorter-than-show case needs extra tracks
    /// added at all.
    /// </summary>
    internal static Task AddBackgroundAudioAsync(
        MediaComposition composition, SlideshowProject project, CancellationToken cancellationToken)
    {
        long runtimeMs = (long)project.Runtime.TotalMilliseconds;
        long totalTrackMs = project.AudioTracks.Sum(track => (long)track.DurationMs);

        return project.AudioMatch == AudioMatch.Loop && totalTrackMs < runtimeMs
            ? AddLoopedToFillAsync(composition, project.AudioTracks, runtimeMs, cancellationToken)
            : AddSequentiallyAsync(composition, project.AudioTracks, cancellationToken);
    }

    private static async Task AddSequentiallyAsync(
        MediaComposition composition, IReadOnlyList<AudioTrack> tracks, CancellationToken cancellationToken)
    {
        TimeSpan cursor = TimeSpan.Zero;
        foreach (AudioTrack track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StorageFile file = await StorageFile.GetFileFromPathAsync(track.PlayablePath);
            BackgroundAudioTrack backgroundTrack = await BackgroundAudioTrack.CreateFromFileAsync(file);
            backgroundTrack.Delay = cursor;
            composition.BackgroundAudioTracks.Add(backgroundTrack);

            cursor += TimeSpan.FromMilliseconds(track.DurationMs);
        }
    }

    private static async Task AddLoopedToFillAsync(
        MediaComposition composition, IReadOnlyList<AudioTrack> tracks, long runtimeMs, CancellationToken cancellationToken)
    {
        TimeSpan cursor = TimeSpan.Zero;
        while (cursor.TotalMilliseconds < runtimeMs)
        {
            foreach (AudioTrack track in tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StorageFile file = await StorageFile.GetFileFromPathAsync(track.PlayablePath);
                BackgroundAudioTrack backgroundTrack = await BackgroundAudioTrack.CreateFromFileAsync(file);
                backgroundTrack.Delay = cursor;
                composition.BackgroundAudioTracks.Add(backgroundTrack);

                cursor += TimeSpan.FromMilliseconds(track.DurationMs);
                if (cursor.TotalMilliseconds >= runtimeMs)
                {
                    break;
                }
            }
        }
    }
}
