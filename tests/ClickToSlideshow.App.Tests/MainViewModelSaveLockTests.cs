using ClickToSlideshow.App.State;
using ClickToSlideshow.App.ViewModels;
using ClickToSlideshow.Core.Model;

namespace ClickToSlideshow.App.Tests;

// The editing lock during a video or PowerPoint save (Gavin, 2026-11-04). The save is a
// TaskCompletionSource the test ends by hand, so edits can be tried while it is still running.
public class MainViewModelSaveLockTests
{
    private static MainViewModel CreateViewModelWithSlides(params string[] paths)
    {
        var viewModel = new MainViewModel(null!);
        foreach (string path in paths)
        {
            viewModel.Slides.Add(new SlideItem(new Slide { SourcePath = path }));
        }

        return viewModel;
    }

    private static List<string> SourcePaths(MainViewModel viewModel) =>
        viewModel.Slides.Select(s => s.SourcePath).ToList();

    [Fact]
    public async Task EditsDuringASave_AreIgnored()
    {
        MainViewModel viewModel = CreateViewModelWithSlides("c", "a", "b");
        viewModel.RotateSelected([viewModel.Slides[0]], clockwise: true);
        var audio = new AudioTrackItem(new AudioTrack { SourcePath = "song.mp3", DurationMs = 1000 });
        viewModel.AudioTracks.Add(audio);
        var save = new TaskCompletionSource();

        Task saving = viewModel.SaveLockedAsync(() => save.Task);
        Assert.False(viewModel.CanEdit);

        viewModel.MoveSelected([viewModel.Slides[0]], delta: 1);
        viewModel.RemoveSelected([viewModel.Slides[1]]);
        viewModel.RotateSelected([viewModel.Slides[2]], clockwise: true);
        viewModel.ApplySort(SortOrder.NameNatural);
        viewModel.ReverseOrder();
        viewModel.RemoveAudioTrack(audio);
        viewModel.Undo();

        Assert.Equal(["c", "a", "b"], SourcePaths(viewModel));
        Assert.Equal([90, 0, 0], viewModel.Slides.Select(s => s.RotationDegrees));
        Assert.Single(viewModel.AudioTracks);

        save.SetResult();
        await saving;
        Assert.True(viewModel.CanEdit);
    }

    [Fact]
    public async Task CancelledSave_UnlocksEditing()
    {
        MainViewModel viewModel = CreateViewModelWithSlides("a", "b");
        var save = new TaskCompletionSource();
        Task saving = viewModel.SaveLockedAsync(() => save.Task);

        save.SetCanceled();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saving);
        Assert.True(viewModel.CanEdit);
        viewModel.MoveSelected([viewModel.Slides[0]], delta: 1);
        Assert.Equal(["b", "a"], SourcePaths(viewModel));
    }

    [Fact]
    public async Task FailedSave_UnlocksEditing()
    {
        MainViewModel viewModel = CreateViewModelWithSlides("a", "b");
        var save = new TaskCompletionSource();
        Task saving = viewModel.SaveLockedAsync(() => save.Task);

        save.SetException(new IOException("disk full"));

        await Assert.ThrowsAsync<IOException>(() => saving);
        Assert.True(viewModel.CanEdit);
        viewModel.RemoveSelected([viewModel.Slides[0]]);
        Assert.Equal(["b"], SourcePaths(viewModel));
    }
}
