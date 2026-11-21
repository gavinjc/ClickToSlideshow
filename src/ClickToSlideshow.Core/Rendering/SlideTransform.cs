using System.Numerics;
using ClickToSlideshow.Core.Model;
using Windows.Foundation;

namespace ClickToSlideshow.Core.Rendering;

/// <summary>
/// Computes the transform that positions a decoded bitmap within the render target: rotate
/// around its own center, scale to fit or fill per <see cref="FitMode"/>, then center on the
/// target canvas.
/// </summary>
public static class SlideTransform
{
    public static Matrix3x2 Compute(Size bitmapSize, int rotationDegrees, FitMode fitMode, Size targetSize)
    {
        float radians = rotationDegrees * MathF.PI / 180f;

        bool swapDimensions = rotationDegrees is 90 or 270;
        double effectiveWidth = swapDimensions ? bitmapSize.Height : bitmapSize.Width;
        double effectiveHeight = swapDimensions ? bitmapSize.Width : bitmapSize.Height;

        double scaleToFitWidth = targetSize.Width / effectiveWidth;
        double scaleToFitHeight = targetSize.Height / effectiveHeight;
        double scale = fitMode == FitMode.Fit
            ? Math.Min(scaleToFitWidth, scaleToFitHeight)
            : Math.Max(scaleToFitWidth, scaleToFitHeight);

        var bitmapCenter = new Vector2((float)(bitmapSize.Width / 2), (float)(bitmapSize.Height / 2));
        var canvasCenter = new Vector2((float)(targetSize.Width / 2), (float)(targetSize.Height / 2));

        return Matrix3x2.CreateTranslation(-bitmapCenter)
            * Matrix3x2.CreateRotation(radians)
            * Matrix3x2.CreateScale((float)scale)
            * Matrix3x2.CreateTranslation(canvasCenter);
    }
}
