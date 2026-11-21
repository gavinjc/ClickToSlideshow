using ClickToSlideshow.Core.Model;

namespace ClickToSlideshow.Core.Tests;

public class SlideshowProjectRuntimeTests
{
    [Fact]
    public void Runtime_IsEmpty_WhenNoSlides()
    {
        var project = new SlideshowProject();

        Assert.Equal(TimeSpan.Zero, project.Runtime);
    }

    [Fact]
    public void Runtime_OneSlide_HasNoTransitionTime()
    {
        var project = new SlideshowProject
        {
            Slides = { new Slide { SourcePath = "a.jpg", DurationMs = 4000 } },
            TransitionMs = 1000,
        };

        Assert.Equal(TimeSpan.FromMilliseconds(4000), project.Runtime);
    }

    [Fact]
    public void Runtime_AddsTransitionTimeBetweenSlides_NotInsteadOfSlideTime()
    {
        // Spec §4.2: Runtime = Σ(slide.DurationMs) + (slideCount − 1) × TransitionMs — a slide
        // is fully visible for its stated duration; transitions happen between slides, not
        // carved out of it. This is the exact bug this test exists to catch: a "carve out of
        // slide time" implementation would produce 9000ms here instead of 11000ms.
        var project = new SlideshowProject
        {
            Slides =
            {
                new Slide { SourcePath = "a.jpg", DurationMs = 4000 },
                new Slide { SourcePath = "b.jpg", DurationMs = 3000 },
                new Slide { SourcePath = "c.jpg", DurationMs = 3000 },
            },
            TransitionMs = 500,
        };

        Assert.Equal(TimeSpan.FromMilliseconds(4000 + 3000 + 3000 + 2 * 500), project.Runtime);
    }

    [Fact]
    public void Runtime_UsesDefaultSlideMs_WhenSlideHasNoOverride()
    {
        var project = new SlideshowProject
        {
            DefaultSlideMs = 5000,
            Slides = { new Slide { SourcePath = "a.jpg" } },
        };

        Assert.Equal(TimeSpan.FromMilliseconds(5000), project.Runtime);
    }
}
