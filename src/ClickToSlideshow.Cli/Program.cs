using System.Diagnostics;
using System.Text;
using ClickToSlideshow.Core;
using ClickToSlideshow.Core.Audio;
using ClickToSlideshow.Core.Imaging;
using ClickToSlideshow.Core.Model;
using Windows.Storage;
using Windows.Storage.FileProperties;

string? listPath = null;
string? audioPath = null;
string? outputPath = null;
int slideMs = 4000;
int transitionMs = 1000;
TransitionKind transitionKind = TransitionKind.Crossfade;
Resolution resolution = Resolution.HD1080p;
FitMode fitMode = FitMode.Fit;

try
{
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--list" when i + 1 < args.Length:
                listPath = args[++i];
                break;
            case "--audio" when i + 1 < args.Length:
                audioPath = args[++i];
                break;
            case "--output" when i + 1 < args.Length:
                outputPath = args[++i];
                break;
            case "--slide-ms" when i + 1 < args.Length:
                slideMs = int.Parse(args[++i]);
                break;
            case "--transition-ms" when i + 1 < args.Length:
                transitionMs = int.Parse(args[++i]);
                break;
            case "--transition" when i + 1 < args.Length:
                transitionKind = ParseTransition(args[++i]);
                break;
            case "--resolution" when i + 1 < args.Length:
                resolution = ParseResolution(args[++i]);
                break;
            case "--fit" when i + 1 < args.Length:
                fitMode = ParseFit(args[++i]);
                break;
        }
    }
}
catch (Exception ex) when (ex is FormatException or ArgumentException)
{
    Console.Error.WriteLine($"Bad argument: {ex.Message}");
    return 1;
}

if (listPath is null || outputPath is null)
{
    Console.Error.WriteLine("Usage: ClickToSlideshow.Cli --list <path> --output <output.mp4> [--audio <path>]");
    Console.Error.WriteLine("                            [--slide-ms <int>] [--transition-ms <int>]");
    Console.Error.WriteLine("                            [--transition cut|crossfade|fade-black|push-left|push-right|push-up|push-down]");
    Console.Error.WriteLine("                            [--resolution 1080p|2160p|original] [--fit fit|fill]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("The list file is UTF-8, one absolute image path per line (same shape the");
    Console.Error.WriteLine("shell extension's temp-file handshake will use in a later milestone).");
    return 1;
}

if (!File.Exists(listPath))
{
    Console.Error.WriteLine($"List file not found: {listPath}");
    return 1;
}

string[] imagePaths = File.ReadAllLines(listPath, Encoding.UTF8)
    .Where(line => !string.IsNullOrWhiteSpace(line))
    .ToArray();

if (imagePaths.Length == 0)
{
    Console.Error.WriteLine("List file contained no image paths.");
    return 1;
}

if (audioPath is not null && !File.Exists(audioPath))
{
    Console.Error.WriteLine($"Audio file not found: {audioPath}");
    return 1;
}

Console.WriteLine($"Building project: {imagePaths.Length} slide(s), transition={transitionKind}, resolution={resolution}");

var slides = new List<Slide>();
foreach (string path in imagePaths)
{
    int rotation = await SlideImageLoader.DetectExifRotationDegreesAsync(path);
    slides.Add(new Slide { SourcePath = path, DurationMs = slideMs, RotationDegrees = rotation });
}

var audioTracks = new List<AudioTrack>();
if (audioPath is not null)
{
    string playablePath = await OggAudioTranscoder.EnsurePlayablePathAsync(audioPath);
    StorageFile audioFile = await StorageFile.GetFileFromPathAsync(playablePath);
    MusicProperties musicProperties = await audioFile.Properties.GetMusicPropertiesAsync();
    audioTracks.Add(new AudioTrack
    {
        SourcePath = audioPath,
        PlayablePath = playablePath,
        DurationMs = (int)musicProperties.Duration.TotalMilliseconds,
    });
}

var project = new SlideshowProject
{
    Slides = slides,
    AudioTracks = audioTracks,
    DefaultSlideMs = slideMs,
    TransitionKind = transitionKind,
    TransitionMs = transitionMs,
    Resolution = resolution,
    FitMode = fitMode,
};

Console.WriteLine($"Rendering -> {outputPath} (runtime {project.Runtime:mm\\:ss\\.fff})");

Stopwatch stopwatch = Stopwatch.StartNew();
try
{
    await SlideshowRenderer.RenderAsync(project, outputPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Render failed: {ex.Message}");
    return 1;
}
stopwatch.Stop();

long outputBytes = new FileInfo(outputPath).Length;
double realtimeRatio = project.Runtime.TotalSeconds / stopwatch.Elapsed.TotalSeconds;

Console.WriteLine($"Done in {stopwatch.Elapsed:mm\\:ss\\.fff} ({realtimeRatio:F1}x realtime)");
Console.WriteLine($"Output: {outputBytes,12:N0} bytes -> {outputPath}");

return 0;

static TransitionKind ParseTransition(string value) => value switch
{
    "cut" => TransitionKind.Cut,
    "crossfade" => TransitionKind.Crossfade,
    "fade-black" => TransitionKind.FadeThroughBlack,
    "push-left" => TransitionKind.PushLeft,
    "push-right" => TransitionKind.PushRight,
    "push-up" => TransitionKind.PushUp,
    "push-down" => TransitionKind.PushDown,
    _ => throw new ArgumentException($"Unknown --transition value '{value}'."),
};

static Resolution ParseResolution(string value) => value switch
{
    "1080p" => Resolution.HD1080p,
    "2160p" => Resolution.UHD2160p,
    "original" => Resolution.Original,
    _ => throw new ArgumentException($"Unknown --resolution value '{value}'."),
};

static FitMode ParseFit(string value) => value switch
{
    "fit" => FitMode.Fit,
    "fill" => FitMode.Fill,
    _ => throw new ArgumentException($"Unknown --fit value '{value}'."),
};
