using System.Text.RegularExpressions;
using ClickToSlideshow.App.ViewModels;

namespace ClickToSlideshow.App.Tests;

// The shell extension decides whether Create slideshow appears from its own C++ table; the app
// decides what a drop or a launch adds from SupportedFiles. If they drift, the right-click menu and
// a drop disagree about the same file.
public class SupportedFilesTests
{
    [Fact]
    public void ShellExtensionTable_MatchesTheAppsPhotoAndMusicTypes()
    {
        string header = File.ReadAllText(SourceTree.Find("src", "ClickToSlideshow.ShellExtension", "SupportedExtensions.h"));
        IEnumerable<string> shellExtensions = Regex.Matches(header, @"L""(\.[a-z0-9]+)""").Select(match => match.Groups[1].Value);

        Assert.Equal(
            SupportedFiles.ImageExtensions.Concat(SupportedFiles.AudioExtensions).Order(),
            shellExtensions.Order());
    }
}
