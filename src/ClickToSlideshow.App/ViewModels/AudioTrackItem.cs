using ClickToSlideshow.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClickToSlideshow.App.ViewModels;

// One row in the audio strip. AudioTrack itself is immutable (SourcePath/DurationMs are both
// init-only), but this wrapper is no longer just a plain display shim — TrackNumber changes on
// reorder, IsPlaying changes on preview, and Peaks arrives asynchronously after construction
// (audio-section-brief.md §3.5), so it needs to be observable like SlideItem.
public partial class AudioTrackItem : ObservableObject
{
    // Bucket count for the waveform — matches AudioPeakExtractor's own "200-400 buckets is ample
    // at this size" guidance (brief §3.3); the placeholder array below uses the same length so
    // swapping in real Peaks never changes the card's rendered width.
    public const int PeakBucketCount = 300;

    public AudioTrackItem(AudioTrack track)
    {
        Track = track;
        FileName = Path.GetFileName(track.SourcePath);
        _peaks = FlatPlaceholderPeaks();
        WaveformBarHeights = ComputeBarHeights(_peaks);
    }

    public Guid Id { get; } = Guid.NewGuid();

    public AudioTrack Track { get; }

    public string PlayablePath => Track.PlayablePath;

    public string FileName { get; }

    public int DurationMs => Track.DurationMs;

    public string FormattedDuration => MainViewModel.FormatDuration(DurationMs);

    // Kept in sync by MainViewModel.RenumberAudioTracks after every reorder/add/remove — not
    // authoritative on its own, same relationship SlideItem.SlideNumber has to its collection.
    [ObservableProperty]
    private int _trackNumber;

    // Mirrors whether this track's MediaPlayer preview is currently running (not paused) —
    // MainViewModel is the only writer (see TogglePreview), same pattern as SlideItem.IsSelected.
    [ObservableProperty]
    private bool _isPlaying;

    // True while the track is loaded in the preview player, playing or paused, or being scrubbed:
    // the position indicator shows whenever there is a position to show.
    [ObservableProperty]
    private bool _hasPlaybackPosition;

    // 0..1 fraction through the preview, polled from MediaPlayer.PlaybackSession.Position by
    // MainViewModel's preview-progress timer — same reason PlayerWindow (M3) polls on a timer
    // rather than using PositionChanged: a smooth moving indicator needs a steady sample rate,
    // not whatever cadence the OS happens to raise position events at. Reset to 0 on stop, not
    // left wherever playback happened to end, so replaying always starts the indicator fresh.
    // Also set directly while the user scrubs the waveform.
    [ObservableProperty]
    private double _playbackProgress;

    // Natural pixel width of the waveform (PeakBucketCount bars at the XAML template's own
    // Rectangle Width="2"/StackPanel Spacing="1" in MainWindow.xaml) — the XAML lays the waveform
    // and indicator out at this width inside a Viewbox that scales them to the card, so the
    // indicator's offset is computed in this space. Keep in sync with the XAML values if either changes.
    private const double WaveformBarWidth = 2.0;
    private const double WaveformBarSpacing = 1.0;
    public const double WaveformWidth = (PeakBucketCount * WaveformBarWidth) + ((PeakBucketCount - 1) * WaveformBarSpacing);

    // Starts as a flat placeholder (never null) so the card always has something to render
    // immediately; MainViewModel swaps in the real decode result once AudioPeakExtractor
    // finishes, off the UI thread. A flat placeholder is not a "flag" the template branches on —
    // it draws as a low, uniform line, visually distinct from a real waveform without needing a
    // separate HasPeaks visibility switch.
    [ObservableProperty]
    private float[] _peaks;

    partial void OnPeaksChanged(float[] value)
    {
        WaveformBarHeights = ComputeBarHeights(value);
        OnPropertyChanged(nameof(WaveformBarHeights));
    }

    // Derived display heights for the card's waveform ItemsControl (brief §3.3) — kept as its
    // own property rather than a converter so the XAML side stays a plain ItemsSource binding.
    private const double WaveformBarMaxHeight = 28.0;
    private const double WaveformBarMinHeight = 2.0;

    public IReadOnlyList<double> WaveformBarHeights { get; private set; }

    private static IReadOnlyList<double> ComputeBarHeights(float[] peaks) =>
        peaks.Select(p => Math.Max(WaveformBarMinHeight, p * WaveformBarMaxHeight)).ToList();

    private static float[] FlatPlaceholderPeaks()
    {
        var peaks = new float[PeakBucketCount];
        Array.Fill(peaks, 0.08f);
        return peaks;
    }
}
