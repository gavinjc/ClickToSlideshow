using ClickToSlideshow.App.State;

namespace ClickToSlideshow.App.Tests;

public class SaveFileNamingTests
{
    [Fact]
    public void ComputeDefaultFileName_ReturnsDateStampWithGivenExtension()
    {
        string name = SaveFileNaming.ComputeDefaultFileName(".mp4");

        Assert.Equal($"Slideshow_{DateTime.Now:yyyy-MM-dd}.mp4", name);
    }

    [Fact]
    public void ResolveCollision_NoExistingFile_ReturnsDesiredNameUnchanged()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        string resolved = SaveFileNaming.ResolveCollision(directory, "Slideshow.json");

        Assert.Equal("Slideshow.json", resolved);
    }

    [Fact]
    public void ResolveCollision_ExistingFile_AppendsIncrementingSuffix()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "Slideshow.json"), "");
            File.WriteAllText(Path.Combine(directory, "Slideshow (2).json"), "");

            string resolved = SaveFileNaming.ResolveCollision(directory, "Slideshow.json");

            Assert.Equal("Slideshow (3).json", resolved);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
