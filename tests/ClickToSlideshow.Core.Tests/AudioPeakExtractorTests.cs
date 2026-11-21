using System.Text;
using ClickToSlideshow.Core.Audio;

namespace ClickToSlideshow.Core.Tests;

public class AudioPeakExtractorTests
{
    [Fact]
    public void ComputePeaksFromWav_LoudSecondHalf_ReflectsAmplitudeAcrossBuckets()
    {
        const int sampleRate = 8000;
        var samples = new short[sampleRate]; // 1 second, mono
        for (int i = samples.Length / 2; i < samples.Length; i++)
        {
            samples[i] = short.MaxValue;
        }

        byte[] wav = TestWav.BuildMonoPcm(samples, sampleRate);

        float[] peaks = AudioPeakExtractor.ComputePeaksFromWav(wav, bucketCount: 10);

        Assert.Equal(10, peaks.Length);
        Assert.All(peaks.Take(5), peak => Assert.Equal(0f, peak));
        Assert.All(peaks.Skip(5), peak => Assert.Equal(1f, peak, precision: 3));
    }

    [Fact]
    public void ComputePeaksFromWav_UnsupportedBitDepth_ReturnsFlatPlaceholderInsteadOfThrowing()
    {
        byte[] wav = BuildWavWithBitsPerSample(bitsPerSample: 8);

        float[] peaks = AudioPeakExtractor.ComputePeaksFromWav(wav, bucketCount: 4);

        Assert.Equal(new float[4], peaks);
    }

    private static byte[] BuildWavWithBitsPerSample(short bitsPerSample)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(8000);
        writer.Write(8000);
        writer.Write((short)1);
        writer.Write(bitsPerSample);

        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(0);

        return stream.ToArray();
    }
}
