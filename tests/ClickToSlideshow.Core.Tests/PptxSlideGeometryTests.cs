using ClickToSlideshow.Core.Model;
using ClickToSlideshow.Core.Pptx;

namespace ClickToSlideshow.Core.Tests;

public class PptxSlideGeometryTests
{
    private const double Tolerance = 0.001;

    [Fact]
    public void Compute_FitLandscapeImageOnLandscapeSlide_MatchesSlideExactlyNoCrop()
    {
        // 2:1 image on a 16:9 slide: width-constrained, letterboxed top/bottom.
        PptxSlideGeometry geometry = PptxSlideGeometryCalculator.Compute(
            rawWidth: 2000, rawHeight: 1000, rotationDegrees: 0, FitMode.Fit,
            targetWidth: 1920, targetHeight: 1080);

        Assert.Equal(1920, geometry.Width, Tolerance);
        Assert.Equal(960, geometry.Height, Tolerance);
        Assert.Equal(0, geometry.OffsetX, Tolerance);
        Assert.Equal(60, geometry.OffsetY, Tolerance);
        Assert.Equal(0, geometry.CropLeft, Tolerance);
        Assert.Equal(0, geometry.CropTop, Tolerance);
    }

    [Fact]
    public void Compute_FillLandscapeImageOnLandscapeSlide_CoversSlideAndCropsSides()
    {
        // Same 2:1 image, Fill: height-constrained, overflow on width gets cropped symmetrically.
        PptxSlideGeometry geometry = PptxSlideGeometryCalculator.Compute(
            rawWidth: 2000, rawHeight: 1000, rotationDegrees: 0, FitMode.Fill,
            targetWidth: 1920, targetHeight: 1080);

        Assert.Equal(1920, geometry.Width, Tolerance);
        Assert.Equal(1080, geometry.Height, Tolerance);
        Assert.Equal(0, geometry.OffsetX, Tolerance);
        Assert.Equal(0, geometry.OffsetY, Tolerance);

        // Full-scale (uncropped) width would be 2160 (1080*2), so 240 total overflow, 120 each
        // side, out of the 2160 uncropped width -> ~5.56% cropped from each of left/right.
        Assert.Equal(120.0 / 2160.0, geometry.CropLeft, Tolerance);
        Assert.Equal(120.0 / 2160.0, geometry.CropRight, Tolerance);
        Assert.Equal(0, geometry.CropTop, Tolerance);
        Assert.Equal(0, geometry.CropBottom, Tolerance);
    }

    [Fact]
    public void Compute_FitPortraitImageOnLandscapeSlide_LetterboxesLeftRight()
    {
        PptxSlideGeometry geometry = PptxSlideGeometryCalculator.Compute(
            rawWidth: 1000, rawHeight: 2000, rotationDegrees: 0, FitMode.Fit,
            targetWidth: 1920, targetHeight: 1080);

        Assert.Equal(540, geometry.Width, Tolerance);
        Assert.Equal(1080, geometry.Height, Tolerance);
        Assert.Equal((1920 - 540) / 2.0, geometry.OffsetX, Tolerance);
        Assert.Equal(0, geometry.OffsetY, Tolerance);
    }

    [Fact]
    public void Compute_Rotated90FitPortraitImageOnLandscapeSlide_TreatsRotatedImageAsLandscape()
    {
        // A portrait-stored image rotated 90 degrees displays as landscape (2000x1000
        // effective) — Fit against a 1920x1080 slide should behave exactly like the
        // already-landscape 2000x1000 case, just with the shape's own local ext unrotated.
        PptxSlideGeometry geometry = PptxSlideGeometryCalculator.Compute(
            rawWidth: 1000, rawHeight: 2000, rotationDegrees: 90, FitMode.Fit,
            targetWidth: 1920, targetHeight: 1080);

        // Effective (post-rotation) size fits width-constrained at scale 0.96, so local
        // (unrotated) ext is rawWidth*0.96 x rawHeight*0.96 = 960 x 1920, whose rotated bounding
        // box is 1920 x 960 — matching the unrotated-landscape Fit case exactly.
        Assert.Equal(960, geometry.Width, Tolerance);
        Assert.Equal(1920, geometry.Height, Tolerance);
        Assert.Equal(90, geometry.RotationDegrees);

        double boundsX = geometry.Height; // swapped: local height is the canvas-X extent
        double boundsY = geometry.Width;
        Assert.Equal(1920, boundsX, Tolerance);
        Assert.Equal(960, boundsY, Tolerance);

        // Off is the LOCAL (unrotated) box's top-left, not the rotated bounding box's — its
        // center (off + ext/2) must equal the canvas center (960, 540) so the 90-degree rotation
        // about that center lands the visible bounding box at (0, 60), letterboxed top/bottom.
        // Confirmed against a real PowerPoint render: the first version of this formula used the
        // rotated bounding box directly here, which visibly mispositioned every 90/270-rotated
        // picture (a small corner-anchored image instead of filling the frame).
        Assert.Equal(960 - 960 / 2.0, geometry.OffsetX, Tolerance);
        Assert.Equal(540 - 1920 / 2.0, geometry.OffsetY, Tolerance);
    }

