using ClickToSlideshow.Core.Audio;
using ClickToSlideshow.Core.Model;
using ClickToSlideshow.Core.Video;

namespace ClickToSlideshow.Core;

/// <summary>
/// Top-level orchestrator: renders a <see cref="SlideshowProject"/> to a finished MP4. Skips
/// the audio-mux pass entirely when there are no audio tracks, rather than paying its re-encode
/// cost for nothing.
/// </summary>
public static class SlideshowRenderer
{
    public static async Task RenderAsync(
        SlideshowProject project,
        string outputPath,
        IAudioMuxer? audioMuxer = null,
        IProgress<RenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        SlideshowProject effective = EffectiveProject(project);
        IProgress<double>? frameProgress = ForPhase(progress, RenderPhase.Frames);

        try
        {
            if (effective.AudioTracks.Count == 0)
            {
                await Mp4VideoRenderer.RenderVideoOnlyAsync(effective, outputPath, frameProgress, cancellationToken);
                return;
            }

            string videoOnlyPath = AppTempFolder.NewFilePath(".mp4");
            try
            {
                await Mp4VideoRenderer.RenderVideoOnlyAsync(effective, videoOnlyPath, frameProgress, cancellationToken);

                IAudioMuxer muxer = audioMuxer ?? new MediaCompositionAudioMuxer();
                await muxer.MuxAsync(
                    videoOnlyPath, effective, outputPath, ForPhase(progress, RenderPhase.Audio), cancellationToken);
            }
            finally
            {
                File.Delete(videoOnlyPath);
            }
        }
        catch
        {
            // Cancelled or failed mid-mux: outputPath may have been created/partially written by
            // the muxer (spec §5: "must clean up partial output"). The video-only path above
            // cleans up after itself via Mp4VideoRenderer's own guard, so this only ever has real
            // work to do in the audio-mux branch — but is harmless (File.Delete no-ops on a
            // missing file) to run unconditionally.
            // Best-effort: a read-only attribute or a brief AV/indexer lock on the file we just
            // wrote throws UnauthorizedAccessException, not IOException — caught broadly so a
            // failed cleanup attempt never replaces the real exception below with its own.
            try
            {
                File.Delete(outputPath);
            }
            catch (Exception)
            {
            }

            throw;
        }
    }

    internal static IProgress<double>? ForPhase(IProgress<RenderProgress>? progress, RenderPhase phase) =>
        progress is null ? null : new Progress<double>(fraction => progress.Report(new RenderProgress(phase, fraction)));

    /// <summary>
    /// The project actually used for rendering or playback: always an independent clone of
    /// <paramref name="project"/> — never the caller's own <see cref="SlideshowProject"/>
    /// instance, since its <see cref="SlideshowProject.Slides"/>/<see cref="SlideshowProject.AudioTracks"/>
    /// lists may belong to a still-open editor that keeps mutating them in place (e.g.
    /// <c>MainViewModel</c>'s <c>RenumberSlides</c>/<c>RenumberAudioTracks</c>) while this render
    /// or a live playback session is in flight — with FitSlides duration scaling applied if the
    /// project's <see cref="SlideshowProject.AudioMatch"/> requires it (spec §4.2). Shared by the
    /// MP4 exporter and the fullscreen player so both compute FitSlides identically.
    /// </summary>
    public static SlideshowProject EffectiveProject(SlideshowProject project)
    {
        return project.AudioMatch == AudioMatch.FitSlides && project.AudioTracks.Count > 0
            ? WithSlideDurationsFitToAudio(project)
            : Clone(project);
    }

    /// <summary>
    /// An independent clone of <paramref name="project"/> with no FitSlides duration scaling
    /// applied — unlike <see cref="EffectiveProject"/>, which scales durations for rendering,
    /// this is for callers (project-JSON save) that need the user's own stored numbers to
    /// round-trip exactly (spec §4.2: "FitSlides ... never rewrites the stored per-slide values").
    /// </summary>
    public static SlideshowProject Clone(SlideshowProject project) =>
        CloneWithSlides(project, CloneSlides(project.Slides), project.DefaultSlideMs);

    /// <summary>
    /// FitSlides scales every slide duration proportionally so the show matches the audio, and
    /// never rewrites the caller's stored values (spec §4.2) — the clone this returns is the
    /// only place the scaled values live.
    /// </summary>
    private static SlideshowProject WithSlideDurationsFitToAudio(SlideshowProject project)
    {
        long totalAudioMs = project.AudioTracks.Sum(track => (long)track.DurationMs);
        long currentSlideMs = project.Slides.Sum(slide => (long)(slide.DurationMs ?? project.DefaultSlideMs));
        long transitionMs = (long)(project.Slides.Count - 1) * project.TransitionMs;
        long targetSlideMs = totalAudioMs - transitionMs;

        if (currentSlideMs <= 0 || targetSlideMs <= 0)
        {
            return Clone(project);
        }

        double scale = (double)targetSlideMs / currentSlideMs;

        List<Slide> scaledSlides = CloneSlides(
            project.Slides, slide => (int)Math.Round((slide.DurationMs ?? project.DefaultSlideMs) * scale));

        return CloneWithSlides(project, scaledSlides, (int)Math.Round(project.DefaultSlideMs * scale));
    }

    private static List<Slide> CloneSlides(IEnumerable<Slide> slides) => CloneSlides(slides, slide => slide.DurationMs);

    // durationMs takes the whole Slide (not just its DurationMs) so FitSlides scaling can fall
    // back to the project's DefaultSlideMs for a slide with no per-slide override.
    private static List<Slide> CloneSlides(IEnumerable<Slide> slides, Func<Slide, int?> durationMs) =>
        slides.Select(slide => new Slide
        {
            SourcePath = slide.SourcePath,
            DurationMs = durationMs(slide),
            RotationDegrees = slide.RotationDegrees,
            TransitionIn = slide.TransitionIn,
        }).ToList();

    private static SlideshowProject CloneWithSlides(SlideshowProject project, List<Slide> slides, int defaultSlideMs) => new()
    {
        FormatVersion = project.FormatVersion,
        Slides = slides,
        AudioTracks = project.AudioTracks.ToList(),
        DefaultSlideMs = defaultSlideMs,
        TransitionKind = project.TransitionKind,
        TransitionMs = project.TransitionMs,
        Resolution = project.Resolution,
        FitMode = project.FitMode,
        BackgroundColor = project.BackgroundColor,
        AudioMatch = project.AudioMatch,
    };
}
