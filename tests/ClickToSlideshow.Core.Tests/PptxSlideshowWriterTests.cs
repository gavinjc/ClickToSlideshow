using ClickToSlideshow.Core.Model;
using ClickToSlideshow.Core.Pptx;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using P = DocumentFormat.OpenXml.Presentation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ClickToSlideshow.Core.Tests;

public class PptxSlideshowWriterTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "ClickToSlideshowTests", Guid.NewGuid().ToString());

    public PptxSlideshowWriterTests() => Directory.CreateDirectory(_tempDirectory);

    public void Dispose() => Directory.Delete(_tempDirectory, recursive: true);

    private string PptxPath => Path.Combine(_tempDirectory, "show.pptx");

    [Fact]
    public async Task WriteAsync_TwoSlidesWithAudio_ProducesSchemaValidPackage()
    {
        SlideshowProject project = await BuildProjectAsync(withAudio: true);

        await PptxSlideshowWriter.WriteAsync(project, PptxPath);

        using PresentationDocument document = PresentationDocument.Open(PptxPath, isEditable: false);
        var validator = new OpenXmlValidator();
        List<ValidationErrorInfo> errors = [.. validator.Validate(document)];

        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => $"{e.Path?.XPath}: {e.Description}")));
    }

    [Fact]
    public async Task WriteAsync_TwoSlideProject_WritesTwoSlideParts()
    {
        SlideshowProject project = await BuildProjectAsync(withAudio: false);

        await PptxSlideshowWriter.WriteAsync(project, PptxPath);

        using PresentationDocument document = PresentationDocument.Open(PptxPath, isEditable: false);
        Assert.Equal(2, document.PresentationPart!.SlideParts.Count());
    }

    [Fact]
    public async Task WriteAsync_SlideLayoutPart_HasBackReferenceToItsSlideMaster()
    {
        // OpenXmlValidator (and the master->layout relationship the SlideLayoutIdList needs)
        // stays silent if this is missing, but real PowerPoint refuses to open the file cleanly
        // without it ("PowerPoint found a problem with content ... can attempt to repair") —
        // confirmed by opening a version of this writer's output that omitted it.
        SlideshowProject project = await BuildProjectAsync(withAudio: false);

        await PptxSlideshowWriter.WriteAsync(project, PptxPath);

        using PresentationDocument document = PresentationDocument.Open(PptxPath, isEditable: false);
        SlidePart slidePart = document.PresentationPart!.SlideParts.First();
        SlideLayoutPart layoutPart = slidePart.SlideLayoutPart!;

        Assert.Contains(layoutPart.Parts, pair => pair.OpenXmlPart is SlideMasterPart);
    }

    [Fact]
    public async Task WriteAsync_SourcesInAnotherFolder_EmbedsBareFileNamesOnly()
    {
        // A shared deck must not disclose folder names: a relative path back out of the deck's
        // folder ("..\first.jpg") or an absolute one would show up in the raw embedded part.
        SlideshowProject project = await BuildProjectAsync(withAudio: true);
        string deckPath = Path.Combine(_tempDirectory, "decks", "show.pptx");

        await PptxSlideshowWriter.WriteAsync(project, deckPath);

        using PresentationDocument document = PresentationDocument.Open(deckPath, isEditable: false);
        using var reader = new StreamReader(document.PresentationPart!.CustomXmlParts.Single().GetStream());
        string embedded = await reader.ReadToEndAsync();
        Assert.Contains("first.jpg", embedded);
        Assert.Contains("track.wav", embedded);
        Assert.DoesNotContain("..", embedded);
        Assert.DoesNotContain("ClickToSlideshowTests", embedded);
    }

    [Fact]
    public async Task WriteAsync_ThenReadEmbeddedProject_RoundTripsProjectSettings()
    {
        SlideshowProject project = await BuildProjectAsync(withAudio: true);

        await PptxSlideshowWriter.WriteAsync(project, PptxPath);
        SlideshowProject reloaded = PptxProjectReader.ReadEmbeddedProject(PptxPath);

        Assert.Equal(project.Slides.Count, reloaded.Slides.Count);
        Assert.Equal(project.Slides[0].SourcePath, reloaded.Slides[0].SourcePath);
        Assert.Equal(project.Slides[0].DurationMs, reloaded.Slides[0].DurationMs);
        Assert.Equal(project.Slides[1].RotationDegrees, reloaded.Slides[1].RotationDegrees);
        Assert.Equal(project.TransitionKind, reloaded.TransitionKind);
        Assert.Equal(project.TransitionMs, reloaded.TransitionMs);
        Assert.Equal(project.Resolution, reloaded.Resolution);
        Assert.Equal(project.FitMode, reloaded.FitMode);
        Assert.Equal(project.AudioMatch, reloaded.AudioMatch);
        Assert.Single(reloaded.AudioTracks);
        Assert.Equal(project.AudioTracks[0].SourcePath, reloaded.AudioTracks[0].SourcePath);
    }

    [Fact]
    public async Task WriteAsync_WithFitSlidesAudioMatch_EmbedsOriginalUnscaledDurationsNotRenderedOnes()
    {
        // FitSlides is the one AudioMatch mode that makes WriteAsync's internal rendering view
        // (SlideshowRenderer.EffectiveProject) differ from the project it's handed — this
        // project's slides sum to 7000ms + 1 transition (500ms) = 7500ms against a 4000ms audio
        // track, so EffectiveProject scales every slide duration by 4000/7500 for rendering.
        // The embedded custom XML part must still carry the original, unscaled 3000/4000ms
        // values (spec §4.2: FitSlides "never rewrites the stored per-slide values"), exactly
        // like SlideshowProjectFile.Save already guarantees for plain JSON.
        SlideshowProject project = await BuildProjectAsync(withAudio: true);
        project.AudioMatch = AudioMatch.FitSlides;
        project.AudioTracks[0] = new AudioTrack { SourcePath = project.AudioTracks[0].SourcePath, DurationMs = 4000 };

        await PptxSlideshowWriter.WriteAsync(project, PptxPath);
        SlideshowProject reloaded = PptxProjectReader.ReadEmbeddedProject(PptxPath);

        Assert.Equal(3000, reloaded.Slides[0].DurationMs);
        Assert.Equal(4000, reloaded.Slides[1].DurationMs);
    }

    [Fact]
    public async Task WriteAsync_SlideNotes_ContainOriginalFileName()
    {
        SlideshowProject project = await BuildProjectAsync(withAudio: false);

        await PptxSlideshowWriter.WriteAsync(project, PptxPath);

        using PresentationDocument document = PresentationDocument.Open(PptxPath, isEditable: false);
        SlidePart firstSlide = document.PresentationPart!.SlideParts.First();
        string notesText = firstSlide.NotesSlidePart!.NotesSlide!.InnerText;

        Assert.Contains(Path.GetFileName(project.Slides[0].SourcePath), notesText);
    }

    [Fact]
    public async Task WriteAsync_RotatedSlide_SetsRotationOnPictureShapeTransform()
    {
        SlideshowProject project = await BuildProjectAsync(withAudio: false);

        await PptxSlideshowWriter.WriteAsync(project, PptxPath);

        using PresentationDocument document = PresentationDocument.Open(PptxPath, isEditable: false);
        SlidePart rotatedSlidePart = document.PresentationPart!.SlideParts.ElementAt(1);
        P.Picture picture = rotatedSlidePart.Slide!.CommonSlideData!.ShapeTree!.Elements<P.Picture>().Single();

        // Slide 1 (index 1) was built with RotationDegrees = 90 below.
        Assert.Equal(90 * 60000, picture.ShapeProperties!.Transform2D!.Rotation!.Value);
    }

    [Fact]
    public async Task WriteAsync_SlideWithTransition_CarriesConfiguredDurationAsPowerPointDurAttribute()
    {
        // The configured length used to be written as a made-up <p:extLst> element that PowerPoint
        // ignored (it showed its 0.5s default). 800ms is deliberately not that default.
        SlideshowProject project = await BuildProjectAsync(withAudio: false);
        project.TransitionMs = 800;

        await PptxSlideshowWriter.WriteAsync(project, PptxPath);

        using PresentationDocument document = PresentationDocument.Open(PptxPath, isEditable: false);
        SlidePart secondSlide = document.PresentationPart!.SlideParts.ElementAt(1);
        var slideXml = System.Xml.Linq.XDocument.Parse(secondSlide.Slide!.OuterXml);

        System.Xml.Linq.XNamespace mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
        System.Xml.Linq.XNamespace p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        System.Xml.Linq.XNamespace p14 = "http://schemas.microsoft.com/office/powerpoint/2010/main";
        System.Xml.Linq.XElement alternate = slideXml.Root!.Element(mc + "AlternateContent")!;

        Assert.Equal("800", (string?)alternate.Element(mc + "Choice")!.Element(p + "transition")!.Attribute(p14 + "dur"));
        Assert.Null(alternate.Element(mc + "Fallback")!.Element(p + "transition")!.Attribute(p14 + "dur"));
    }

    [Fact]
    public async Task WriteAsync_TwoAudioTracks_EmbedsOneMp3CoveringTheWholeShow()
    {
        // Slides run 3000 + 4000 + 500 (one transition gap) = 7500ms. Track 1 alone is 3s, so an
        // embedded soundtrack close to 7.5s can only be the two tracks joined; the previous
        // writer embedded track 1 alone (and its original bytes, whatever their format).
        SlideshowProject project = await BuildProjectAsync(withAudio: false);
        project.AudioTracks.Add(new AudioTrack { SourcePath = await WriteTestWavAsync("one.wav", seconds: 3), DurationMs = 3000 });
        project.AudioTracks.Add(new AudioTrack { SourcePath = await WriteTestWavAsync("two.wav", seconds: 4), DurationMs = 4000 });

        await PptxSlideshowWriter.WriteAsync(project, PptxPath);

        string extractedPath = Path.Combine(_tempDirectory, "extracted.mp3");
        using (PresentationDocument document = PresentationDocument.Open(PptxPath, isEditable: false))
        {
            DataPart soundtrack = Assert.Single(document.DataParts, part => part.ContentType.StartsWith("audio/"));
            Assert.Equal("audio/mpeg", soundtrack.ContentType);

            using Stream source = soundtrack.GetStream();
            using FileStream destination = File.Create(extractedPath);
            source.CopyTo(destination);
        }

        StorageFile extracted = await StorageFile.GetFileFromPathAsync(extractedPath);
        var track = await Windows.Media.Editing.BackgroundAudioTrack.CreateFromFileAsync(extracted);
        Assert.InRange(track.OriginalDuration.TotalMilliseconds, 7300, 7800);
    }

    [Fact]
    public async Task WriteAsync_OriginalResolution_SizesSlidesToTheLargestPhoto()
    {
        // Original used to throw NotSupportedException before anything was written. The size rule
        // itself (rotation, limits) is covered by OriginalFrameSizeTests; this checks it reaches
        // the deck.
        string small = await WriteTestJpegAsync("small.jpg", 300, 400);
        string large = await WriteTestJpegAsync("large.jpg", 640, 480);
        var project = new SlideshowProject
        {
            Slides =
            {
                new Slide { SourcePath = small, RotationDegrees = 90 },
                new Slide { SourcePath = large },
            },
            Resolution = Resolution.Original,
        };

        await PptxSlideshowWriter.WriteAsync(project, PptxPath);

        using PresentationDocument document = PresentationDocument.Open(PptxPath, isEditable: false);
        P.SlideSize slideSize = document.PresentationPart!.Presentation!.SlideSize!;
        const double emuPerPixel = 914400.0 / 96.0;
        Assert.Equal((long)Math.Round(640 * emuPerPixel), slideSize.Cx!.Value);
        Assert.Equal((long)Math.Round(480 * emuPerPixel), slideSize.Cy!.Value);
    }

    // The save progress row shows these; a slide step that never reached 1 would leave the bar
    // short of full when the Saved message appears.
    [Fact]
    public async Task WriteAsync_ReportsOneSlideStepPerSlide_EndingAtOne()
    {
        SlideshowProject project = await BuildProjectAsync(withAudio: false);
        var progress = new RecordingProgress();

        await PptxSlideshowWriter.WriteAsync(project, PptxPath, progress);

        Assert.Equal(
            [new RenderProgress(RenderPhase.Frames, 0.5), new RenderProgress(RenderPhase.Frames, 1.0)],
            progress.Reports);
    }

    // With music the soundtrack is rendered first, so the row must open on "Adding music" and
    // the soundtrack renderer's own reports must reach it, not just the slide steps.
    [Fact]
    public async Task WriteAsync_WithAudio_ReportsTheSoundtrackPhaseFirst_ThenTheSlides()
    {
        SlideshowProject project = await BuildProjectAsync(withAudio: true);
        var progress = new RecordingProgress();

        await PptxSlideshowWriter.WriteAsync(project, PptxPath, progress);

        // The renderer's reports are posted through a Progress<double>, so give them time to land.
        for (int waited = 0; waited < 5000 && !progress.Reports.Any(r => r.Phase == RenderPhase.Audio && r.Fraction > 0); waited += 50)
        {
            await Task.Delay(50);
        }

        Assert.Equal(new RenderProgress(RenderPhase.Audio, 0), progress.Reports[0]);
        Assert.Contains(progress.Reports, r => r.Phase == RenderPhase.Audio && r.Fraction > 0);
        Assert.Equal(new RenderProgress(RenderPhase.Frames, 1.0), progress.Reports.Last(r => r.Phase == RenderPhase.Frames));
    }

    // Cancel on the save progress row promises no half-made file is left behind.
    [Fact]
    public async Task WriteAsync_Cancelled_LeavesNoFile()
    {
        SlideshowProject project = await BuildProjectAsync(withAudio: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PptxSlideshowWriter.WriteAsync(project, PptxPath, progress: null, cancellation.Token));

        Assert.False(File.Exists(PptxPath));
    }

    // Progress<T> posts each report to a synchronisation context, so its order and timing in a
    // test would be uncertain; this records them as they're made. A queue, because the writer's
    // inner Progress<double> can deliver from another thread while the test reads.
    private sealed class RecordingProgress : IProgress<RenderProgress>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<RenderProgress> _reports = new();

        public RenderProgress[] Reports => _reports.ToArray();

        public void Report(RenderProgress value) => _reports.Enqueue(value);
    }

    private async Task<SlideshowProject> BuildProjectAsync(bool withAudio)
    {
        string firstImagePath = await WriteTestJpegAsync("first.jpg", 200, 100);
        string secondImagePath = await WriteTestJpegAsync("second.jpg", 100, 200);

        var project = new SlideshowProject
        {
            Slides =
            {
                new Slide { SourcePath = firstImagePath, DurationMs = 3000 },
                new Slide { SourcePath = secondImagePath, DurationMs = 4000, RotationDegrees = 90, TransitionIn = TransitionKind.PushLeft },
            },
            TransitionKind = TransitionKind.Crossfade,
            TransitionMs = 500,
            Resolution = Resolution.HD1080p,
            FitMode = FitMode.Fit,
            AudioMatch = AudioMatch.Trim,
        };

        if (withAudio)
        {
            string audioPath = await WriteTestWavAsync("track.wav", seconds: 5);
            project.AudioTracks.Add(new AudioTrack { SourcePath = audioPath, DurationMs = 5000 });
        }

        return project;
    }

    private async Task<string> WriteTestJpegAsync(string fileName, int width, int height)
    {
        string path = Path.Combine(_tempDirectory, fileName);
        using SoftwareBitmap sourceBitmap = new(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);

        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(_tempDirectory);
        StorageFile outputFile = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

        using IRandomAccessStream stream = await outputFile.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        encoder.SetSoftwareBitmap(sourceBitmap);
        await encoder.FlushAsync();

        return path;
    }

    // Real, decodable audio: the writer renders a soundtrack through Media Foundation, so a
    // header-only stub no longer works. A 440 Hz tone of the requested length, 16-bit mono 44.1 kHz.
    private async Task<string> WriteTestWavAsync(string fileName, int seconds)
    {
        const int SampleRate = 44100;
        string path = Path.Combine(_tempDirectory, fileName);
        await File.WriteAllBytesAsync(path, TestWav.BuildMonoPcm(TestWav.Tone(440, SampleRate, seconds), SampleRate));
        return path;
    }
}
