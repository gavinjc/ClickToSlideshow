using Bureau.Common.Messages;
using ClickToSlideshow.App.ViewModels;

namespace ClickToSlideshow.App.Tests;

// HadProblemThisSession decides whether the Store rating prompt may appear after a save
// (app-conventions.md → Rating prompt, Gavin 2026-10-31), and the message rows are how an error
// reaches the user. An error must keep counting after its row has closed. (The skipped-file rows
// can't be driven from here: their text reads a resw string through ResourceLoader, which needs a
// WinUI-hosted process; SkippedFilesTextTests checks their wording instead.)
public class MainViewModelRatingGateTests : IDisposable
{
    private readonly string _emptyFolder = Path.Combine(Path.GetTempPath(), "ClickToSlideshowTests", Guid.NewGuid().ToString());
    private readonly MainViewModel _viewModel = new(null!);
    private readonly List<Message> _shown = [];
    private readonly List<string> _closed = [];

    public MainViewModelRatingGateTests()
    {
        Directory.CreateDirectory(_emptyFolder);
        _viewModel.MessageShown += _shown.Add;
        _viewModel.MessageClosed += _closed.Add;
    }

    public void Dispose() => Directory.Delete(_emptyFolder, recursive: true);

    [Fact]
    public void ShowError_ShowsAnErrorRow_AndHoldsBackTheRating()
    {
        _viewModel.ShowError("Save failed");

        Message row = Assert.Single(_shown);
        Assert.Equal(MessageKeys.Error, row.Key);
        Assert.Equal(MessageSeverity.Error, row.Severity);
        Assert.Equal("Save failed", row.Text);
        Assert.True(_viewModel.HadProblemThisSession);
    }

    [Fact]
    public async Task ACleanLoadAfterAnError_ClosesTheErrorRow_ButStillHoldsBackTheRating()
    {
        _viewModel.ShowError("Save failed");

        await _viewModel.LoadFolderAsync(_emptyFolder);

        Assert.Contains(MessageKeys.Error, _closed);
        Assert.True(_viewModel.HadProblemThisSession);
    }

    [Fact]
    public async Task ACleanLoad_LeavesTheRatingFree()
    {
        await _viewModel.LoadFolderAsync(_emptyFolder);

        Assert.Empty(_shown);
        Assert.False(_viewModel.HadProblemThisSession);
    }

    [Fact]
    public async Task LoadingAFolderThatIsGone_ShowsAnErrorRow()
    {
        await _viewModel.LoadFolderAsync(Path.Combine(_emptyFolder, "gone"));

        Assert.Equal(MessageKeys.Error, Assert.Single(_shown).Key);
        Assert.True(_viewModel.HadProblemThisSession);
    }
}
