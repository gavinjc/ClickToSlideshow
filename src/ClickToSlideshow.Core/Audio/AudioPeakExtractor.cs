using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace ClickToSlideshow.Core.Audio;

/// <summary>
/// Decodes a track to a fixed-length array of per-bucket peak amplitudes (0..1) for the audio
/// card's waveform (audio-section-brief.md §3.3). Transcodes to a temp PCM WAV via
/// <see cref="MediaTranscoder"/> — the same WinRT media API family <see cref="Video.Mp4VideoRenderer"/>
/// already uses — rather than a new decode dependency, since Windows' built-in codecs already
/// handle any source format the app accepts through it.
/// </summary>
public static class AudioPeakExtractor
{
    public static async Task<float[]> ExtractPeaksAsync(
        string sourcePath, int bucketCount, CancellationToken cancellationToken = default)
    {
        string tempWavPath = AppTempFolder.NewFilePath(".wav");
        try
        {
            StorageFile sourceFile = await StorageFile.GetFileFromPathAsync(sourcePath);

            File.WriteAllBytes(tempWavPath, []);
            StorageFile tempFile = await StorageFile.GetFileFromPathAsync(tempWavPath);

            // AudioEncodingQuality.Auto fails prepare (CanTranscode=false, FailureReason=Unknown) for
            // any compressed source — confirmed against real mp3/m4a/aac/wma files, not assumed. WAV
            // has no inherent quality tier for Auto to match against a lossy input; a fixed quality
            // works for every source format tested. Only wav→wav (no real transcode needed) ever
            // succeeded with Auto, which is what made this look format-specific before it was isolated.
            MediaEncodingProfile profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
            var transcoder = new MediaTranscoder();
            PrepareTranscodeResult prepareResult = await transcoder.PrepareFileTranscodeAsync(sourceFile, tempFile, profile);
            if (!prepareResult.CanTranscode)
            {
                throw new InvalidOperationException(
                    $"MediaTranscoder cannot prepare '{sourcePath}' for waveform extraction: {prepareResult.FailureReason}.");
            }

            await prepareResult.TranscodeAsync().AsTask(cancellationToken);

            return ComputePeaksFromWav(File.ReadAllBytes(tempWavPath), bucketCount);
        }
        finally
        {
            File.Delete(tempWavPath);
        }
    }

    // Public (not internal) so it's directly unit-testable without InternalsVisibleTo, matching
    // PptxSlideGeometry.Compute's precedent for a pure sub-step split out of a WinRT-dependent
    // pipeline. The transcode step above needs real WinRT media activation (verified by running
    // the Cli, per this project's own M1/M3/M5 lesson that WinRT media types don't reliably
    // activate under a bare `dotnet test` host), but parsing a decoded WAV into peaks doesn't.
    public static float[] ComputePeaksFromWav(byte[] wavBytes, int bucketCount)
    {
        var peaks = new float[bucketCount];

        (int dataOffset, int dataLength, short channels, short bitsPerSample, _) = WavPcm.ParseHeader(wavBytes);
        if (dataOffset < 0 || bitsPerSample != 16 || channels < 1)
        {
            // Never fail a save over a waveform that's decoration (brief §3.3) — flat placeholder.
            return peaks;
        }

        int bytesPerFrame = channels * 2;
        int frameCount = dataLength / bytesPerFrame;
        if (frameCount == 0)
        {
            return peaks;
        }

        for (int bucket = 0; bucket < bucketCount; bucket++)
        {
            int startFrame = (int)((long)bucket * frameCount / bucketCount);
            int endFrame = (int)((long)(bucket + 1) * frameCount / bucketCount);
            short peakSample = 0;

            for (int frame = startFrame; frame < endFrame; frame++)
            {
                int frameOffset = dataOffset + (frame * bytesPerFrame);
                for (int channel = 0; channel < channels; channel++)
                {
                    short sample = BitConverter.ToInt16(wavBytes, frameOffset + (channel * 2));
                    short magnitude = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
                    if (magnitude > peakSample)
                    {
                        peakSample = magnitude;
                    }
                }
            }

            peaks[bucket] = peakSample / (float)short.MaxValue;
        }

        return peaks;
    }
}
