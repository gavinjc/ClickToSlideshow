namespace ClickToSlideshow.Core.Tests;

// xunit runs unpackaged, so only the fallback branch is reachable here; the packaged branch
// (LocalState/TempState) was verified by hand against the installed MSIX. If package-identity
// detection inverted, ApplicationData.Current would throw in AppDataFolders' static initialiser
// and these would fail before reaching the assertions.
public class AppDataFoldersTests
{
    [Fact]
    public void LocalRoot_Unpackaged_IsClickToSlideshowFolderUnderLocalAppData()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClickTo Slideshow");

        Assert.Equal(expected, AppDataFolders.LocalRoot);
    }

    [Fact]
    public void TempRoot_Unpackaged_IsClickToSlideshowFolderUnderTemp()
    {
        Assert.Equal(Path.Combine(Path.GetTempPath(), "ClickToSlideshow"), AppDataFolders.TempRoot);
    }
}
