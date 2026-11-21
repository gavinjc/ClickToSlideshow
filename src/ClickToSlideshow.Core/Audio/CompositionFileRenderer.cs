using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace ClickToSlideshow.Core.Audio;

internal static class CompositionFileRenderer
{
    public static async Task RenderAsync(
        MediaComposition composition,
        string outputPath,
        MediaEncodingProfile profile,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllBytes(outputPath, []);
        StorageFile outputFile = await StorageFile.GetFileFromPathAsync(outputPath);

        // RenderToFileAsync reports 0-100 and signals failure through its result, not by throwing —
        // ignoring it leaves the empty placeholder file above as a "successful" export.
        var percentProgress = new Progress<double>(percent => progress?.Report(percent / 100.0));
        TranscodeFailureReason failure = await composition
            .RenderToFileAsync(outputFile, MediaTrimmingPreference.Precise, profile)
            .AsTask(cancellationToken, percentProgress);

        if (failure != TranscodeFailureReason.None)
        {
            throw new InvalidOperationException($"Rendering the audio failed: {failure}.");
        }
    }
}
