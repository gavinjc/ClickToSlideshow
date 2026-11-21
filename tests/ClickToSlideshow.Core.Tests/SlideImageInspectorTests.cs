using System.Runtime.InteropServices.WindowsRuntime;
using ClickToSlideshow.Core.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ClickToSlideshow.Core.Tests;

public class SlideImageInspectorTests
{
    [Fact]
    public async Task InspectAsync_ExifTaggedJpeg_ReportsRawOrientationAndRawDimensions()
    {
        string path = await WriteExifTaggedJpegAsync(rawWidth: 200, rawHeight: 100, exifOrientation: 6);
        try
        {
            SlideInfo info = await SlideImageInspector.InspectAsync(path);

            Assert.Equal(200, info.PixelWidth);
            Assert.Equal(100, info.PixelHeight);
            Assert.Equal(6, info.ExifOrientation);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task GetThumbnailAsync_LargerThanRequestedLongEdge_ScalesDownToFitRequestedLongEdge()
    {
        string path = await WriteExifTaggedJpegAsync(rawWidth: 200, rawHeight: 100, exifOrientation: 1);
        try
        {
            byte[] thumbnailBytes = await SlideImageInspector.GetThumbnailAsync(path, requestedLongEdge: 50);

            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(thumbnailBytes.AsBuffer());
            stream.Seek(0);
            BitmapDecoder thumbnailDecoder = await BitmapDecoder.CreateAsync(stream);

            Assert.True(thumbnailDecoder.PixelWidth <= 50, $"expected width <= 50, got {thumbnailDecoder.PixelWidth}");
            Assert.True(Math.Max(thumbnailDecoder.PixelWidth, thumbnailDecoder.PixelHeight) >= 40);
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
