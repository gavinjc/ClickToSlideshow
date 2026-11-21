using ClickToSlideshow.Core.Model;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace ClickToSlideshow.Core.Audio;

/// <summary>
/// Renders a project's audio tracks — Loop/Trim/FitSlides sequencing, spec §4.2 — into one
/// show-length WAV with the fade-out already baked in, attaches it to a caller-supplied
/// <see cref="MediaComposition"/>, and renders that composition to its final output. Shared by
/// the MP4 muxer and the PPTX writer's soundtrack so both save paths get the same fade, the same
/// temp-WAV lifecycle, and the same progress split from one implementation — each caller supplies
/// only what's genuinely different: its own foreground clip (the video, or a black placeholder
/// for audio-only output) and its own output profile.
///
/// <see cref="BackgroundAudioTrack"/> has no fade property, and the natural WinRT fix — a custom
/// <c>Windows.Media.Effects.IBasicAudioEffect</c> added to it — needs its own Windows Runtime
/// Component project, whose class activation was not confirmed to work for this app's unpackaged
/// dev-launch path (only for a packaged MSIX install); shipping that unverified was the exact
/// mistake this project's own conventions warn against. This sidesteps it entirely: let
/// <see cref="MediaCompositionAudioMuxer.AddBackgroundAudioAsync"/> do the sequencing exactly as
/// before, render that to a plain PCM WAV, then apply the fade as a direct gain ramp
/// (<see cref="AudioFadeOut"/>) on the raw samples — no custom effect, no extra project, no
/// packaging dependency.
/// </summary>
internal static class FadedSoundtrackRenderer
{
    /// <summary>
    /// <paramref name="composition"/> must already have its own foreground clip added. The
    /// intermediate WAV's whole lifecycle is owned here — created, read back for the fade,
    /// attached, and deleted — and never handed to the caller: the composition can't safely read
    /// it once it's gone, so the file has to outlive the final render call below, not just the
    /// attach, which is why this does the attach-and-render itself instead of returning a
    /// ready-made <see cref="BackgroundAudioTrack"/> for the caller to render later.
    /// </summary>
    public static async Task RenderWithFadedAudioAsync(
        SlideshowProject project,
        MediaComposition composition,
        string outputPath,
        MediaEncodingProfile profile,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        string fadedWavPath = AppTempFolder.NewFilePath(".wav");
        try
        {
            await RenderFadedWavAsync(project, fadedWavPath, Scaled(progress, 0.0, 0.4), cancellationToken);

            StorageFile fadedWavFile = await StorageFile.GetFileFromPathAsync(fadedWavPath);
            composition.BackgroundAudioTracks.Add(await BackgroundAudioTrack.CreateFromFileAsync(fadedWavFile));

            await CompositionFileRenderer.RenderAsync(
                composition, outputPath, profile, Scaled(progress, 0.4, 1.0), cancellationToken);
        }
        finally
        {
            File.Delete(fadedWavPath);
        }
    }

    private static async Task RenderFadedWavAsync(
        SlideshowProject project,
        string outputWavPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        string sequencedPath = AppTempFolder.NewFilePath(".wav");
        try
        {
            var composition = new MediaComposition();

            // This composition is purely internal — just a vehicle for sequencing the tracks to a
            // WAV — so it needs its own foreground clip regardless of what the caller's own
            // composition (passed to RenderWithFadedAudioAsync above) uses for that. Background
            // audio tracks don't count towards a composition's duration, so a black clip exactly
            // project.Runtime long supplies it (and truncates any audio running past it); its
            // video is discarded by the audio-only WAV profile below.
            composition.Clips.Add(MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 0, 0, 0), project.Runtime));
            await MediaCompositionAudioMuxer.AddBackgroundAudioAsync(composition, project, cancellationToken);

            MediaEncodingProfile sequencingProfile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
            await CompositionFileRenderer.RenderAsync(composition, sequencedPath, sequencingProfile, progress, cancellationToken);

            byte[] wavBytes = File.ReadAllBytes(sequencedPath);
            (int dataOffset, int dataLength, short channels, short bitsPerSample, int sampleRate) =
                WavPcm.ParseHeader(wavBytes);
            if (dataOffset < 0 || bitsPerSample != 16)
            {
                throw new InvalidOperationException(
                    $"Sequenced soundtrack WAV has an unsupported format (bitsPerSample={bitsPerSample}).");
            }

            byte[] pcm = wavBytes[dataOffset..(dataOffset + dataLength)];
            AudioFadeOut.ApplyInPlace(pcm, sampleRate, channels);

            using FileStream output = File.Create(outputWavPath);
            WavPcm.WriteHeader(output, sampleRate, channels, pcm.Length);
            output.Write(pcm);
        }
        finally
        {
            File.Delete(sequencedPath);
        }
    }

    // Splits the 0..1 progress this class receives between the WAV pre-render (sequencing plus
    // fade) and the final composition render, so the save bar's "Adding music…" phase still moves
    // smoothly across both passes instead of jumping from 0 to 100 twice.
    private static IProgress<double>? Scaled(IProgress<double>? progress, double from, double to) =>
        progress is null ? null : new Progress<double>(fraction => progress.Report(from + (fraction * (to - from))));
}
