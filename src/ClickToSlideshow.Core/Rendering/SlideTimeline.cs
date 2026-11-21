using ClickToSlideshow.Core.Model;

namespace ClickToSlideshow.Core.Rendering;

/// <summary>
/// Resolved position within a slideshow at a given time: which slide is showing, and — if a
/// transition is in progress — which slide is coming in next and how far through it we are.
/// </summary>
public readonly record struct SlidePosition(int SlideIndex, int? NextSlideIndex, double TransitionProgress);

/// <summary>
/// Maps a point in time to a <see cref="SlidePosition"/>. Pure function of the timeline, per
/// spec §4.5 — used identically by export and (from M3) live playback so they cannot drift.
///
/// Per-slide <see cref="Slide.TransitionIn"/> overrides change which transition renders at a
/// boundary but not the boundary's duration — every boundary still consumes the project's
/// <see cref="SlideshowProject.TransitionMs"/>. A Cut override therefore still occupies a
/// transition window; <see cref="Transitions.CutTransition"/> just holds the outgoing slide
/// for it instead of blending. Variable-length windows (a true zero-duration Cut) are deferred
/// until M2 needs them — the M1 CLI never sets TransitionIn, so this never fires today.
/// </summary>
public static class SlideTimeline
{
    public static SlidePosition Locate(SlideshowProject project, TimeSpan t)
    {
        if (project.Slides.Count == 0)
        {
            throw new InvalidOperationException("Cannot locate a position in an empty slideshow.");
        }

        double remainingMs = Math.Max(0, t.TotalMilliseconds);

        for (int i = 0; i < project.Slides.Count; i++)
        {
            int slideMs = project.Slides[i].DurationMs ?? project.DefaultSlideMs;

            if (remainingMs < slideMs)
            {
                return new SlidePosition(i, null, 0);
            }
            remainingMs -= slideMs;

            bool hasNextSlide = i + 1 < project.Slides.Count;
            if (hasNextSlide && project.TransitionMs > 0 && remainingMs < project.TransitionMs)
            {
                double progress = remainingMs / project.TransitionMs;
                return new SlidePosition(i, i + 1, progress);
            }
            remainingMs -= project.TransitionMs;
        }

        return new SlidePosition(project.Slides.Count - 1, null, 0);
    }

    /// <summary>
    /// The inverse of <see cref="Locate"/>: the point in time at which <paramref name="slideIndex"/>
    /// begins. Used by the player's ←/→ slide-stepping (M3) to jump to a slide's start.
    /// </summary>
    public static TimeSpan StartTime(SlideshowProject project, int slideIndex)
    {
        double ms = 0;
        for (int i = 0; i < slideIndex; i++)
        {
            ms += project.Slides[i].DurationMs ?? project.DefaultSlideMs;
            ms += project.TransitionMs;
        }

        return TimeSpan.FromMilliseconds(ms);
    }
}
