using ClickToSlideshow.Core.Model;

namespace ClickToSlideshow.Core.Tests;

// EffectiveProject's entire reason for existing is that it must never hand back the caller's own
// Slides/AudioTracks list instances — MainViewModel mutates those in place (Clear()+AddRange())
// while a render or a live playback session can be reading them on another thread. These tests
// exercise that guarantee directly, for both the FitSlides and non-FitSlides branches.
public class SlideshowRendererTests
{
    private static SlideshowProject TwoSlideProject(AudioMatch audioMatch) => new()
    {
        Slides =
        {
            new Slide { SourcePath = "a.jpg", DurationMs = 4000 },
            new Slide { SourcePath = "b.jpg", DurationMs = 4000 },
        },
        AudioTracks = { new AudioTrack { SourcePath = "track.mp3", DurationMs = 6000 } },
        TransitionMs = 1000,
        AudioMatch = audioMatch,
    };

    [Fact]
    public void EffectiveProject_NonFitSlides_MutatingSourceSlidesAfterward_LeavesResultUnaffected()
    {
        SlideshowProject project = TwoSlideProject(AudioMatch.Loop);

        SlideshowProject effective = SlideshowRenderer.EffectiveProject(project);
        project.Slides.Clear();
        project.AudioTracks.Clear();

        Assert.Equal(2, effective.Slides.Count);
        Assert.Single(effective.AudioTracks);
    }

    [Fact]
    public void EffectiveProject_FitSlides_MutatingSourceSlidesAfterward_LeavesResultUnaffected()
    {
        SlideshowProject project = TwoSlideProject(AudioMatch.FitSlides);

        SlideshowProject effective = SlideshowRenderer.EffectiveProject(project);
        project.Slides.Clear();
        project.AudioTracks.Clear();

        Assert.Equal(2, effective.Slides.Count);
        Assert.Single(effective.AudioTracks);
    }

    [Fact]
    public void EffectiveProject_NonFitSlides_ReturnsDifferentSlideInstances()
    {
        SlideshowProject project = TwoSlideProject(AudioMatch.Loop);
        Slide original = project.Slides[0];

        SlideshowProject effective = SlideshowRenderer.EffectiveProject(project);

        Assert.NotSame(original, effective.Slides[0]);
    }

    [Fact]
    public void EffectiveProject_FitSlides_ScalesSlideDurationsToMatchAudioLength()
    {
        // 2 slides x 4000ms + 1 transition x 1000ms = 9000ms show against a 6000ms track ->
        // target slide time = 6000 - 1000 = 5000ms, scale = 5000/8000 = 0.625.
        SlideshowProject project = TwoSlideProject(AudioMatch.FitSlides);

        SlideshowProject effective = SlideshowRenderer.EffectiveProject(project);

        Assert.Equal(2500, effective.Slides[0].DurationMs);
        Assert.Equal(2500, effective.Slides[1].DurationMs);
    }

    [Fact]
    public void EffectiveProject_FitSlides_NeverRewritesTheCallersOwnSlideDurations()
    {
        SlideshowProject project = TwoSlideProject(AudioMatch.FitSlides);

        SlideshowRenderer.EffectiveProject(project);

        Assert.Equal(4000, project.Slides[0].DurationMs);
        Assert.Equal(4000, project.Slides[1].DurationMs);
    }

    [Fact]
    public void Clone_ReturnsIndependentClone()
    {
        SlideshowProject project = TwoSlideProject(AudioMatch.Loop);
        Slide original = project.Slides[0];

        SlideshowProject clone = SlideshowRenderer.Clone(project);
        project.Slides.Clear();

        Assert.NotSame(original, clone.Slides[0]);
        Assert.Equal(2, clone.Slides.Count);
    }

    [Fact]
    public void Clone_UnlikeEffectiveProject_NeverAppliesFitSlidesScaling()
    {
        // Same 9000ms show / 6000ms track FitSlides math as EffectiveProject_FitSlides_
        // ScalesSlideDurationsToMatchAudioLength above, which scales to 2500ms — Clone must
        // ignore AudioMatch entirely and keep the caller's stored 4000ms durations.
        SlideshowProject project = TwoSlideProject(AudioMatch.FitSlides);

        SlideshowProject clone = SlideshowRenderer.Clone(project);

        Assert.Equal(4000, clone.Slides[0].DurationMs);
        Assert.Equal(4000, clone.Slides[1].DurationMs);
    }
}
