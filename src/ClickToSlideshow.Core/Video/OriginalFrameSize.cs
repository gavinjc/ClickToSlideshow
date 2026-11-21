namespace ClickToSlideshow.Core.Video;

/// <summary>One slide's source pixel size (as decoded, before any rotation) and its rotation.</summary>
public readonly record struct SlidePixelSize(int Width, int Height, int RotationDegrees);

/// <summary>
/// The frame size for <see cref="Model.Resolution.Original"/>: the largest slide by pixel area,
/// after its rotation, scaled to what the H.264 encoder accepts and made even. Pure, so the rule is
/// testable without decoding anything; <see cref="Mp4Profiles"/> supplies the sizes. The limits
/// below were probed on 2026-10-31; see the decisions doc's "Original resolution" entry.
/// </summary>
public static class OriginalFrameSize
{
    // H.264 Level 5.1/5.2 MaxFS (ITU-T H.264 Annex A): 36,864 16x16 macroblocks, 4096x2304 at
    // 16:9. 5.2 is the highest level Media Foundation's encoder offers. The dev machine's hardware
    // encoder also took 4096x4096, past this, but another PC's may not, and MediaTranscoder does
    // not fall back to the software encoder when the hardware one refuses a size.
    public const int MaxMacroblocks = 36_864;

    // The hardware encoder refused every frame with a side over 4096 (4608x2592 failed, although
    // the software encoder took it).
    public const int MaxSide = 4096;

    // The hardware encoder refused 64x48 and took 80x60. 128 leaves a margin for other encoders;
    // a smaller photo is shown with bands, as Fit does for any shape mismatch.
    public const int MinSide = 128;

    /// <returns>The frame size, or <see langword="null"/> when no slide has a known size (an
    /// empty show, or every slide unreadable) — the caller then falls back to 1080p.</returns>
    public static (int Width, int Height)? Choose(IEnumerable<SlidePixelSize> slides)
    {
        (int Width, int Height)? largest = null;
        foreach (SlidePixelSize slide in slides)
        {
            if (slide.Width <= 0 || slide.Height <= 0)
            {
                continue;
            }

            bool swapDimensions = slide.RotationDegrees is 90 or 270;
            (int width, int height) = swapDimensions ? (slide.Height, slide.Width) : (slide.Width, slide.Height);
            if (largest is null || (long)width * height > (long)largest.Value.Width * largest.Value.Height)
            {
                largest = (width, height);
            }
        }

        return largest is { } size ? FitEncoder(size.Width, size.Height) : null;
    }

    internal static (int Width, int Height) FitEncoder(int width, int height)
    {
        double scale = Math.Min(1.0, Math.Min((double)MaxSide / width, (double)MaxSide / height));
        double macroblocks = (width * scale / 16) * (height * scale / 16);
        if (macroblocks > MaxMacroblocks)
        {
            scale *= Math.Sqrt(MaxMacroblocks / macroblocks);
        }

        // Whole macroblocks round up, so the scaled size can still be a row or column over; shrink
        // both sides together until it fits, so the shape is kept.
        while (Macroblocks(EvenWithinSideLimits(width * scale)) * Macroblocks(EvenWithinSideLimits(height * scale)) > MaxMacroblocks)
        {
            scale *= 0.995;
        }

        return (EvenWithinSideLimits(width * scale), EvenWithinSideLimits(height * scale));
    }

    // Floored to even (spec §4.5: some hardware encoders reject odd sizes); the epsilon keeps an
    // exact 4096 that arrives as 4095.9999 from becoming 4094. The minimum only ever raises a
    // side, which can't breach MaxMacroblocks: 128 x 4096 is 2,048 macroblocks.
    private static int EvenWithinSideLimits(double value) => Math.Clamp((int)Math.Floor(value + 1e-6) & ~1, MinSide, MaxSide);

    private static long Macroblocks(int pixels) => (pixels + 15) / 16;
}
