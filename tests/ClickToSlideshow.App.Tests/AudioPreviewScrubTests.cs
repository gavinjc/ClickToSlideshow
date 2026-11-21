using ClickToSlideshow.App.ViewModels;
using ClickToSlideshow.Core.Model;

namespace ClickToSlideshow.App.Tests;

// Only the paths that never touch a MediaPlayer: a track that isn't loaded in the preview player
// can be scrubbed and cancelled without one. The playing/paused/seek-while-loaded paths need a
// real player and are checked by hand.
public class AudioPreviewScrubTests
{
    private static AudioTrackItem TrackOfDuration(int durationMs) =>
        new(new AudioTrack { SourcePath = "song.mp3", DurationMs = durationMs });

    [Fact]
    public void CancelScrub_TrackNotLoaded_HidesTheIndicatorAgain()
    {
        var viewModel = new MainViewModel(null!);
        AudioTrackItem track = TrackOfDuration(60_000);
        viewModel.ScrubPreview(track, 0.4);
        Assert.True(track.HasPlaybackPosition);
        Assert.Equal(0.4, track.PlaybackProgress);

        viewModel.CancelScrub(track);

        Assert.False(track.HasPlaybackPosition);
        Assert.Equal(0.0, track.PlaybackProgress);
    }

    [Fact]
    public void CancelScrub_ForADifferentTrackThanTheOneScrubbed_LeavesTheScrubAlone()
    {
        var viewModel = new MainViewModel(null!);
        AudioTrackItem scrubbed = TrackOfDuration(60_000);
        AudioTrackItem other = TrackOfDuration(60_000);
        viewModel.ScrubPreview(scrubbed, 0.4);

        viewModel.CancelScrub(other);

        Assert.True(scrubbed.HasPlaybackPosition);
        Assert.Equal(0.4, scrubbed.PlaybackProgress);
    }

    [Fact]
    public void SeekPreview_TrackWithNoDuration_DoesNotLeaveAStuckIndicator()
    {
        var viewModel = new MainViewModel(null!);
        AudioTrackItem track = TrackOfDuration(0);
        viewModel.ScrubPreview(track, 0.4);

        viewModel.SeekPreview(track, 0.4);

        Assert.False(track.HasPlaybackPosition);
        Assert.Equal(0.0, track.PlaybackProgress);
    }
}
