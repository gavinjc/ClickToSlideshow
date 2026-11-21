using ClickToSlideshow.Core.Audio;

namespace ClickToSlideshow.Core.Tests;

// Fixtures in TestData are 1-second 440 Hz mono tones encoded by ffmpeg. Checking pitch and length
// (not just "produced a file") is what proves the decoder returned the real audio rather than
// silence or noise of the right size.
public class OggAudioTranscoderTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "ClickToSlideshowTests", Guid.NewGuid().ToString());

    public OggAudioTranscoderTests() => Directory.CreateDirectory(_tempDirectory);

    public void Dispose() => Directory.Delete(_tempDirectory, recursive: true);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    [Fact]
    public void DecodeToWav_VorbisFile_ProducesMonoWavOfTheSamePitchAndLength()
    {
        string wavPath = Path.Combine(_tempDirectory, "vorbis.wav");

        OggAudioTranscoder.DecodeToWav(Fixture("tone440.ogg"), wavPath);

        WavInfo wav = ReadWav(wavPath);
        Assert.Equal(44100, wav.SampleRate);
        Assert.Equal(1, wav.Channels);
        Assert.InRange(wav.Seconds, 0.95, 1.1);
        Assert.InRange(wav.DominantFrequencyHz, 430, 450);
    }

    [Theory]
    [InlineData("tone440.opus")]
    [InlineData("tone440-opus-in.ogg")]
    public void DecodeToWav_OpusFile_ProducesStereoWavOfTheSamePitchAndLength(string fixtureName)
    {
        // The second case is an Opus stream in an .ogg container: the Vorbis-first path for .ogg
        // has to fall back rather than fail.
        string wavPath = Path.Combine(_tempDirectory, "opus.wav");

        OggAudioTranscoder.DecodeToWav(Fixture(fixtureName), wavPath);

        WavInfo wav = ReadWav(wavPath);
        Assert.Equal(48000, wav.SampleRate);
        Assert.Equal(2, wav.Channels);
        Assert.InRange(wav.Seconds, 0.95, 1.1);
        Assert.InRange(wav.DominantFrequencyHz, 430, 450);
    }

    [Fact]
    public void DecodeToWav_CorruptFile_ThrowsAndLeavesNoWavBehind()
    {
        string corruptPath = Path.Combine(_tempDirectory, "corrupt.ogg");
        File.WriteAllBytes(corruptPath, Enumerable.Range(0, 2000).Select(i => (byte)(i * 31)).ToArray());
        string wavPath = Path.Combine(_tempDirectory, "corrupt.wav");

        Assert.ThrowsAny<Exception>(() => OggAudioTranscoder.DecodeToWav(corruptPath, wavPath));

        // A leftover file here would be served by the cache as a finished decode.
        Assert.Empty(Directory.GetFiles(_tempDirectory, "corrupt.wav*"));
    }

    [Fact]
    public async Task EnsurePlayablePathAsync_NonOggFile_ReturnsTheSourceUntouched()
    {
        string mp3Path = Path.Combine(_tempDirectory, "song.mp3");

        Assert.Equal(mp3Path, await OggAudioTranscoder.EnsurePlayablePathAsync(mp3Path));
    }

    [Fact]
    public async Task EnsurePlayablePathAsync_OggFile_ReturnsDecodedWav()
    {
        string source = Path.Combine(_tempDirectory, "decoded.ogg");
        File.Copy(Fixture("tone440.ogg"), source);

        string wavPath = await OggAudioTranscoder.EnsurePlayablePathAsync(source);

        try
        {
            Assert.EndsWith(".wav", wavPath);
            Assert.InRange(ReadWav(wavPath).Seconds, 0.95, 1.1);
        }
        finally
        {
            File.Delete(wavPath);
        }
    }

    [Fact]
    public async Task EnsurePlayablePathAsync_SecondCall_ReusesTheCachedDecodeAndMarksItRecentlyUsed()
    {
        string source = Path.Combine(_tempDirectory, "reused.ogg");
        File.Copy(Fixture("tone440.ogg"), source);
        string first = await OggAudioTranscoder.EnsurePlayablePathAsync(source);

        try
        {
            // Overwritten and aged so a re-decode (which would replace the marker) and a missing
            // touch (which would leave the old timestamp) are each visible.
            byte[] marker = "not a real wav"u8.ToArray();
            File.WriteAllBytes(first, marker);
            File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddDays(-30));

            string second = await OggAudioTranscoder.EnsurePlayablePathAsync(source);

            Assert.Equal(first, second);
            Assert.Equal(marker, File.ReadAllBytes(second));
            Assert.True(File.GetLastWriteTimeUtc(second) > DateTime.UtcNow.AddMinutes(-5));
        }
        finally
        {
            File.Delete(first);
        }
    }

    [Fact]
    public void DecodeToWav_DestinationHeldOpenByAnotherInstance_KeepsTheirFileAndLeavesNoPartialBehind()
    {
        // Two instances decoding the same file: the loser can't replace a WAV the winner is
        // already playing, and should accept theirs rather than fail.
        string wavPath = Path.Combine(_tempDirectory, "raced.wav");
        byte[] winnersFile = "winner"u8.ToArray();
        File.WriteAllBytes(wavPath, winnersFile);
        using var winner = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        OggAudioTranscoder.DecodeToWav(Fixture("tone440.ogg"), wavPath);

        Assert.Equal(winnersFile, File.ReadAllBytes(wavPath));
        Assert.DoesNotContain(Directory.GetFiles(_tempDirectory), file => file.EndsWith(".partial"));
    }

    [Fact]
    public void PruneStaleCache_DeletesFilesOlderThanTheLimitAndKeepsRecentOnes()
    {
        string cache = Path.Combine(_tempDirectory, "cache");
        Directory.CreateDirectory(cache);
        string oldFile = Path.Combine(cache, "old.wav");
        string recentFile = Path.Combine(cache, "recent.wav");
        File.WriteAllText(oldFile, "x");
        File.WriteAllText(recentFile, "x");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-10));

        OggAudioTranscoder.PruneStaleCache(TimeSpan.FromDays(7), cache);

        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(recentFile));
    }

    [Fact]
    public void PruneStaleCache_StaleFileThatCannotBeDeleted_IsLeftAloneWithoutThrowing()
    {
        // Read-only makes File.Delete throw UnauthorizedAccessException rather than IOException.
        string cache = Path.Combine(_tempDirectory, "cache");
        Directory.CreateDirectory(cache);
        string stuckFile = Path.Combine(cache, "stuck.wav");
        File.WriteAllText(stuckFile, "x");
        File.SetLastWriteTimeUtc(stuckFile, DateTime.UtcNow.AddDays(-10));
        File.SetAttributes(stuckFile, FileAttributes.ReadOnly);

        try
        {
            OggAudioTranscoder.PruneStaleCache(TimeSpan.FromDays(7), cache);

            Assert.True(File.Exists(stuckFile));
        }
        finally
        {
            File.SetAttributes(stuckFile, FileAttributes.Normal);
        }
    }

    private sealed record WavInfo(int SampleRate, int Channels, double Seconds, double DominantFrequencyHz);

    private static WavInfo ReadWav(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int channels = BitConverter.ToInt16(bytes, 22);
        int sampleRate = BitConverter.ToInt32(bytes, 24);
        int dataBytes = BitConverter.ToInt32(bytes, 40);
        Assert.Equal(bytes.Length - 44, dataBytes);

        int frames = dataBytes / (2 * channels);
        double seconds = frames / (double)sampleRate;

        // First channel only, skipping the first and last tenth (codec ramp-up/padding): a pure
        // tone crosses zero twice per cycle.
        int start = frames / 10;
        int end = frames - frames / 10;
        int crossings = 0;
        short previous = BitConverter.ToInt16(bytes, 44 + start * channels * 2);
        for (int i = start + 1; i < end; i++)
        {
            short current = BitConverter.ToInt16(bytes, 44 + i * channels * 2);
            if ((previous < 0) != (current < 0))
            {
                crossings++;
            }

            previous = current;
        }

        double analysedSeconds = (end - start) / (double)sampleRate;
        return new WavInfo(sampleRate, channels, seconds, crossings / (2 * analysedSeconds));
    }
}
