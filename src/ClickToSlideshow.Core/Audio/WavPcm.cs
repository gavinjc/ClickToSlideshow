using System.Text;

namespace ClickToSlideshow.Core.Audio;

/// <summary>
/// Minimal RIFF/WAVE PCM header read/write, shared by <see cref="OggAudioTranscoder"/> (its decode
/// output), <see cref="AudioPeakExtractor"/> (waveform extraction) and
/// <see cref="FadedSoundtrackRenderer"/> (its fade-out step) — one implementation instead of three
/// near-identical ones. <see cref="ParseHeader"/> walks chunks by their declared size rather than
/// assuming a fixed 44-byte layout, since it also has to read back whatever
/// <see cref="Windows.Media.Editing.MediaComposition"/>'s own WAV sink writes, not only the header
/// <see cref="WriteHeader"/> emits here. Public (not internal) so it's directly unit-testable
/// without InternalsVisibleTo, matching <see cref="AudioPeakExtractor.ComputePeaksFromWav"/>'s
/// own precedent for a pure sub-step split out of a WinRT-dependent pipeline.
/// </summary>
public static class WavPcm
{
    public static void WriteHeader(Stream output, int sampleRate, int channels, long dataBytes)
    {
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write((int)(36 + dataBytes));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write((int)dataBytes);
    }

    public static (int DataOffset, int DataLength, short Channels, short BitsPerSample, int SampleRate) ParseHeader(
        byte[] wavBytes)
    {
        int pos = 12; // past "RIFF" + size (4) + "WAVE"
        short channels = 0;
        short bitsPerSample = 0;
        int sampleRate = 0;
        int dataOffset = -1;
        int dataLength = 0;

        while (pos + 8 <= wavBytes.Length)
        {
            string chunkId = Encoding.ASCII.GetString(wavBytes, pos, 4);
            int chunkSize = BitConverter.ToInt32(wavBytes, pos + 4);
            int chunkDataStart = pos + 8;

            if (chunkId == "fmt ")
            {
                channels = BitConverter.ToInt16(wavBytes, chunkDataStart + 2);
                sampleRate = BitConverter.ToInt32(wavBytes, chunkDataStart + 4);
                bitsPerSample = BitConverter.ToInt16(wavBytes, chunkDataStart + 14);
            }
            else if (chunkId == "data")
            {
                dataOffset = chunkDataStart;
                dataLength = chunkSize;
            }

            pos = chunkDataStart + chunkSize + (chunkSize % 2); // chunks are word-aligned
        }

        return (dataOffset, dataLength, channels, bitsPerSample, sampleRate);
    }
}
