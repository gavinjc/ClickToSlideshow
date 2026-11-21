using ClickToSlideshow.Core.Model;

namespace ClickToSlideshow.Core.Pptx;

/// <summary>
/// Where a picture shape sits on a PPTX slide: unrotated local position/size (as OOXML's
/// <c>off</c>/<c>ext</c> expect — rotation is applied by the shape's own <c>rot</c> about its
/// center, not baked into these numbers), plus a source-image crop for <c>FitMode.Fill</c>
/// expressed as 0..1 fractions of the raw image on each edge.
/// </summary>
public readonly record struct PptxSlideGeometry(
    double OffsetX,
    double OffsetY,
    double Width,
    double Height,
    int RotationDegrees,
    double CropLeft,
    double CropTop,
    double CropRight,
    double CropBottom);

/// <summary>
/// Pure geometry for placing one slide's picture on a PPTX slide, in whatever linear unit
/// <paramref name="targetWidth"/>/<paramref name="targetHeight"/> are expressed in (EMU, in
/// practice). Mirrors <see cref="Rendering.SlideTransform.Compute"/>'s Fit/Fill scale rule
/// exactly, so a PPTX slide and a Win2D-rendered frame show the same crop/letterbox — see that
/// type's doc comment for the shared rotate-about-center-then-scale model this is derived from.
///
/// OOXML rotates a shape about the center of its own *unrotated* <c>off</c>/<c>ext</c> box, and
/// centering by center-point is itself rotation-invariant, so — unlike <see cref="Rendering.SlideTransform"/>,
/// which must build one combined matrix — this never needs to reason about a rotated bounding
/// box directly for placement. It only reasons about the rotated bounding box once, to work out
/// how much of the source image overflows the target under <see cref="FitMode.Fill"/> and must
/// be cropped away.
/// </summary>
public static class PptxSlideGeometryCalculator
{
    public static PptxSlideGeometry Compute(
        double rawWidth, double rawHeight, int rotationDegrees, FitMode fitMode,
        double targetWidth, double targetHeight)
    {
        bool swap = rotationDegrees is 90 or 270;
        double effectiveWidth = swap ? rawHeight : rawWidth;
        double effectiveHeight = swap ? rawWidth : rawHeight;

        double scaleToFitWidth = targetWidth / effectiveWidth;
        double scaleToFitHeight = targetHeight / effectiveHeight;
        double scale = fitMode == FitMode.Fit
            ? Math.Min(scaleToFitWidth, scaleToFitHeight)
            : Math.Max(scaleToFitWidth, scaleToFitHeight);

        // Local (unrotated) size: what off/ext would be if rotationDegrees were 0.
        double localWidth = rawWidth * scale;
        double localHeight = rawHeight * scale;

        (double boundsX, double boundsY) = CanvasBounds(localWidth, localHeight, swap);

        double excessX = Math.Max(0, boundsX - targetWidth);
        double excessY = Math.Max(0, boundsY - targetHeight);

        // Map the canvas-space excess back onto the local (raw-image) axes it came from.
        (double cropLocalWidth, double cropLocalHeight) = swap ? (excessY, excessX) : (excessX, excessY);

        double croppedLocalWidth = localWidth - cropLocalWidth;
        double croppedLocalHeight = localHeight - cropLocalHeight;

        // Centering must use the LOCAL (unrotated) box, not its post-rotation canvas bounds:
        // OOXML's off/ext describe the unrotated box, and rot then spins it about its own
        // center — off + ext/2, in local space. Local-center placement is what's
        // rotation-invariant (the center point itself doesn't move under rotation), not the
        // position of the swapped bounding box, which is a different point whenever rot is 90
        // or 270 (found by comparing against a real PowerPoint render, not derived correctly the
        // first time — see PptxSlideGeometryTests for the corrected expected values).
        return new PptxSlideGeometry(
            OffsetX: (targetWidth - croppedLocalWidth) / 2,
            OffsetY: (targetHeight - croppedLocalHeight) / 2,
            Width: croppedLocalWidth,
            Height: croppedLocalHeight,
            RotationDegrees: rotationDegrees,
            CropLeft: cropLocalWidth / (2 * localWidth),
            CropRight: cropLocalWidth / (2 * localWidth),
            CropTop: cropLocalHeight / (2 * localHeight),
            CropBottom: cropLocalHeight / (2 * localHeight));
    }

    // A rectangle rotated by a multiple of 90 degrees has an axis-aligned bounding box equal to
    // itself (0/180) or itself with width/height swapped (90/270) — true only because rotation
    // here is always a multiple of 90 degrees (EXIF/user rotation never produces anything else).
    private static (double X, double Y) CanvasBounds(double localWidth, double localHeight, bool swap) =>
        swap ? (localHeight, localWidth) : (localWidth, localHeight);
}
