using System.Security.Cryptography;
using System.Text;
using Concentus;
using Concentus.Oggfile;
using NVorbis;

namespace ClickToSlideshow.Core.Audio;

/// <summary>
/// Windows has no Ogg/Vorbis/Opus decoder Media Foundation can reach (confirmed even with the Web
/// Media Extensions installed), so .ogg/.opus files are decoded here, in managed code, to a temp
/// WAV that everything downstream (waveform, player, MP4 mux, PPTX soundtrack) can open like any
/// other track. The source file is only read; projects keep pointing at it, not at the WAV.
/// </summary>
public static class OggAudioTranscoder
{
    private const int WavHeaderSize = 44;
    private const int OpusSampleRate = 48000;
    private const int OpusChannels = 2;

    private static readonly string CacheDirectory = Path.Combine(AppTempFolder.Root, "audio");

    public static bool IsOggFamily(string path) =>
        Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".opus", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The path Media Foundation can open for <paramref name="sourcePath"/>: the file itself for
    /// every format Windows handles, otherwise a cached decoded WAV. The cache key covers the
    /// file's length and timestamp, so an edited file is re-decoded rather than served stale.
    /// </summary>
    public static async Task<string> EnsurePlayablePathAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (!IsOggFamily(sourcePath))
        {
            return sourcePath;
        }

        string wavPath = CachePathFor(sourcePath);
        if (File.Exists(wavPath))
        {
            // Refreshed so PruneStaleCache doesn't delete a decode that's still being used. A
            // reader holding the file open doesn't block this (tested), so it needs no guard.
            File.SetLastWriteTimeUtc(wavPath, DateTime.UtcNow);
            return wavPath;
        }

        await Task.Run(() => DecodeToWav(sourcePath, wavPath), cancellationToken);
        return wavPath;
    }

    // directory is only for tests; the app always prunes its own cache folder.
    public static void PruneStaleCache(TimeSpan maxAge, string? directory = null)
    {
        directory ??= CacheDirectory;
        if (!Directory.Exists(directory))
        {
            return;
        }

        DateTime cutoff = DateTime.UtcNow - maxAge;
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use by a running instance — leave it for the next launch.
            }
        }
    }

    // Public so it's unit-testable directly against a chosen destination.
    public static void DecodeToWav(string sourcePath, string wavPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(wavPath))!);

        // Written beside the destination and renamed once complete: a cancelled or crashed decode
        // must never leave a truncated WAV that the cache would then serve as finished. Uniquely
        // named because two app instances can decode the same file at once.
        string partialPath = $"{wavPath}.{Guid.NewGuid():N}.partial";
        try
        {
            using (FileStream output = File.Create(partialPath))
            {
                output.Write(new byte[WavHeaderSize]);
                (int sampleRate, int channels, long dataBytes) = DecodeSamples(sourcePath, output);

                // The Opus reader treats an unreadable file as "no packets" rather than an error.
                if (dataBytes == 0)
                {
                    throw new InvalidDataException($"No audio could be decoded from '{Path.GetFileName(sourcePath)}'.");
                }

                output.Position = 0;
                WavPcm.WriteHeader(output, sampleRate, channels, dataBytes);
            }

            try
            {
                File.Move(partialPath, wavPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && File.Exists(wavPath))
            {
                // Another instance finished the same decode first and has it open (Windows reports
                // replacing an open file as access denied); theirs is as good.
            }
        }
        finally
        {
            File.Delete(partialPath);
        }
    }

    // .opus is always Opus; .ogg is usually Vorbis but can carry Opus, so it tries Vorbis first
    // and falls back when NVorbis finds no Vorbis stream in the container.
    private static (int SampleRate, int Channels, long DataBytes) DecodeSamples(string sourcePath, FileStream output)
    {
        if (Path.GetExtension(sourcePath).Equals(".opus", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeOpus(sourcePath, output);
        }

        try
        {
            return DecodeVorbis(sourcePath, output);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            // NVorbis reports a container with no Vorbis stream as InvalidDataException, but an
            // Opus stream specifically as ArgumentException ("Found OPUS bitstream").
            output.SetLength(WavHeaderSize);
            output.Position = WavHeaderSize;
            return DecodeOpus(sourcePath, output);
        }
    }

    private static (int SampleRate, int Channels, long DataBytes) DecodeVorbis(string sourcePath, Stream output)
    {
        using var reader = new VorbisReader(sourcePath);
        var samples = new float[reader.SampleRate * reader.Channels / 2];
        var pcm = new byte[samples.Length * 2];
        long dataBytes = 0;

        int read;
        while ((read = reader.ReadSamples(samples, 0, samples.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                short sample = (short)Math.Clamp((int)MathF.Round(samples[i] * short.MaxValue), short.MinValue, short.MaxValue);
                pcm[i * 2] = (byte)(sample & 0xFF);
                pcm[i * 2 + 1] = (byte)(sample >> 8);
            }

            output.Write(pcm, 0, read * 2);
            dataBytes += read * 2;
        }

        return (reader.SampleRate, reader.Channels, dataBytes);
    }

    // Decoded as stereo whatever the source: Opus upmixes mono, and a fixed layout keeps the
    // WAV header independent of the file's own channel mapping.
    private static (int SampleRate, int Channels, long DataBytes) DecodeOpus(string sourcePath, Stream output)
    {
        IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(OpusSampleRate, OpusChannels);
        using FileStream input = File.OpenRead(sourcePath);
        var opusStream = new OpusOggReadStream(decoder, input);
        long dataBytes = 0;

        while (opusStream.HasNextPacket)
        {
            short[]? packet = opusStream.DecodeNextPacket();
            if (packet is null)
            {
                continue;
            }

            var pcm = new byte[packet.Length * 2];
            Buffer.BlockCopy(packet, 0, pcm, 0, pcm.Length);
            output.Write(pcm, 0, pcm.Length);
            dataBytes += pcm.Length;
        }

        return (OpusSampleRate, OpusChannels, dataBytes);
    }

    private static string CachePathFor(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        string key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(CacheDirectory, hash + ".wav");
    }
}
