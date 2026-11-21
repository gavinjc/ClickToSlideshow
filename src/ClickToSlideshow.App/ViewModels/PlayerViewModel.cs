using System.Diagnostics;
using ClickToSlideshow.App.Diagnostics;
using ClickToSlideshow.Core.Audio;
using ClickToSlideshow.Core.Imaging;
using ClickToSlideshow.Core.Model;
using ClickToSlideshow.Core.Playback;
using ClickToSlideshow.Core.Rendering;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace ClickToSlideshow.App.ViewModels;

/// <summary>
/// Drives the fullscreen player (M3). Renders through the same <see cref="FrameComposer"/> the
/// MP4 exporter uses (spec §4.5) — this class supplies the wall clock and the decoded-bitmap
/// cache, exactly as <see cref="Core.Video.Mp4VideoRenderer"/> does for export, adapted to be
/// interactive/non-blocking instead of a fire-and-forget forward pass. Owns a
/// <see cref="MediaPlayer"/> for audio, kept in sync <b>with</b> the wall clock (spec §4.7: "in
/// sync with the frame clock") rather than the other way around — the wall clock is the single
/// source of truth for the video position, audio just starts/stops/fades alongside it.
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject
{
    private readonly SlideshowProject _project;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Dictionary<int, Task<CanvasBitmap>> _bitmapTasks = new();
    private readonly HashSet<int> _loggedDecodeFailures = new();
    private readonly MediaPlayer? _audioPlayer;
    private int _currentAudioTrackIndex;

    // Set just before a seek whose PlaybackSession.Position write isn't guaranteed to stick —
    // either _audioPlayer.Source was just swapped to a different track, or the player's current
    // Source (including the one set in the constructor) has never fired MediaOpened at all yet —
    // applied in OnAudioOpened once that source is actually ready. Same reason MainViewModel's
    // audio-preview scrubbing (StartPreview/OnPreviewOpened) defers a post-swap seek this way:
    // not verified whether a position set before the media finishes opening is kept.
    private TimeSpan _pendingAudioSeek;

    // False until the current _audioPlayer.Source has fired MediaOpened at least once — a
    // PlaybackSession.Position write before that isn't reliably kept, per _pendingAudioSeek's own
    // comment, and that risk is live not just right after a Source swap but also in the narrow
    // window between construction (which sets an initial Source) and its first MediaOpened.
    private bool _audioSourceReady;

    // A manually seekable, pausable stopwatch: _basePosition holds all accumulated time up to
    // the last play/pause/seek transition, _playStopwatch only measures time since the most
    // recent resume. This is the single source of truth for "now" (spec §4.7's "wall clock") —
    // see CurrentPosition. Deliberately not driven off MediaPlayer.PlaybackSession.Position: audio
    // must track the frame clock, not the reverse — SeekAudioTo reseeks _audioPlayer to match
    // this clock, never the other way around.
    private readonly Stopwatch _playStopwatch = new();
    private TimeSpan _basePosition = TimeSpan.Zero;
    private bool _endedFired;

    public PlayerViewModel(SlideshowProject project, DispatcherQueue dispatcherQueue)
    {
        _project = project;
        _dispatcherQueue = dispatcherQueue;

        if (_project.AudioTracks.Count > 0)
        {
            _audioPlayer = new MediaPlayer();
            // Stay out of Windows' media controls: a pause from there would stop the music while the slides ran on.
            _audioPlayer.CommandManager.IsEnabled = false;
            _audioPlayer.MediaEnded += OnAudioTrackEnded;
            _audioPlayer.MediaOpened += OnAudioOpened;
            _audioPlayer.Source = MediaSource.CreateFromUri(new Uri(_project.AudioTracks[0].PlayablePath));
        }
    }

    /// <summary>Raised (on the UI thread) when playback reaches the end of the show on its own —
    /// the window uses this to reveal its chrome rather than leaving the user stuck looking at a
    /// frozen, hidden-chrome screen.</summary>
    public event EventHandler? Ended;

    [ObservableProperty]
    private bool _isPlaying;

    // Display-only progress (spec's scope decision: no click/drag-to-seek this milestone) — 0..1,
    // updated once per render tick from RenderFrame. The view stays a plain {Binding} consumer of
    // this already-computed value, same principle MainViewModel's runtime bar follows.
    [ObservableProperty]
    private double _progress;

    // User-set volume (0..1), independent of the end-of-show fade — UpdateFadeVolume multiplies
    // the two together every frame, so a mid-fade volume change and a fade both land on
    // _audioPlayer.Volume without either overwriting the other's effect.
    [ObservableProperty]
    private double _volume = 1.0;

    partial void OnIsPlayingChanged(bool value)
    {
        if (value)
        {
            _endedFired = false;
            _playStopwatch.Restart();
            _audioPlayer?.Play();
        }
        else
        {
            _basePosition += _playStopwatch.Elapsed;
            _playStopwatch.Reset();
            _audioPlayer?.Pause();
        }
    }

    public void TogglePlayPause() => IsPlaying = !IsPlaying;

    // ←/→ (spec §4.7) step to the adjacent slide's start, taking the audio along to the
    // equivalent point in the Loop/Trim/FitSlides track sequence (AudioSequencer.Locate).
    public void NextSlide() => SeekToSlide(SlideTimeline.Locate(_project, CurrentPosition()).SlideIndex + 1);

    public void PreviousSlide() => SeekToSlide(SlideTimeline.Locate(_project, CurrentPosition()).SlideIndex - 1);

    private void SeekToSlide(int targetIndex)
    {
        int clamped = Math.Clamp(targetIndex, 0, _project.Slides.Count - 1);
        _basePosition = SlideTimeline.StartTime(_project, clamped);
        if (IsPlaying)
        {
            _playStopwatch.Restart();
        }

        SeekAudioTo(_basePosition);
    }

    private void SeekAudioTo(TimeSpan position)
    {
        if (_audioPlayer is null)
        {
            return;
        }

        var trackDurations = _project.AudioTracks.Select(track => TimeSpan.FromMilliseconds(track.DurationMs)).ToList();
        AudioSequencer.AudioPosition? located = AudioSequencer.Locate(_project.AudioMatch, trackDurations, position);
        if (located is null)
        {
            // Trim/FitSlides landed past the last track (or there's nothing to play) — silence,
            // not a track restarted from 0.
            _audioPlayer.Pause();
            return;
        }

        if (located.Value.TrackIndex != _currentAudioTrackIndex)
        {
            _currentAudioTrackIndex = located.Value.TrackIndex;
            _audioSourceReady = false;
            _pendingAudioSeek = located.Value.Offset;
            _audioPlayer.Source = MediaSource.CreateFromUri(new Uri(_project.AudioTracks[_currentAudioTrackIndex].PlayablePath));
        }
        else if (_audioSourceReady)
        {
            _audioPlayer.PlaybackSession.Position = located.Value.Offset;
        }
        else
        {
            // Same track, but its Source has never fired MediaOpened yet (only reachable right
            // after construction) — a direct Position write here isn't reliably kept either.
            _pendingAudioSeek = located.Value.Offset;
        }

        if (IsPlaying)
        {
            _audioPlayer.Play();
        }
        else
        {
            _audioPlayer.Pause();
        }
    }

    // Mirrors MainViewModel.OnPreviewOpened: a position set on _audioPlayer before its Source
    // finishes opening isn't reliably kept, so a seek that can't be sure the source is ready
    // (a cross-track swap, or one that lands before the very first MediaOpened) stashes the
    // offset here and applies it once MediaOpened actually fires.
    private void OnAudioOpened(MediaPlayer sender, object args)
    {
        _audioSourceReady = true;
        if (_pendingAudioSeek > TimeSpan.Zero)
        {
            sender.PlaybackSession.Position = _pendingAudioSeek;
        }

        _pendingAudioSeek = TimeSpan.Zero;
    }

    private TimeSpan CurrentPosition() => IsPlaying ? _basePosition + _playStopwatch.Elapsed : _basePosition;

    /// <summary>
    /// Called from <c>PlayerWindow</c>'s render-timer tick on the UI thread. Draws the current
    /// frame if its bitmaps are ready, or just clears to the background colour and holds the
    /// previous frame's content otherwise — decoding is fire-and-forget from here, not awaited,
    /// so a slow decode never stalls the render loop. <see cref="PublishProgress"/> still
    /// marshals through <c>_dispatcherQueue</c> even though this already runs on the UI thread,
    /// matching the same discipline <c>MainViewModel</c> uses for its own background decode work
    /// — cheap insurance if the render loop ever moves to a background thread later.
    /// </summary>
    public void RenderFrame(ICanvasResourceCreator resourceCreator, CanvasDrawingSession session, Size targetSize)
    {
        TimeSpan t = CurrentPosition();
        if (t >= _project.Runtime)
        {
            t = _project.Runtime;
            RaiseEndedOnce();
        }

        SlidePosition position = SlideTimeline.Locate(_project, t);
        CanvasBitmap? current = BitmapForSlide(resourceCreator, position.SlideIndex);

        // Prefetch the following slide as soon as the current one starts showing, not only once
        // the transition window begins — that window is the tail end of a slide's own duration,
        // leaving only a few hundred ms for a real decode to finish. Started this early, decode
        // has the whole slide's duration as lead time instead.
        int upcomingIndex = position.SlideIndex + 1;
        bool hasUpcoming = upcomingIndex < _project.Slides.Count;
        if (hasUpcoming)
        {
            BitmapForSlide(resourceCreator, upcomingIndex);
        }

        if (current is null)
        {
            // Nothing decoded yet at all (only possible for the very first frame of the whole
            // show) — nothing to hold, so clear rather than present uninitialized surface content.
            session.Clear(_project.BackgroundColor);
            return;
        }

        CanvasBitmap? next = position.NextSlideIndex is { } nextIndex ? BitmapForSlide(resourceCreator, nextIndex) : null;
        if (position.NextSlideIndex is not null && next is null)
        {
            // The incoming slide hasn't finished decoding yet (rare with the prefetch above, but
            // possible for a very short slide duration or a slow first decode). Don't draw or
            // clear — PlayerWindow's render target already holds the current slide's last-drawn
            // frame, and simply not touching it here keeps that on screen instead of flashing to
            // the background colour.
            return;
        }

        FrameComposer.Render(session, _project, t, index => index == position.SlideIndex ? current : next!, targetSize);
        ReleaseBitmapsExcept(hasUpcoming ? [position.SlideIndex, upcomingIndex] : [position.SlideIndex]);
        UpdateFadeVolume(t);
        PublishProgress(t);
    }

    private CanvasBitmap? BitmapForSlide(ICanvasResourceCreator resourceCreator, int index)
    {
        if (!_bitmapTasks.TryGetValue(index, out Task<CanvasBitmap>? task))
        {
            task = SlideImageLoader.LoadBitmapAsync(resourceCreator, _project.Slides[index].SourcePath);
            _bitmapTasks[index] = task;
        }

        // A faulted task (missing/moved/corrupt source file) leaves this slide permanently
        // blank for the rest of the session — same per-item "don't crash the whole show over one
        // bad file" tradeoff MainViewModel.LoadSlideAsync makes for the grid, but logged once
        // rather than silently swallowed, so a bad file is at least diagnosable after the fact.
        if (task.IsFaulted && _loggedDecodeFailures.Add(index))
        {
            AppLog.LogWarning("PlayerViewModel.BitmapForSlide", $"{_project.Slides[index].SourcePath}: {task.Exception}");
        }

        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    // Mirrors Mp4VideoRenderer.ReleaseBitmapsExcept's LRU-of-two rule (spec §4.5) — not shared
    // with it directly: that one evicts from a plain bitmap dictionary on a single forward decode
    // pass, this one evicts from a dictionary of in-flight decode Tasks to support the player's
    // non-blocking prefetch, different enough in shape that merging them would cost more than the
    // ~10 duplicated lines it would save.
    private void ReleaseBitmapsExcept(int[] keep)
    {
        foreach (int index in _bitmapTasks.Keys.ToArray())
        {
            if (keep.Contains(index))
            {
                continue;
            }

            if (_bitmapTasks[index].IsCompletedSuccessfully)
            {
                _bitmapTasks[index].Result.Dispose();
            }

            _bitmapTasks.Remove(index);
        }
    }

    // Always-on ramp rather than branching per AudioMatch: harmless (and inaudible) once the
    // track has already gone silent on its own (Trim's shorter-than-show case), and correctly
    // covers every case the spec §4.2 table calls for a fade — Loop (always) and Trim/FitSlides'
    // longer-than-show cases, where the track would otherwise hard-cut at the runtime boundary.
    // Shares AudioFadeOut.Duration with the MP4/PPTX save paths' own fade-out (AudioFadeOut,
    // FadedSoundtrackRenderer) so live playback and a saved export always fade at the same rate.
    private void UpdateFadeVolume(TimeSpan t)
    {
        if (_audioPlayer is null)
        {
            return;
        }

        TimeSpan remaining = _project.Runtime - t;
        double fade = remaining < AudioFadeOut.Duration
            ? Math.Clamp(remaining.TotalSeconds / AudioFadeOut.Duration.TotalSeconds, 0, 1)
            : 1.0;
        _audioPlayer.Volume = Volume * fade;
    }

    private void PublishProgress(TimeSpan t)
    {
        double progress = _project.Runtime.TotalMilliseconds <= 0
            ? 0
            : Math.Clamp(t.TotalMilliseconds / _project.Runtime.TotalMilliseconds, 0, 1);
        _dispatcherQueue.TryEnqueue(() => Progress = progress);
    }

    private void RaiseEndedOnce()
    {
        if (_endedFired)
        {
            return;
        }

        _endedFired = true;
        _dispatcherQueue.TryEnqueue(() =>
        {
            IsPlaying = false;
            Ended?.Invoke(this, EventArgs.Empty);
        });
    }

    // MediaPlayer.MediaEnded fires on a WinRT callback thread, not the UI thread — but only
    // touches _audioPlayer/_currentAudioTrackIndex here, neither of which the UI thread writes,
    // so no dispatcher marshalling is needed for this handler specifically.
    private void OnAudioTrackEnded(MediaPlayer sender, object args)
    {
        int? next = AudioSequencer.Advance(
            _project.AudioMatch, _project.AudioTracks.Count, _currentAudioTrackIndex, CurrentPosition(), _project.Runtime);

        if (next is not int nextIndex)
        {
            return;
        }

        _currentAudioTrackIndex = nextIndex;
        sender.Source = MediaSource.CreateFromUri(new Uri(_project.AudioTracks[nextIndex].PlayablePath));
        sender.Play();
    }

    public void Cleanup()
    {
        foreach (Task<CanvasBitmap> task in _bitmapTasks.Values)
        {
            if (task.IsCompletedSuccessfully)
            {
                task.Result.Dispose();
            }
        }

        _bitmapTasks.Clear();

        if (_audioPlayer is not null)
        {
            _audioPlayer.MediaEnded -= OnAudioTrackEnded;
            _audioPlayer.MediaOpened -= OnAudioOpened;
            _audioPlayer.Dispose();
        }
    }
}
