using ClickToSlideshow.App.ViewModels;

namespace ClickToSlideshow.App.Tests;

// The skipped-row wording is a family rule (Bureau.Common app-conventions.md → Message bar):
// "Skipped notes.txt" for one, "2 files skipped: a.txt, b.png" for more, never "(s)". Checked
// against the real resw strings, with the "{stem}One"/"{stem}Many" pairs MainViewModel uses.
public class SkippedFilesTextTests
{
    private static string Format(IReadOnlyList<string> names, string formatKeyStem) =>
        SkippedFilesText.Format(names, AppResw.Get(formatKeyStem + "One"), AppResw.Get(formatKeyStem + "Many"));

    [Fact]
    public void OneUnsupportedFile_ReadsSkippedAndItsName()
    {
        Assert.Equal("Skipped notes.txt", Format(["notes.txt"], "SkippedFiles"));
    }

    [Fact]
    public void TwoUnsupportedFiles_ReadCountFilesSkippedAndTheList()
    {
        Assert.Equal("2 files skipped: a.txt, b.png", Format(["a.txt", "b.png"], "SkippedFiles"));
    }

    [Theory]
    [InlineData("SkippedFiles")]
    [InlineData("UnreadableFiles")]
    [InlineData("MissingAudio")]
    [InlineData("UnreadableAudio")]
    public void EveryKind_HasBothFormsAndNoParenthesisedPlural(string formatKeyStem)
    {
        string one = Format(["song.ogg"], formatKeyStem);
        string many = Format(["a.ogg", "b.opus", "c.mp3"], formatKeyStem);

        Assert.StartsWith("Skipped song.ogg", one, StringComparison.Ordinal);
        Assert.StartsWith("3 ", many, StringComparison.Ordinal);
        Assert.EndsWith("a.ogg, b.opus, c.mp3", many, StringComparison.Ordinal);
        Assert.DoesNotContain("(s)", one, StringComparison.Ordinal);
        Assert.DoesNotContain("(s)", many, StringComparison.Ordinal);
    }
}
