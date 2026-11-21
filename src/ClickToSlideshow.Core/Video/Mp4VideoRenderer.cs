using ClickToSlideshow.Core.Imaging;
using ClickToSlideshow.Core.Model;
using ClickToSlideshow.Core.Rendering;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ClickToSlideshow.Core.Video;

/// <summary>
/// Renders a <see cref="SlideshowProject"/> to a video-only MP4 via the pipeline M0 proved:
/// Win2D <see cref="CanvasRenderTarget"/> → <see cref="IDirect3DSurface"/> (plain cast) →
/// <see cref="MediaStreamSample"/> → <see cref="MediaTranscoder"/>. Audio is a separate pass —
/// see <see cref="Audio.MediaCompositionAudioMuxer"/> — per decisions.md Decision 4.
/// </summary>
public static class Mp4VideoRenderer
{
    private static readonly TimeSpan FrameDuration = TimeSpan.FromSeconds(1.0 / 30.0);

    public static async Task RenderVideoOnlyAsync(
        SlideshowProject project,
        string outputPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (project.Slides.Count == 0)
        {
            throw new InvalidOperationException("Cannot render a slideshow with no slides.");
        }

        MediaEncodingProfile profile = await Mp4Profiles.ForProjectAsync(project);
        profile.Audio = null;

        // Deriving the render target size from the profile — rather than computing it
        // independently — is deliberate: a mismatch between the two is exactly the
        // CanTranscode=false failure mode M0's notes warned about.
        int width = (int)profile.Video.Width;
        int height = (int)profile.Video.Height;
        var targetSize = new Size(width, height);

        CanvasDevice device = CanvasDevice.GetSharedDevice();
        var decodedBitmaps = new Dictionary<int, CanvasBitmap>();
        using var frameGate = new SemaphoreSlim(1, 1);
        TimeSpan nextTimestamp = TimeSpan.Zero;

        async Task<CanvasBitmap> EnsureDecodedAsync(int slideIndex)
        {
            if (decodedBitmaps.TryGetValue(slideIndex, out CanvasBitmap? existing))
            {
                return existing;
            }

            CanvasBitmap bitmap = await SlideImageLoader.LoadBitmapAsync(device, project.Slides[slideIndex].SourcePath);
            decodedBitmaps[slideIndex] = bitmap;
            return bitmap;
        }

        void ReleaseBitmapsExcept(params int[] keep)
        {
            foreach (int index in decodedBitmaps.Keys.ToArray())
            {
                if (keep.Contains(index))
                {
                    continue;
                }

                decodedBitmaps[index].Dispose();
                decodedBitmaps.Remove(index);
            }
        }

        var videoProperties = VideoEncodingProperties.CreateUncompressed(
            MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
        var videoDescriptor = new VideoStreamDescriptor(videoProperties);
        var streamSource = new MediaStreamSource(videoDescriptor)
        {
            BufferTime = TimeSpan.Zero,
            Duration = project.Runtime,
            IsLive = false,
        };

        streamSource.SampleRequested += (sender, args) =>
        {
            MediaStreamSourceSampleRequest request = args.Request;
            MediaStreamSourceSampleRequestDeferral deferral = request.GetDeferral();

            _ = Task.Run(async () =>
            {
                // SampleRequested can fire concurrently; the shared timestamp counter and
                // decode window are not thread-safe, so frames are produced one at a time.
                // Both the gate and the deferral must be released on every exit path, or the
                // next frame deadlocks waiting on a gate this one never gave back.
                await frameGate.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (nextTimestamp >= project.Runtime)
                    {
                        return; // leaving request.Sample unset signals end-of-stream
                    }

                    TimeSpan t = nextTimestamp;
                    nextTimestamp += FrameDuration;
                    progress?.Report(nextTimestamp.TotalMilliseconds / project.Runtime.TotalMilliseconds);

                    SlidePosition position = SlideTimeline.Locate(project, t);
                    await EnsureDecodedAsync(position.SlideIndex);
                    if (position.NextSlideIndex is { } nextIndex)
                    {
                        await EnsureDecodedAsync(nextIndex);
                        ReleaseBitmapsExcept(position.SlideIndex, nextIndex);
                    }
                    else
                    {
                        ReleaseBitmapsExcept(position.SlideIndex);
                    }

                    // Not disposed here: the transcoder reads the underlying Direct3D surface
                    // asynchronously after this method returns, and disposing eagerly risks a
                    // use-after-dispose on the GPU side. Left to the GC instead — unverified
                    // against the <800MB peak-memory target (spec §5) on a long render; profile
                    // this during M1 verification before trusting it on a 300-photo set.
                    var renderTarget = new CanvasRenderTarget(device, width, height, 96);
                    using (CanvasDrawingSession session = renderTarget.CreateDrawingSession())
                    {
                        FrameComposer.Render(session, project, t, index => decodedBitmaps[index], targetSize);
                    }

                    var surface = (IDirect3DSurface)renderTarget;
                    MediaStreamSample sample = MediaStreamSample.CreateFromDirect3D11Surface(surface, t);
                    sample.Duration = FrameDuration;
                    request.Sample = sample;
                }
                finally
                {
                    frameGate.Release();
                    deferral.Complete();
                }
            }, cancellationToken);
        };

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            File.WriteAllBytes(outputPath, []);
            StorageFile outputFile = await StorageFile.GetFileFromPathAsync(outputPath);
            using IRandomAccessStream outputStream = await outputFile.OpenAsync(FileAccessMode.ReadWrite);

            PrepareTranscodeResult prepareResult =
                await transcoder.PrepareMediaStreamSourceTranscodeAsync(streamSource, outputStream, profile);
            if (!prepareResult.CanTranscode)
            {
                throw new InvalidOperationException(
                    $"Cannot transcode slideshow to MP4: {prepareResult.FailureReason}.");
            }

            await prepareResult.TranscodeAsync().AsTask(cancellationToken);
        }
        catch
        {
            // A cancelled or failed transcode can leave the pre-created placeholder (or a
            // partially-written file) behind at outputPath (spec §5: "must clean up partial
            // output") — File.Delete no-ops if it was never created. Caught broadly (not just
            // IOException): a read-only attribute or a brief AV/indexer lock throws
            // UnauthorizedAccessException, and a failed cleanup attempt must never replace the
            // real exception below with its own.
            try
            {
                File.Delete(outputPath);
            }
            catch (Exception)
            {
            }

            throw;
        }
        finally
        {
            foreach (CanvasBitmap bitmap in decodedBitmaps.Values)
            {
                bitmap.Dispose();
            }
        }
    }
}
