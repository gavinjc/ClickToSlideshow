using ClickToSlideshow.Core.Imaging;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ClickToSlideshow.Core.Tests;

public class SlideImageLoaderTests
{
    // 6 = EXIF "rotate 90 CW to display correctly". Windows.Graphics.Imaging's own decoder
    // (both BitmapDecoder.GetSoftwareBitmapAsync and Win2D's CanvasBitmap.LoadAsync) silently
    // pre-rotates pixel data to match this tag by default. LoadBitmapAsync must undo that
    // (ExifOrientationMode.IgnoreExifOrientation) so Slide.RotationDegrees — read separately via
    // DetectExifRotationDegreesAsync and applied once by FrameComposer — stays the only rotation
    // applied, instead of stacking on top of an implicit one already baked into the decode.
    [Fact]
    public async Task LoadBitmapAsync_ExifTaggedPortraitPhoto_ReturnsRawUnrotatedDimensions()
    {
        string path = await WriteExifTaggedJpegAsync(rawWidth: 40, rawHeight: 20, exifOrientation: 6);
        try
        {
            CanvasDevice device = CanvasDevice.GetSharedDevice();
            CanvasBitmap bitmap = await SlideImageLoader.LoadBitmapAsync(device, path);

            Assert.Equal(40u, bitmap.SizeInPixels.Width);
            Assert.Equal(20u, bitmap.SizeInPixels.Height);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<string> WriteExifTaggedJpegAsync(int rawWidth, int rawHeight, ushort exifOrientation)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.jpg");
        using SoftwareBitmap sourceBitmap = new(BitmapPixelFormat.Bgra8, rawWidth, rawHeight, BitmapAlphaMode.Premultiplied);

        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        StorageFile outputFile = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);

        using (IRandomAccessStream stream = await outputFile.OpenAsync(FileAccessMode.ReadWrite))
        {
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
            encoder.SetSoftwareBitmap(sourceBitmap);
            var properties = new BitmapPropertySet
            {
                ["System.Photo.Orientation"] = new BitmapTypedValue(exifOrientation, Windows.Foundation.PropertyType.UInt16),
            };
            await encoder.BitmapProperties.SetPropertiesAsync(properties);
            await encoder.FlushAsync();
        }

        return path;
    }
}
