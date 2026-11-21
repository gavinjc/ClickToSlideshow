using Microsoft.Graphics.Canvas;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ClickToSlideshow.Core.Imaging;

/// <summary>
/// Decodes a source image for the render pipeline. Rotation is read separately from decode
/// (<see cref="DetectExifRotationDegreesAsync"/>) — the caller stores the result on
/// <see cref="Model.Slide.RotationDegrees"/> once, at import time, and
/// <see cref="Rendering.FrameComposer"/> applies it every frame. <see cref="CanvasBitmap.LoadAsync(ICanvasResourceCreator, IRandomAccessStream)"/>
/// silently honours EXIF orientation itself (confirmed by measuring its reported
/// <c>Size</c> against a known-tagged test file — contrary to what its own docs imply), which
/// would double-rotate every EXIF-tagged photo on top of the explicit
/// <see cref="Model.Slide.RotationDegrees"/> transform. Decoding through
/// <see cref="BitmapDecoder.GetSoftwareBitmapAsync(BitmapPixelFormat, BitmapAlphaMode, BitmapTransform, ExifOrientationMode, ColorManagementMode)"/>
/// with <see cref="ExifOrientationMode.IgnoreExifOrientation"/> keeps <see cref="Model.Slide.RotationDegrees"/>
/// the single source of truth for rotation, as the rest of the pipeline assumes.
/// </summary>
public static class SlideImageLoader
{
    public static async Task<CanvasBitmap> LoadBitmapAsync(ICanvasResourceCreator resourceCreator, string path)
    {
        BitmapDecoder decoder = await OpenDecoderAsync(path);
        using SoftwareBitmap rawBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        return CanvasBitmap.CreateFromSoftwareBitmap(resourceCreator, rawBitmap);
    }

    public static async Task<int> DetectExifRotationDegreesAsync(string path)
    {
        BitmapDecoder decoder = await OpenDecoderAsync(path);

        var properties = await decoder.BitmapProperties.GetPropertiesAsync(["System.Photo.Orientation"]);
        ushort exifOrientation = properties.TryGetValue("System.Photo.Orientation", out BitmapTypedValue value)
            && value.Value is ushort orientation
                ? orientation
                : (ushort)1;

        return NormalizeExifRotation(exifOrientation);
    }

    internal static int NormalizeExifRotation(ushort exifOrientation) => exifOrientation switch
    {
        6 => 90,
        3 => 180,
        8 => 270,
        _ => 0,
    };

    internal static async Task<BitmapDecoder> OpenDecoderAsync(string path)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        using IRandomAccessStream stream = await file.OpenReadAsync();
        return await BitmapDecoder.CreateAsync(stream);
    }
}
