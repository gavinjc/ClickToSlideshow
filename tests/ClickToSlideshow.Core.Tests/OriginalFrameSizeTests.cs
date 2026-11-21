using ClickToSlideshow.Core.Video;

namespace ClickToSlideshow.Core.Tests;

public class OriginalFrameSizeTests
{
    [Fact]
    public void Choose_PicksTheLargestSlideByArea_NotByWidth()
    {
        // The wide one is wider but smaller: 3000x1000 is 3.0 MP, 2000x2000 is 4.0 MP.
        (int, int)? size = OriginalFrameSize.Choose(
        [
            new SlidePixelSize(3000, 1000, 0),
            new SlidePixelSize(2000, 2000, 0),
            new SlidePixelSize(800, 600, 0),
        ]);

        Assert.Equal((2000, 2000), size);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void Choose_QuarterTurnedSlide_SwapsWidthAndHeight(int rotationDegrees)
    {
        (int, int)? size = OriginalFrameSize.Choose([new SlidePixelSize(3000, 2000, rotationDegrees)]);

        Assert.Equal((2000, 3000), size);
    }

    [Fact]
    public void Choose_HalfTurnedSlide_KeepsItsShape()
    {
        (int, int)? size = OriginalFrameSize.Choose([new SlidePixelSize(3000, 2000, 180)]);

        Assert.Equal((3000, 2000), size);
    }

    [Fact]
    public void Choose_OddSourceSize_RoundsDownToEven()
    {
        (int, int)? size = OriginalFrameSize.Choose([new SlidePixelSize(1281, 721, 0)]);

        Assert.Equal((1280, 720), size);
    }

    [Fact]
    public void Choose_SideOverTheLimit_ScalesDownKeepingTheShape()
    {
        // 6000x2000 is 3:1; capped at 4096 wide it is 4096x1365.33, floored to even.
        (int, int)? size = OriginalFrameSize.Choose([new SlidePixelSize(6000, 2000, 0)]);

        Assert.Equal((4096, 1364), size);
    }

    [Fact]
    public void Choose_AtTheLevelLimitExactly_IsLeftAlone()
    {
        // 4096x2304 is exactly 256x144 = 36,864 macroblocks.
        (int, int)? size = OriginalFrameSize.Choose([new SlidePixelSize(4096, 2304, 0)]);

        Assert.Equal((4096, 2304), size);
    }

    [Theory]
    [InlineData(4096, 4096)]
    [InlineData(6000, 4000)]
    [InlineData(4000, 3000)]
    [InlineData(2304, 4097)]
    public void Choose_OverTheMacroblockLimit_FitsWithinItAndStaysEven(int width, int height)
    {
        (int Width, int Height) size = OriginalFrameSize.Choose([new SlidePixelSize(width, height, 0)])!.Value;

        long macroblocks = (long)((size.Width + 15) / 16) * ((size.Height + 15) / 16);
        Assert.InRange(macroblocks, 1, OriginalFrameSize.MaxMacroblocks);
        Assert.Equal(0, size.Width % 2);
        Assert.Equal(0, size.Height % 2);
        Assert.InRange(size.Width, 1, OriginalFrameSize.MaxSide);
        Assert.InRange(size.Height, 1, OriginalFrameSize.MaxSide);

        // Shape kept to within a macroblock's worth of rounding.
        Assert.InRange((double)size.Width / size.Height, (double)width / height * 0.99, (double)width / height * 1.01);
    }

    [Fact]
    public void Choose_SquareOverTheMacroblockLimit_IsTheLargestSquareThatFits()
    {
        // sqrt(36,864) = 192 macroblocks a side = 3072 pixels.
        (int, int)? size = OriginalFrameSize.Choose([new SlidePixelSize(4096, 4096, 0)]);

        Assert.Equal((3072, 3072), size);
    }

    [Fact]
    public void Choose_TinySlide_IsRaisedToTheMinimumSide()
    {
        (int, int)? size = OriginalFrameSize.Choose([new SlidePixelSize(64, 48, 0)]);

        Assert.Equal((OriginalFrameSize.MinSide, OriginalFrameSize.MinSide), size);
    }

    [Fact]
    public void Choose_NoSlides_ReturnsNull()
    {
        Assert.Null(OriginalFrameSize.Choose([]));
    }

    [Fact]
    public void Choose_OnlyUnknownSizes_ReturnsNull()
    {
        Assert.Null(OriginalFrameSize.Choose([new SlidePixelSize(0, 0, 0)]));
    }
}
