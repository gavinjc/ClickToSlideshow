using ClickToSlideshow.Core.Model;
using ClickToSlideshow.Core.Playback;

namespace ClickToSlideshow.Core.Tests;

// Covers the pure Loop/Trim/FitSlides sequencing logic only. PlayerViewModel's own consumption
// of it — SeekAudioTo's same-track/cross-track/silence branches, the OnAudioOpened deferred-seek
// handshake, and Volume's multiplication against the end-of-show fade — all require a live
// MediaPlayer, a WinRT COM activation that throws in this unbootstrapped xunit host (the same
// constraint ClickToSlideshow.App.Tests.AudioPreviewScrubTests documents for MainViewModel's own
// preview player) and so are checked by hand instead.
public class AudioSequencerTests
{
    [Fact]
    public void Advance_MidPlaylist_ReturnsNextTrackInOrder()
    {
        int? next = AudioSequencer.Advance(
            AudioMatch.Loop, trackCount: 3, endedTrackIndex: 0,
            elapsed: TimeSpan.FromSeconds(1), runtime: TimeSpan.FromSeconds(10));

        Assert.Equal(1, next);
    }

    [Fact]
    public void Advance_LoopPastLastTrackBeforeRuntimeEnds_WrapsToFirstTrack()
    {
        int? next = AudioSequencer.Advance(
            AudioMatch.Loop, trackCount: 3, endedTrackIndex: 2,
            elapsed: TimeSpan.FromSeconds(4.5), runtime: TimeSpan.FromSeconds(10));

        Assert.Equal(0, next);
    }

    [Fact]
    public void Advance_LoopOnceRuntimeReached_StopsInsteadOfWrapping()
    {
        int? next = AudioSequencer.Advance(
            AudioMatch.Loop, trackCount: 3, endedTrackIndex: 2,
            elapsed: TimeSpan.FromSeconds(10), runtime: TimeSpan.FromSeconds(10));

        Assert.Null(next);
    }

    [Fact]
    public void Advance_TrimPastLastTrack_StopsRegardlessOfRemainingRuntime()
    {
        int? next = AudioSequencer.Advance(
            AudioMatch.Trim, trackCount: 3, endedTrackIndex: 2,
            elapsed: TimeSpan.FromSeconds(4.5), runtime: TimeSpan.FromSeconds(10));

        Assert.Null(next);
    }

    [Fact]
    public void Advance_FitSlidesPastLastTrack_StopsLikeTrim()
    {
        int? next = AudioSequencer.Advance(
            AudioMatch.FitSlides, trackCount: 2, endedTrackIndex: 1,
            elapsed: TimeSpan.FromSeconds(1), runtime: TimeSpan.FromSeconds(10));

        Assert.Null(next);
    }

    [Fact]
    public void Advance_SingleTrackLoop_RestartsSameTrack()
    {
        int? next = AudioSequencer.Advance(
            AudioMatch.Loop, trackCount: 1, endedTrackIndex: 0,
            elapsed: TimeSpan.FromSeconds(1), runtime: TimeSpan.FromSeconds(10));

        Assert.Equal(0, next);
    }

    private static readonly TimeSpan[] TwoTenSecondTracks = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)];

    [Fact]
    public void Locate_WithinFirstTrack_ReturnsItsOwnOffset()
    {
        AudioSequencer.AudioPosition? position =
            AudioSequencer.Locate(AudioMatch.Trim, TwoTenSecondTracks, TimeSpan.FromSeconds(4));

        Assert.Equal(new AudioSequencer.AudioPosition(0, TimeSpan.FromSeconds(4)), position);
    }

    [Fact]
    public void Locate_WithinSecondTrack_ReturnsOffsetPastFirstTrack()
    {
        AudioSequencer.AudioPosition? position =
            AudioSequencer.Locate(AudioMatch.Trim, TwoTenSecondTracks, TimeSpan.FromSeconds(14));

        Assert.Equal(new AudioSequencer.AudioPosition(1, TimeSpan.FromSeconds(4)), position);
    }

    [Fact]
    public void Locate_TrimPastLastTrack_ReturnsNullForSilence()
    {
        AudioSequencer.AudioPosition? position =
            AudioSequencer.Locate(AudioMatch.Trim, TwoTenSecondTracks, TimeSpan.FromSeconds(25));

        Assert.Null(position);
    }

    [Fact]
    public void Locate_FitSlidesPastLastTrack_ReturnsNullLikeTrim()
    {
        AudioSequencer.AudioPosition? position =
            AudioSequencer.Locate(AudioMatch.FitSlides, TwoTenSecondTracks, TimeSpan.FromSeconds(25));

        Assert.Null(position);
    }

    [Fact]
    public void Locate_LoopPastLastTrack_WrapsIntoFirstTrackInstead()
    {
        AudioSequencer.AudioPosition? position =
            AudioSequencer.Locate(AudioMatch.Loop, TwoTenSecondTracks, TimeSpan.FromSeconds(25));

        Assert.Equal(new AudioSequencer.AudioPosition(0, TimeSpan.FromSeconds(5)), position);
    }

    [Fact]
    public void Locate_LoopManyCyclesIn_StillWrapsToCorrectTrackAndOffset()
    {
        AudioSequencer.AudioPosition? position =
            AudioSequencer.Locate(AudioMatch.Loop, TwoTenSecondTracks, TimeSpan.FromSeconds(76));

        // 76s / 20s-per-loop = 3 full loops (60s) + 16s in; 16s lands 6s into the second track.
        Assert.Equal(new AudioSequencer.AudioPosition(1, TimeSpan.FromSeconds(6)), position);
    }

    [Fact]
    public void Locate_NoTracks_ReturnsNullForSilence()
    {
        AudioSequencer.AudioPosition? position =
            AudioSequencer.Locate(AudioMatch.Loop, [], TimeSpan.FromSeconds(1));

        Assert.Null(position);
    }
}
