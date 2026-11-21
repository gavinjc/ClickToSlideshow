using ClickToSlideshow.Core.Model;
using ClickToSlideshow.Core.Rendering;

namespace ClickToSlideshow.Core.Tests;

public class SlideTimelineTests
{
    private static SlideshowProject ThreeSlideProject() => new()
    {
        Slides =
        {
            new Slide { SourcePath = "a.jpg", DurationMs = 4000 },
            new Slide { SourcePath = "b.jpg", DurationMs = 4000 },
            new Slide { SourcePath = "c.jpg", DurationMs = 4000 },
        },
        TransitionMs = 1000,
    };

    [Fact]
    public void Locate_AtStart_IsFirstSlideWithNoTransition()
    {
        SlidePosition position = SlideTimeline.Locate(ThreeSlideProject(), TimeSpan.Zero);

        Assert.Equal(0, position.SlideIndex);
        Assert.Null(position.NextSlideIndex);
    }

    [Fact]
    public void Locate_MidwaySlide_IsSameSlideNoTransition()
    {
        SlidePosition position = SlideTimeline.Locate(ThreeSlideProject(), TimeSpan.FromMilliseconds(2000));

        Assert.Equal(0, position.SlideIndex);
        Assert.Null(position.NextSlideIndex);
    }

    [Fact]
    public void Locate_JustAfterSlideDuration_EntersTransitionAtZeroProgress()
    {
        // Slide 0 shown fully for 4000ms, then the transition window starts.
        SlidePosition position = SlideTimeline.Locate(ThreeSlideProject(), TimeSpan.FromMilliseconds(4000));

        Assert.Equal(0, position.SlideIndex);
        Assert.Equal(1, position.NextSlideIndex);
        Assert.Equal(0, position.TransitionProgress, precision: 3);
    }

    [Fact]
    public void Locate_MidTransition_ReportsHalfProgress()
    {
        SlidePosition position = SlideTimeline.Locate(ThreeSlideProject(), TimeSpan.FromMilliseconds(4500));

        Assert.Equal(0, position.SlideIndex);
        Assert.Equal(1, position.NextSlideIndex);
        Assert.Equal(0.5, position.TransitionProgress, precision: 3);
    }

    [Fact]
    public void Locate_JustAfterTransitionWindow_IsSecondSlideNoTransition()
    {
        SlidePosition position = SlideTimeline.Locate(ThreeSlideProject(), TimeSpan.FromMilliseconds(5000));

        Assert.Equal(1, position.SlideIndex);
        Assert.Null(position.NextSlideIndex);
    }

    [Fact]
    public void Locate_AfterFinalSlideEnds_ClampsToLastSlide()
    {
        SlidePosition position = SlideTimeline.Locate(ThreeSlideProject(), TimeSpan.FromMilliseconds(999_999));

        Assert.Equal(2, position.SlideIndex);
        Assert.Null(position.NextSlideIndex);
    }

    [Fact]
    public void Locate_LastSlide_NeverProducesATransition_ThereIsNoSlideAfterIt()
    {
        SlidePosition position = SlideTimeline.Locate(ThreeSlideProject(), TimeSpan.FromMilliseconds(10_000));

        Assert.Equal(2, position.SlideIndex);
        Assert.Null(position.NextSlideIndex);
    }

    [Fact]
    public void Locate_EmptyProject_Throws()
    {
        var project = new SlideshowProject();

        Assert.Throws<InvalidOperationException>(() => SlideTimeline.Locate(project, TimeSpan.Zero));
    }

    [Fact]
    public void StartTime_FirstSlide_IsZero()
    {
        TimeSpan start = SlideTimeline.StartTime(ThreeSlideProject(), 0);

        Assert.Equal(TimeSpan.Zero, start);
    }

    [Fact]
    public void StartTime_LaterSlide_SumsPrecedingDurationsAndTransitions()
    {
        // Slide 2 starts after slide 0's 4000ms + one 1000ms transition + slide 1's 4000ms + another transition.
        TimeSpan start = SlideTimeline.StartTime(ThreeSlideProject(), 2);

        Assert.Equal(TimeSpan.FromMilliseconds(10_000), start);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void StartTime_IsTheInverseOfLocate(int slideIndex)
    {
        SlideshowProject project = ThreeSlideProject();

        SlidePosition position = SlideTimeline.Locate(project, SlideTimeline.StartTime(project, slideIndex));

        Assert.Equal(slideIndex, position.SlideIndex);
        Assert.Null(position.NextSlideIndex);
    }
}
