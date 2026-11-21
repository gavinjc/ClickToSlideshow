using ClickToSlideshow.Core.Model;

namespace ClickToSlideshow.Core.Playback;

/// <summary>
/// Decides which audio track should play next when one finishes during live playback (M3),
/// live-mirroring the export muxer's Loop/Trim semantics (spec §4.2) but triggered off
/// <c>MediaPlayer.MediaEnded</c> instead of precomputed — see
/// <see cref="Audio.MediaCompositionAudioMuxer"/>'s <c>AddLoopedToFillAsync</c>/
/// <c>AddSequentiallyAsync</c> for the export-side equivalent. A pure function, independently
/// testable without a live <c>MediaPlayer</c> — mirrors <see cref="Rendering.SlideTimeline"/>'s
/// role for the video side.
/// </summary>
public static class AudioSequencer
{
    /// <returns>The index of the track to play next, or <see langword="null"/> to stop and play
    /// silence for the remainder of the show.</returns>
    public static int? Advance(AudioMatch audioMatch, int trackCount, int endedTrackIndex, TimeSpan elapsed, TimeSpan runtime)
    {
        if (elapsed >= runtime)
        {
            return null;
        }

        int nextIndex = endedTrackIndex + 1;
        if (nextIndex < trackCount)
        {
            return nextIndex;
        }

        return audioMatch == AudioMatch.Loop ? 0 : null;
    }

    public readonly record struct AudioPosition(int TrackIndex, TimeSpan Offset);

    /// <summary>
    /// Which track (and offset into it) should be sounding at <paramref name="elapsed"/> — the
    /// same Loop/Trim/FitSlides concatenation <see cref="Advance"/> walks one MediaEnded event at
    /// a time, but resolved directly for an arbitrary point, so a seek (PlayerViewModel's
    /// ←/→ skip) can jump the audio there in one step instead of replaying every track boundary
    /// in between. Returns <see langword="null"/> for silence: no tracks, or (Trim/FitSlides only)
    /// past the end of the concatenated tracks.
    /// </summary>
    public static AudioPosition? Locate(AudioMatch audioMatch, IReadOnlyList<TimeSpan> trackDurations, TimeSpan elapsed)
    {
        TimeSpan total = TimeSpan.Zero;
        foreach (TimeSpan duration in trackDurations)
        {
            total += duration;
        }

        if (total <= TimeSpan.Zero)
        {
            return null;
        }

        // Loop wraps indefinitely; Trim/FitSlides never repeat, so a position past the last
        // track's end means the audio has already finished for good.
        TimeSpan position = audioMatch == AudioMatch.Loop
            ? TimeSpan.FromTicks(((elapsed.Ticks % total.Ticks) + total.Ticks) % total.Ticks)
            : elapsed;

        if (position >= total)
        {
            return null;
        }

        TimeSpan cumulative = TimeSpan.Zero;
        for (int i = 0; i < trackDurations.Count; i++)
        {
            TimeSpan next = cumulative + trackDurations[i];
            if (position < next)
            {
                return new AudioPosition(i, position - cumulative);
            }

            cumulative = next;
        }

        return null; // Unreachable: position < total was already checked above.
    }
}
