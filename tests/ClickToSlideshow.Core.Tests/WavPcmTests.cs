using ClickToSlideshow.Core.Audio;

namespace ClickToSlideshow.Core.Tests;

public class WavPcmTests
{
    [Fact]
    public void ParseHeader_FileWrittenByWriteHeader_RoundTripsSampleRateChannelsAndData()
    {
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];
        using var stream = new MemoryStream();

        WavPcm.WriteHeader(stream, sampleRate: 22050, channels: 2, dataBytes: data.Length);
        stream.Write(data);

        (int dataOffset, int dataLength, short channels, short bitsPerSample, int sampleRate) =
            WavPcm.ParseHeader(stream.ToArray());

        Assert.Equal(44, dataOffset); // the canonical PCM header this writer always emits
        Assert.Equal(data.Length, dataLength);
        Assert.Equal(2, channels);
        Assert.Equal(16, bitsPerSample);
        Assert.Equal(22050, sampleRate);
    }

    [Fact]
    public void ParseHeader_ExtraChunkBeforeData_SkipsItAndStillFindsTheDataChunk()
    {
        // Not every WAV writer places "data" immediately after "fmt " — a real-world producer
        // (Windows Media Foundation's own WAV sink, unlike WriteHeader above) can insert other
        // chunks in between, which is exactly what this class has to tolerate that a fixed
        // 44-byte offset assumption would not.
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(0); // overall size, unread by ParseHeader
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1); // mono
            writer.Write(8000);
            writer.Write(8000 * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("JUNK"u8);
            writer.Write(4);
            writer.Write(0); // 4 bytes of padding data, word-aligned
            writer.Write("data"u8);
            writer.Write(2);
            writer.Write((short)1234);
        }

        (int dataOffset, int dataLength, short channels, short bitsPerSample, int sampleRate) =
            WavPcm.ParseHeader(stream.ToArray());

        Assert.Equal(1, channels);
        Assert.Equal(16, bitsPerSample);
        Assert.Equal(8000, sampleRate);
        Assert.Equal(2, dataLength);
        Assert.Equal(1234, BitConverter.ToInt16(stream.ToArray(), dataOffset));
    }
}
