using ClickToSlideshow.App.Converters;
using ClickToSlideshow.App.ViewModels;
using Microsoft.UI.Xaml;

namespace ClickToSlideshow.App.Tests;

public class PlaybackProgressToIndicatorMarginConverterTests
{
    private static double LeftMarginAt(double progress)
    {
        var converter = new PlaybackProgressToIndicatorMarginConverter();
        return ((Thickness)converter.Convert(progress, typeof(Thickness), null!, "")).Left;
    }

    [Fact]
    public void Convert_AtTheEndOfTheTrack_PutsTheIndicatorFlushWithTheWaveformsRightEdge()
    {
        // Indicator is 2 px wide: its left edge must stop 2 px short of the edge, or it'd hang off
        // the end of the waveform.
        Assert.Equal(AudioTrackItem.WaveformWidth - 2.0, LeftMarginAt(1.0));
    }

    [Fact]
    public void Convert_AtTheStartOfTheTrack_PutsTheIndicatorAtTheLeftEdge()
    {
        Assert.Equal(0.0, LeftMarginAt(0.0));
    }

    [Fact]
    public void Convert_HalfwayThroughTheTrack_IsHalfwayAcrossTheUsableWidth()
    {
        Assert.Equal((AudioTrackItem.WaveformWidth - 2.0) / 2, LeftMarginAt(0.5));
    }
}
