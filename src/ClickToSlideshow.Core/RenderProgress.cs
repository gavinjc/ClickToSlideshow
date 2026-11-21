namespace ClickToSlideshow.Core;

public enum RenderPhase
{
    Frames,
    Audio,
}

/// <summary>
/// One progress report from <see cref="SlideshowRenderer"/>. Each phase restarts <see cref="Fraction"/>
/// at 0 rather than the two being blended into one number: the video pass and the audio-mux
/// re-encode have no fixed cost ratio (it varies with resolution, slide count and track length),
/// so any blend would be a guess that stalls or jumps.
/// </summary>
public readonly record struct RenderProgress(RenderPhase Phase, double Fraction);
