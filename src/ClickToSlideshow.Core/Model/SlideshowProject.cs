using Windows.UI;

namespace ClickToSlideshow.Core.Model;

public enum TransitionKind
{
    Cut,
    Crossfade,
    FadeThroughBlack,
    PushLeft,
    PushRight,
    PushUp,
    PushDown,
}

public enum Resolution
{
    Original,
    HD1080p,
    UHD2160p,
}

public enum FitMode
{
    Fit,
    Fill,
}

public enum AudioMatch
{
    Loop,
    Trim,
    FitSlides,
}

public sealed class Slide
{
    public required string SourcePath { get; init; }

    public int? DurationMs { get; set; }

    public int RotationDegrees { get; set; }

    public TransitionKind? TransitionIn { get; set; }
}

public sealed class AudioTrack
{
    private readonly string? _playablePath;

    /// <summary>The user's file. This is what project files and PPTX metadata point at.</summary>
    public required string SourcePath { get; init; }

    public required int DurationMs { get; init; }

    /// <summary>
    /// The file Media Foundation can open: <see cref="SourcePath"/> itself, unless it's a format
    /// Windows can't decode (Ogg/Opus), in which case a decoded temp copy. Everything that plays,
    /// mixes or measures audio reads this; only persistence reads <see cref="SourcePath"/>.
    /// </summary>
    public string PlayablePath
    {
        get => _playablePath ?? SourcePath;
        init => _playablePath = value;
    }
}

public sealed class SlideshowProject
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public List<Slide> Slides { get; init; } = [];

    public List<AudioTrack> AudioTracks { get; init; } = [];

    public int DefaultSlideMs { get; set; } = 4000;

    public TransitionKind TransitionKind { get; set; } = TransitionKind.Crossfade;

    public int TransitionMs { get; set; } = 1000;

    public Resolution Resolution { get; set; } = Resolution.HD1080p;

    public FitMode FitMode { get; set; } = FitMode.Fit;

    // Built directly rather than via Microsoft.UI.Colors.Black: that named-color helper is a
    // WinRT static class requiring WindowsAppSDK COM activation, which fails with
    // REGDB_E_CLASSNOTREG in an unbootstrapped host (an xunit test runner, here) — found by
    // running the test suite below. Color itself is a plain blittable struct, so building it
    // directly needs no activation at all.
    public Color BackgroundColor { get; set; } = new() { A = 255, R = 0, G = 0, B = 0 };

    public AudioMatch AudioMatch { get; set; } = AudioMatch.Loop;

    /// <summary>
    /// Total show runtime: transitions are additive, not carved out of slide duration
    /// (spec §4.2) — a slide is fully visible for its stated duration.
    /// </summary>
    public TimeSpan Runtime
    {
        get
        {
            if (Slides.Count == 0)
            {
                return TimeSpan.Zero;
            }

            long slideMs = Slides.Sum(slide => (long)(slide.DurationMs ?? DefaultSlideMs));
            long transitionMs = (long)(Slides.Count - 1) * TransitionMs;
            return TimeSpan.FromMilliseconds(slideMs + transitionMs);
        }
    }

    public TransitionKind TransitionInto(int slideIndex)
    {
        return Slides[slideIndex].TransitionIn ?? TransitionKind;
    }
}