    [Fact]
    public void Compute_Rotated90FillPortraitImageOnLandscapeSlide_CropsAlongLocalHeightAxis()
    {
        // Same rotated image under Fill: the overflow appears on the canvas-X axis (2160 wide
        // vs. a 1920 target), which for a 90-degree rotation maps back to the local HEIGHT axis
        // (canvas-X <-> local height when swapped) — so the crop lands on top/bottom, not
        // left/right, exactly the swap this mapping exists to get right.
        PptxSlideGeometry geometry = PptxSlideGeometryCalculator.Compute(
            rawWidth: 1000, rawHeight: 2000, rotationDegrees: 90, FitMode.Fill,
            targetWidth: 1920, targetHeight: 1080);

        double boundsX = geometry.Height;
        double boundsY = geometry.Width;
        Assert.Equal(1920, boundsX, Tolerance);
        Assert.Equal(1080, boundsY, Tolerance);

        Assert.Equal(0, geometry.CropLeft, Tolerance);
        Assert.Equal(0, geometry.CropRight, Tolerance);
        Assert.True(geometry.CropTop > 0);
        Assert.True(geometry.CropBottom > 0);
    }

    [Fact]
    public void Compute_SquareImageOnLandscapeSlideFit_LetterboxesLeftRightAndStaysCentered()
    {
        PptxSlideGeometry geometry = PptxSlideGeometryCalculator.Compute(
            rawWidth: 1000, rawHeight: 1000, rotationDegrees: 0, FitMode.Fit,
            targetWidth: 1920, targetHeight: 1080);

        Assert.Equal(1080, geometry.Width, Tolerance);
        Assert.Equal(1080, geometry.Height, Tolerance);
        Assert.Equal((1920 - 1080) / 2.0, geometry.OffsetX, Tolerance);
        Assert.Equal(0, geometry.OffsetY, Tolerance);
    }

    [Theory]
    [InlineData(0, FitMode.Fit)]
    [InlineData(90, FitMode.Fit)]
    [InlineData(180, FitMode.Fit)]
    [InlineData(270, FitMode.Fit)]
    [InlineData(0, FitMode.Fill)]
    [InlineData(90, FitMode.Fill)]
    [InlineData(180, FitMode.Fill)]
    [InlineData(270, FitMode.Fill)]
    public void Compute_AnyRotationOrFitMode_LocalBoxCenterIsCanvasCenter(int rotationDegrees, FitMode fitMode)
    {
        // The physical invariant that makes OOXML's off/ext/rot model work at all: PowerPoint
        // rotates a shape about the center of its own *unrotated* box (off + ext/2), so for the
        // rotated result to land centered on the slide, that local center — not the rotated
        // bounding box's position — must equal the canvas center. A prior version of Compute got
        // this wrong for 90/270 by centering the rotated bounding box instead of the local box;
        // every non-rotated/180 case happened to pass anyway since local box == rotated bbox
        // there, which is exactly why a real PowerPoint render (not just these numbers) caught
        // it first. This test checks the invariant directly rather than a hand-derived number.
        PptxSlideGeometry geometry = PptxSlideGeometryCalculator.Compute(
            rawWidth: 1000, rawHeight: 2000, rotationDegrees, fitMode,
            targetWidth: 1920, targetHeight: 1080);

        double localCenterX = geometry.OffsetX + geometry.Width / 2;
        double localCenterY = geometry.OffsetY + geometry.Height / 2;

        Assert.Equal(1920 / 2.0, localCenterX, Tolerance);
        Assert.Equal(1080 / 2.0, localCenterY, Tolerance);
    }

    [Fact]
    public void Compute_Rotated180_SameGeometryAsUnrotated()
    {
        PptxSlideGeometry unrotated = PptxSlideGeometryCalculator.Compute(
            rawWidth: 2000, rawHeight: 1000, rotationDegrees: 0, FitMode.Fit, 1920, 1080);
        PptxSlideGeometry rotated180 = PptxSlideGeometryCalculator.Compute(
            rawWidth: 2000, rawHeight: 1000, rotationDegrees: 180, FitMode.Fit, 1920, 1080);

        Assert.Equal(unrotated.Width, rotated180.Width, Tolerance);
        Assert.Equal(unrotated.Height, rotated180.Height, Tolerance);
        Assert.Equal(unrotated.OffsetX, rotated180.OffsetX, Tolerance);
        Assert.Equal(unrotated.OffsetY, rotated180.OffsetY, Tolerance);
        Assert.Equal(180, rotated180.RotationDegrees);
    }
}
