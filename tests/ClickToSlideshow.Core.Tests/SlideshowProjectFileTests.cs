using System.Text.Json;
using ClickToSlideshow.Core.Model;
using ClickToSlideshow.Core.Serialization;
using Windows.UI;

namespace ClickToSlideshow.Core.Tests;

public class SlideshowProjectFileTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "ClickToSlideshowTests", Guid.NewGuid().ToString());

    public SlideshowProjectFileTests() => Directory.CreateDirectory(_tempDirectory);

    public void Dispose() => Directory.Delete(_tempDirectory, recursive: true);

    private string JsonPath => Path.Combine(_tempDirectory, "project.json");

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        string imagePath = Path.Combine(_tempDirectory, "photos", "a.jpg");
        string audioPath = Path.Combine(_tempDirectory, "track.mp3");
        var project = new SlideshowProject
        {
            Slides =
            {
                new Slide
                {
                    SourcePath = imagePath,
                    DurationMs = 3500,
                    RotationDegrees = 90,
                    TransitionIn = TransitionKind.PushLeft,
                },
            },
            AudioTracks = { new AudioTrack { SourcePath = audioPath, DurationMs = 12000 } },
            DefaultSlideMs = 5000,
            TransitionKind = TransitionKind.FadeThroughBlack,
            TransitionMs = 800,
            Resolution = Resolution.UHD2160p,
            FitMode = FitMode.Fill,
            BackgroundColor = new Color { A = 255, R = 12, G = 34, B = 56 },
            AudioMatch = AudioMatch.FitSlides,
        };

        SlideshowProjectFile.Save(project, JsonPath);
        SlideshowProject loaded = SlideshowProjectFile.Load(JsonPath);

        Assert.Equal(imagePath, loaded.Slides[0].SourcePath);
        Assert.Equal(3500, loaded.Slides[0].DurationMs);
        Assert.Equal(90, loaded.Slides[0].RotationDegrees);
        Assert.Equal(TransitionKind.PushLeft, loaded.Slides[0].TransitionIn);
        Assert.Equal(audioPath, loaded.AudioTracks[0].SourcePath);
        Assert.Equal(12000, loaded.AudioTracks[0].DurationMs);
        Assert.Equal(5000, loaded.DefaultSlideMs);
        Assert.Equal(TransitionKind.FadeThroughBlack, loaded.TransitionKind);
        Assert.Equal(800, loaded.TransitionMs);
        Assert.Equal(Resolution.UHD2160p, loaded.Resolution);
        Assert.Equal(FitMode.Fill, loaded.FitMode);
        Assert.Equal(255, loaded.BackgroundColor.A);
        Assert.Equal(12, loaded.BackgroundColor.R);
        Assert.Equal(34, loaded.BackgroundColor.G);
        Assert.Equal(56, loaded.BackgroundColor.B);
        Assert.Equal(AudioMatch.FitSlides, loaded.AudioMatch);
    }

    [Fact]
    public void Save_SourceUnderProjectDirectory_StoresRelativePath()
    {
        string sourcePath = Path.Combine(_tempDirectory, "photos", "a.jpg");
        var project = new SlideshowProject { Slides = { new Slide { SourcePath = sourcePath } } };

        SlideshowProjectFile.Save(project, JsonPath);

        string storedPath = ReadStoredSlideSourcePath();
        Assert.False(Path.IsPathRooted(storedPath));
        Assert.Equal(Path.Combine("photos", "a.jpg"), storedPath);
    }

    [Fact]
    public void Save_SourceOnADifferentRoot_FallsBackToAbsolutePath()
    {
        // Pure string math (Path.GetRelativePath) — the fallback root need not actually exist.
        string differentRoot = Path.GetPathRoot(_tempDirectory) == @"Z:\" ? @"Y:\" : @"Z:\";
        string sourcePath = Path.Combine(differentRoot, "photos", "a.jpg");
        var project = new SlideshowProject { Slides = { new Slide { SourcePath = sourcePath } } };

        SlideshowProjectFile.Save(project, JsonPath);

        string storedPath = ReadStoredSlideSourcePath();
        Assert.True(Path.IsPathRooted(storedPath));
        Assert.Equal(sourcePath, storedPath);

        SlideshowProject loaded = SlideshowProjectFile.Load(JsonPath);
        Assert.Equal(sourcePath, loaded.Slides[0].SourcePath);
    }

    [Fact]
    public void Load_FormatVersionNewerThanSupported_ThrowsWithPathInMessage()
    {
        File.WriteAllText(JsonPath, /* lang=json */ """
            {
              "FormatVersion": 999,
              "Slides": [],
              "AudioTracks": [],
              "DefaultSlideMs": 4000,
              "TransitionKind": "Crossfade",
              "TransitionMs": 1000,
              "Resolution": "HD1080p",
              "FitMode": "Fit",
              "BackgroundColor": "#FF000000",
              "AudioMatch": "Loop"
            }
            """);

        // The path matters here, not just the exception type: MainViewModel.LoadProjectAsync
        // surfaces ex.Message verbatim as the on-screen error, so a user opening a stale project
        // needs to see which file it's complaining about.
        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => SlideshowProjectFile.Load(JsonPath));
        Assert.Contains(JsonPath, exception.Message);
    }

    private string ReadStoredSlideSourcePath()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(JsonPath));
        return document.RootElement.GetProperty("Slides")[0].GetProperty("SourcePath").GetString()!;
    }
}
