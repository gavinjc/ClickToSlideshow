using ClickToSlideshow.App.ViewModels;

namespace ClickToSlideshow.App.Tests;

// CodecFixLinks.GetFixLink is the format-agnostic guard MainViewModel.DescribeLoadError uses — it
// must fire for every extension whose WIC codec is an optional Store install (HEIC/HEIF, WEBP),
// stay silent for extensions WIC always ships a codec for, and point each format at its own Store
// "apps for this file type" page rather than a hardcoded one.
public class CodecFixLinksTests
{
    [Theory]
    [InlineData(".heic", "HeifCodecMissingMessage")]
    [InlineData(".heif", "HeifCodecMissingMessage")]
    [InlineData(".HEIC", "HeifCodecMissingMessage")]
    [InlineData(".webp", "WebpCodecMissingMessage")]
    [InlineData(".cr2", "RawCodecMissingMessage")]
    [InlineData(".NEF", "RawCodecMissingMessage")]
    [InlineData(".dng", "RawCodecMissingMessage")]
    public void KnownOptionalCodecExtension_ReturnsMessageKeyAndMatchingStoreLink(string extension, string expectedMessageKey)
    {
        var fixLink = CodecFixLinks.GetFixLink(extension);

        Assert.NotNull(fixLink);
        Assert.Equal(expectedMessageKey, fixLink!.Value.MessageResourceKey);
        Assert.Equal($"ms-windows-store://assoc/?FileExt={extension}", fixLink.Value.StoreLinkUri.OriginalString);
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".tiff")]
    [InlineData(".bmp")]
    [InlineData(".gif")]
    [InlineData(".mp3")]
    [InlineData(".exe")]
    public void ExtensionWithNoOptionalCodec_ReturnsNull(string extension)
    {
        Assert.Null(CodecFixLinks.GetFixLink(extension));
    }
}
