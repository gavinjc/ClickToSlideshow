using ClickToSlideshow.App.ViewModels;
using ClickToSlideshow.Core.Model;

namespace ClickToSlideshow.App.Tests;

// MoveSelected/RemoveSelected's index arithmetic (multi-select iteration order, undo
// reinsertion at the original index) is easy to get subtly wrong and reachable without a live
// DispatcherQueue — MainViewModel's constructor only stores it, and neither method under test
// touches it (only LoadSlideAsync's decode callback does).
public class MainViewModelReorderTests
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
    public void MoveSelected_SingleItemRight_MovesOneSlotRight()
    {
        MainViewModel viewModel = CreateViewModelWithSlides("a", "b", "c");
        SlideItem b = viewModel.Slides[1];

        viewModel.MoveSelected([b], delta: 1);

        Assert.Equal(["a", "c", "b"], SourcePaths(viewModel));
    }

    [Fact]
    public void MoveSelected_ItemAlreadyAtBoundary_LeavesOrderUnchanged()
    {
        MainViewModel viewModel = CreateViewModelWithSlides("a", "b", "c");
        SlideItem c = viewModel.Slides[2];

        viewModel.MoveSelected([c], delta: 1);

        Assert.Equal(["a", "b", "c"], SourcePaths(viewModel));
    }

    // Regression guard for the reverse-iteration-when-shifting-right requirement: moving two
    // selected items right by one slot must preserve their relative order (A before B), not
    // swap them or clobber one with the other's index mid-move.
    [Fact]
    public void MoveSelected_TwoAdjacentItemsRight_PreservesRelativeOrder()
    {
        MainViewModel viewModel = CreateViewModelWithSlides("a", "b", "c");
        SlideItem a = viewModel.Slides[0];
        SlideItem b = viewModel.Slides[1];

        viewModel.MoveSelected([a, b], delta: 1);

        Assert.Equal(["c", "a", "b"], SourcePaths(viewModel));
    }

    [Fact]
    public void MoveSelected_ThenUndo_RestoresOriginalOrder()
    {
        MainViewModel viewModel = CreateViewModelWithSlides("a", "b", "c");
        SlideItem b = viewModel.Slides[1];
        viewModel.MoveSelected([b], delta: 1);

        viewModel.Undo();

        Assert.Equal(["a", "b", "c"], SourcePaths(viewModel));
    }

    [Fact]
    public void RemoveSelected_RemovesOnlyTheSelectedItem()
    {
        MainViewModel viewModel = CreateViewModelWithSlides("a", "b", "c");
        SlideItem b = viewModel.Slides[1];

        viewModel.RemoveSelected([b]);

        Assert.Equal(["a", "c"], SourcePaths(viewModel));
    }

    // Regression guard for the "insert at Math.Min(originalIndex, Count)" undo logic: removing
    // two non-adjacent items and undoing must reinsert each at its own original index, not just
    // append both at the end or swap their positions.
    [Fact]
    public void RemoveSelected_MultipleNonAdjacent_ThenUndo_ReinsertsAtOriginalIndices()
    {
        MainViewModel viewModel = CreateViewModelWithSlides("a", "b", "c");
        SlideItem a = viewModel.Slides[0];
        SlideItem c = viewModel.Slides[2];
        viewModel.RemoveSelected([a, c]);
        Assert.Equal(["b"], SourcePaths(viewModel));

        viewModel.Undo();

        Assert.Equal(["a", "b", "c"], SourcePaths(viewModel));
    }

    // The grid and audio list reorder by RemoveAt then Insert, never Move — the shape these tests
    // replay. Undo used to listen only for Move, so a drag could never be undone.
    private static void DragWithin<T>(IList<T> items, int from, int to)
    {
        T item = items[from];
        items.RemoveAt(from);
        items.Insert(to, item);
    }

    // Gavin's 2026-11-04 retest: a change, then a drag, then Ctrl+Z undid the change before the
    // drag, because the drag had recorded nothing. The first undo must take back only the drag and
    // the second the change. A real add needs files on disk and a dispatcher for thumbnails and
    // waveforms, so the change before the drag here is a rotate (slides) and a remove (tracks):
    // either is an undo step the drag must sit on top of.
    [Fact]
    public void SlideDrag_AfterAnotherChange_IsItsOwnUndoStep()
    {
        MainViewModel viewModel = CreateViewModelWithSlides("a", "b", "c");
        viewModel.RotateSelected([viewModel.Slides[0]], clockwise: true);

        DragWithin(viewModel.Slides, from: 0, to: 2);
        Assert.Equal(["b", "c", "a"], SourcePaths(viewModel));

        viewModel.Undo();
        Assert.Equal(["a", "b", "c"], SourcePaths(viewModel));
        Assert.Equal(90, viewModel.Slides[0].RotationDegrees);

        viewModel.Undo();
        Assert.Equal(0, viewModel.Slides[0].RotationDegrees);
    }

    // The grid's Extended selection drags every selected tile, a Remove/Insert pair each. One Undo
    // must take back the whole drag, and the next must reach the step before it, not part of the drag.
    [Fact]
    public void MultiTileSlideDrag_IsOneUndoStep()
    {
        MainViewModel viewModel = CreateViewModelWithSlides("a", "b", "c", "d");
        viewModel.RotateSelected([viewModel.Slides[0]], clockwise: true);

        viewModel.BeginSlideDrag();
        DragWithin(viewModel.Slides, from: 0, to: 3);
        DragWithin(viewModel.Slides, from: 0, to: 3);
        viewModel.EndSlideDrag();
        Assert.Equal(["c", "d", "a", "b"], SourcePaths(viewModel));

        viewModel.Undo();
        Assert.Equal(["a", "b", "c", "d"], SourcePaths(viewModel));
        Assert.Equal(90, viewModel.Slides[0].RotationDegrees);

        viewModel.Undo();
        Assert.Equal(0, viewModel.Slides[0].RotationDegrees);
    }

    // EndSlideDrag/BeginSlideDrag must re-arm the guard, or the second drag records nothing and its
    // Undo takes back the first drag instead.
    [Fact]
    public void TwoMultiTileSlideDragsInARow_AreAnUndoStepEach()
    {
        MainViewModel viewModel = CreateViewModelWithSlides("a", "b", "c", "d");

        viewModel.BeginSlideDrag();
        DragWithin(viewModel.Slides, from: 0, to: 3);
        DragWithin(viewModel.Slides, from: 0, to: 3);
        viewModel.EndSlideDrag();
        Assert.Equal(["c", "d", "a", "b"], SourcePaths(viewModel));

        viewModel.BeginSlideDrag();
        DragWithin(viewModel.Slides, from: 0, to: 3);
        DragWithin(viewModel.Slides, from: 0, to: 3);
        viewModel.EndSlideDrag();
        Assert.Equal(["a", "b", "c", "d"], SourcePaths(viewModel));

        viewModel.Undo();
        Assert.Equal(["c", "d", "a", "b"], SourcePaths(viewModel));

        viewModel.Undo();
        Assert.Equal(["a", "b", "c", "d"], SourcePaths(viewModel));
    }

    // The one-step-per-drag guard applies only between BeginSlideDrag and EndSlideDrag; a Remove
    // after it must still record its own step, every time.
    [Fact]
    public void SlideRemovesOutsideADrag_EachRecordAStep()
    {
        MainViewModel viewModel = CreateViewModelWithSlides("a", "b", "c");
        viewModel.BeginSlideDrag();
        DragWithin(viewModel.Slides, from: 0, to: 2);
        viewModel.EndSlideDrag();
        Assert.Equal(["b", "c", "a"], SourcePaths(viewModel));

        DragWithin(viewModel.Slides, from: 0, to: 2);
        DragWithin(viewModel.Slides, from: 0, to: 2);
        Assert.Equal(["a", "b", "c"], SourcePaths(viewModel));

        viewModel.Undo();
        Assert.Equal(["c", "a", "b"], SourcePaths(viewModel));

        viewModel.Undo();
        Assert.Equal(["b", "c", "a"], SourcePaths(viewModel));

        viewModel.Undo();
        Assert.Equal(["a", "b", "c"], SourcePaths(viewModel));
    }

    [Fact]
    public void AudioTrackDrag_AfterAnotherChange_IsItsOwnUndoStep()
    {
        var viewModel = new MainViewModel(null!);
        foreach (string path in (string[])["one.mp3", "two.mp3", "three.mp3"])
        {
            viewModel.AudioTracks.Add(new AudioTrackItem(new AudioTrack { SourcePath = path, DurationMs = 1000 }));
        }
        viewModel.RemoveAudioTrack(viewModel.AudioTracks[0]);

        DragWithin(viewModel.AudioTracks, from: 1, to: 0);
        Assert.Equal(["three.mp3", "two.mp3"], viewModel.AudioTracks.Select(t => t.FileName));

        viewModel.Undo();
        Assert.Equal(["two.mp3", "three.mp3"], viewModel.AudioTracks.Select(t => t.FileName));
        Assert.Equal([1, 2], viewModel.AudioTracks.Select(t => t.TrackNumber));

        viewModel.Undo();
        Assert.Equal(["one.mp3", "two.mp3", "three.mp3"], viewModel.AudioTracks.Select(t => t.FileName));
    }

    [Fact]
    public void SetDuration_OnSeveralSlides_IsOneUndoStep()
    {
        MainViewModel viewModel = CreateViewModelWithSlides("a", "b", "c");
        viewModel.SetDuration([viewModel.Slides[0]], 2000);

        viewModel.SetDuration([viewModel.Slides[0], viewModel.Slides[1]], 6000);
        Assert.Equal([6000, 6000, null], viewModel.Slides.Select(s => s.DurationMs));

        viewModel.Undo();
        Assert.Equal([2000, null, null], viewModel.Slides.Select(s => s.DurationMs));

        viewModel.Undo();
        Assert.Equal([null, null, null], viewModel.Slides.Select(s => s.DurationMs));
    }

    // The duration box raises ValueChanged again when an undo puts its bound value back; that echo
    // sets the value the slide already has and must not record a step that blocks the next undo.
    [Fact]
    public void SetDuration_ToTheCurrentValue_RecordsNoUndoStep()
    {
        MainViewModel viewModel = CreateViewModelWithSlides("a", "b");
        viewModel.RotateSelected([viewModel.Slides[0]], clockwise: true);

        viewModel.SetDuration([viewModel.Slides[0]], null);
        viewModel.Undo();

        Assert.Equal(0, viewModel.Slides[0].RotationDegrees);
    }

    [Fact]
    public void SetDuration_ThenUndo_PutsTheRuntimeBack()
    {
        MainViewModel viewModel = CreateViewModelWithSlides("a", "b");
        int runtimeBefore = viewModel.RuntimeMs;

        viewModel.SetDuration([viewModel.Slides[0]], viewModel.DefaultSlideMs + 3000);
        Assert.Equal(runtimeBefore + 3000, viewModel.RuntimeMs);

        viewModel.Undo();
        Assert.Equal(runtimeBefore, viewModel.RuntimeMs);
    }

    [Fact]
    public void SetTransitionIn_ThenUndo_RestoresEachSlidesOwnTransition()
    {
        MainViewModel viewModel = CreateViewModelWithSlides("a", "b");
        viewModel.SetTransitionIn([viewModel.Slides[0]], TransitionKind.Cut);

        viewModel.SetTransitionIn([viewModel.Slides[0], viewModel.Slides[1]], TransitionKind.PushLeft);
        viewModel.Undo();

        Assert.Equal([TransitionKind.Cut, null], viewModel.Slides.Select(s => s.TransitionIn));
    }
}
