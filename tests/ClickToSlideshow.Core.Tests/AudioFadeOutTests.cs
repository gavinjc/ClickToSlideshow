using ClickToSlideshow.Core.Audio;

namespace ClickToSlideshow.Core.Tests;

public class AudioFadeOutTests
{
    // Frame count in AudioFadeOut.Duration at 10Hz — derived, not hardcoded, so a change to the
    // constant moves these tests' expectations with it instead of leaving them pinned to stale
    // math that would then assert the wrong thing without failing loudly.
    private const int SampleRate = 10;
    private static readonly int FadeFrames = (int)(AudioFadeOut.Duration.TotalSeconds * SampleRate);

    [Fact]
    public void ApplyInPlace_BufferLongerThanFadeWindow_LeavesEarlierSamplesUntouched()
    {
        var pcm = BuildMonoPcm(totalFrames: FadeFrames + 10, amplitude: 20000);

        AudioFadeOut.ApplyInPlace(pcm, SampleRate, channels: 1);

        for (int frame = 0; frame < 10; frame++)
        {
            Assert.Equal(20000, ReadSample(pcm, frame, channel: 0, channels: 1));
        }
    }

    [Fact]
    public void ApplyInPlace_BufferLongerThanFadeWindow_RampsLinearlyToExactSilenceAtTheLastFrame()
    {
        int totalFrames = FadeFrames + 10;
        var pcm = BuildMonoPcm(totalFrames, amplitude: 20000);

        AudioFadeOut.ApplyInPlace(pcm, SampleRate, channels: 1);

        // Fade window is the last FadeFrames frames: gain 1.0 at its first frame, 0.0 at the very
        // last frame, linear in between.
        int fadeStart = totalFrames - FadeFrames;
        int midFrame = fadeStart + (FadeFrames / 2);
        double expectedMidGain = (double)(totalFrames - 1 - midFrame) / (FadeFrames - 1);

        Assert.Equal(20000, ReadSample(pcm, fadeStart, channel: 0, channels: 1));
        Assert.Equal(0, ReadSample(pcm, totalFrames - 1, channel: 0, channels: 1));
        Assert.Equal((short)Math.Round(20000 * expectedMidGain), ReadSample(pcm, midFrame, channel: 0, channels: 1));
    }

    [Fact]
    public void ApplyInPlace_BufferShorterThanFadeWindow_FadesTheWholeBufferFromTheStart()
    {
        int totalFrames = FadeFrames - 10; // shorter than the fade window
        var pcm = BuildMonoPcm(totalFrames, amplitude: 1000);

        AudioFadeOut.ApplyInPlace(pcm, SampleRate, channels: 1);

        int midFrame = totalFrames / 2;
        double expectedMidGain = (double)(totalFrames - 1 - midFrame) / (totalFrames - 1);

        Assert.Equal(1000, ReadSample(pcm, frame: 0, channel: 0, channels: 1));
        Assert.Equal(0, ReadSample(pcm, totalFrames - 1, channel: 0, channels: 1));
        Assert.Equal((short)Math.Round(1000 * expectedMidGain), ReadSample(pcm, midFrame, channel: 0, channels: 1));
    }

    [Fact]
    public void ApplyInPlace_StereoBuffer_AppliesTheSameGainToBothChannelsIndependently()
    {
        int totalFrames = FadeFrames - 10; // whole buffer inside the fade window, as above
        int frameBytes = 2 * 2;
        var pcm = new byte[totalFrames * frameBytes];
        for (int frame = 0; frame < totalFrames; frame++)
        {
            WriteSample(pcm, frame, channel: 0, channels: 2, value: 10000);
            WriteSample(pcm, frame, channel: 1, channels: 2, value: -10000);
        }

        AudioFadeOut.ApplyInPlace(pcm, SampleRate, channels: 2);

        int midFrame = totalFrames / 2;
        double expectedMidGain = (double)(totalFrames - 1 - midFrame) / (totalFrames - 1);
        short expectedMid = (short)Math.Round(10000 * expectedMidGain);

        Assert.Equal(expectedMid, ReadSample(pcm, midFrame, channel: 0, channels: 2));
        Assert.Equal((short)-expectedMid, ReadSample(pcm, midFrame, channel: 1, channels: 2));
    }

    private static byte[] BuildMonoPcm(int totalFrames, short amplitude)
    {
        var pcm = new byte[totalFrames * 2];
        for (int frame = 0; frame < totalFrames; frame++)
        {
            WriteSample(pcm, frame, channel: 0, channels: 1, amplitude);
        }

        return pcm;
    }

    private static void WriteSample(byte[] pcm, int frame, int channel, int channels, short value)
    {
        int i = ((frame * channels) + channel) * 2;
        pcm[i] = (byte)(value & 0xFF);
        pcm[i + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static short ReadSample(byte[] pcm, int frame, int channel, int channels) =>
        BitConverter.ToInt16(pcm, ((frame * channels) + channel) * 2);
}
