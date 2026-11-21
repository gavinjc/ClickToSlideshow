using System.Text;

namespace ClickToSlideshow.Core.Tests;

internal static class TestWav
{
    public static byte[] BuildMonoPcm(short[] samples, int sampleRate)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        int dataSize = samples.Length * 2;
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2); // byte rate
        writer.Write((short)2); // block align
        writer.Write((short)16); // bits per sample

        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        foreach (short sample in samples)
        {
            writer.Write(sample);
        }

        return stream.ToArray();
    }

    public static short[] Tone(double frequencyHz, int sampleRate, int seconds) =>
        Enumerable.Range(0, sampleRate * seconds)
            .Select(i => (short)(Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate) * 8000))
            .ToArray();
}
