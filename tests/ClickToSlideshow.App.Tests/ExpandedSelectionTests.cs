using ClickToSlideshow.App.ViewModels;

namespace ClickToSlideshow.App.Tests;

// A drop from File Explorer must expand the way the right-click menu does (ExplorerCommand.cpp's
// Invoke and ExpandFolder), or the two routes would add different things from the same folder.
public class ExpandedSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClickToSlideshowTests", Guid.NewGuid().ToString());

    public ExpandedSelectionTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string CreateFile(params string[] relativeParts)
    {
        string path = Path.Combine([_root, .. relativeParts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    private string CreateFolder(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void DroppedFiles_SupportedAreKept_UnsupportedAreSkippedByName()
    {
        string photo = CreateFile("beach.jpg");
        string song = CreateFile("song.mp3");
        string notes = CreateFile("notes.txt");
        string gif = CreateFile("party.gif");

        ExpandedSelection selection = ExpandedSelection.Of([photo, notes, song, gif]);

        Assert.Equal([photo, song], selection.SupportedPaths);
        Assert.Equal(["notes.txt", "party.gif"], selection.SkippedNames);
    }

    [Fact]
    public void DroppedFolder_GivesItsSupportedFiles_SkipsItsOthers_IgnoresSubfolders()
    {
        string folder = CreateFolder("Holiday");
        string photo = CreateFile("Holiday", "a.png");
        string song = CreateFile("Holiday", "b.flac");
        CreateFile("Holiday", "readme.txt");
        CreateFile("Holiday", "Nested", "deep.jpg");

        ExpandedSelection selection = ExpandedSelection.Of([folder]);

        Assert.Equal([photo, song], selection.SupportedPaths.Order());
        Assert.Equal(["readme.txt"], selection.SkippedNames);
    }

    [Fact]
    public void DroppedFolderWithOnlyUnsupportedFiles_IsSkippedByItsOwnNameAlone()
    {
        string folder = CreateFolder("Documents");
        CreateFile("Documents", "letter.docx");
        CreateFile("Documents", "Photos", "inside.jpg");

        ExpandedSelection selection = ExpandedSelection.Of([folder]);

        Assert.Empty(selection.SupportedPaths);
        Assert.Equal(["Documents"], selection.SkippedNames);
    }

    [Fact]
    public void EmptyFolder_IsSkippedByItsOwnName()
    {
        string folder = CreateFolder("Empty");

        ExpandedSelection selection = ExpandedSelection.Of([folder]);

        Assert.Equal(["Empty"], selection.SkippedNames);
    }

    // A photo dragged from a phone over MTP has no path, so MainWindow passes its bare name.
    // Taken by extension alone it would become a slide that can never load; listed as an
    // unsupported skip it wouldn't hold back the rating prompt.
    [Fact]
    public void BareNameWithSupportedExtension_IsUnreadableNotAdded()
    {
        ExpandedSelection selection = ExpandedSelection.Of(["IMG_0001.jpg"]);

        Assert.Empty(selection.SupportedPaths);
        Assert.Empty(selection.SkippedNames);
        Assert.Equal(["IMG_0001.jpg"], selection.UnreadableNames);
    }

    [Fact]
    public void MissingFileWithSupportedExtension_IsUnreadable()
    {
        string gone = Path.Combine(_root, "gone.mp3");

        ExpandedSelection selection = ExpandedSelection.Of([gone]);

        Assert.Empty(selection.SupportedPaths);
        Assert.Equal(["gone.mp3"], selection.UnreadableNames);
    }

    [Fact]
    public void BareNameOfUnsupportedType_IsAnOrdinarySkip()
    {
        ExpandedSelection selection = ExpandedSelection.Of(["clip.mov"]);

        Assert.Equal(["clip.mov"], selection.SkippedNames);
        Assert.Empty(selection.UnreadableNames);
    }

    // Slideshow is the family's one app that allows repeats (app-conventions.md → Add button).
    [Fact]
    public void SameFileTwice_IsKeptTwice()
    {
        string photo = CreateFile("cover.jpg");

        ExpandedSelection selection = ExpandedSelection.Of([photo, photo]);

        Assert.Equal([photo, photo], selection.SupportedPaths);
        Assert.Empty(selection.SkippedNames);
    }
}
