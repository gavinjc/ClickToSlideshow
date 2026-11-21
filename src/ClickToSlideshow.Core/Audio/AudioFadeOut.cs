namespace ClickToSlideshow.Core.Audio;

/// <summary>
/// Applies spec §4.2's fade-out — a linear ramp to silence over the final 2s of the show — to
/// already-sequenced 16-bit PCM. Mirrors <c>PlayerViewModel.UpdateFadeVolume</c>'s always-on ramp
/// for the live player, and that comment's own reasoning applies here too: unconditional rather
/// than branching per <c>AudioMatch</c>, since ramping down samples that are already silence
/// (Trim's shorter-than-show case) changes nothing audible.
/// </summary>
public static class AudioFadeOut
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(2);

    // pcm is interleaved 16-bit samples (channels frames wide) covering the whole track; mutated
    // in place since the caller (FadedSoundtrackRenderer) owns a private buffer it's about to
    // write out, not anything shared.
    public static void ApplyInPlace(byte[] pcm, int sampleRate, int channels)
    {
        int frameBytes = channels * 2;
        long totalFrames = pcm.Length / frameBytes;
        long fadeFrames = Math.Min(totalFrames, (long)(Duration.TotalSeconds * sampleRate));
        long fadeStartFrame = totalFrames - fadeFrames;

        for (long frame = fadeStartFrame; frame < totalFrames; frame++)
        {
            // 1.0 at fadeStartFrame, exactly 0.0 at the last frame — not (totalFrames - frame) /
            // fadeFrames, which would leave the final frame at a non-zero 1/fadeFrames instead of
            // true silence.
            double gain = fadeFrames <= 1 ? 0.0 : (double)(totalFrames - 1 - frame) / (fadeFrames - 1);
            int frameOffset = (int)(frame * frameBytes);

            for (int channel = 0; channel < channels; channel++)
            {
                int i = frameOffset + (channel * 2);
                short sample = BitConverter.ToInt16(pcm, i);
                short scaled = (short)Math.Clamp((int)Math.Round(sample * gain), short.MinValue, short.MaxValue);
                pcm[i] = (byte)(scaled & 0xFF);
                pcm[i + 1] = (byte)((scaled >> 8) & 0xFF);
            }
        }
    }
}
