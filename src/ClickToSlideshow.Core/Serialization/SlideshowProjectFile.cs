using System.Text.Json;
using System.Text.Json.Serialization;
using ClickToSlideshow.Core.Model;
using Windows.UI;

namespace ClickToSlideshow.Core.Serialization;

/// <summary>
/// Reads and writes the project-JSON output (spec §4.8). Source paths are stored relative to
/// the project file's own directory where possible, falling back to absolute automatically —
/// <see cref="Path.GetRelativePath(string, string)"/> already returns the input unchanged when
/// the two paths don't share a root (e.g. different drives), so no separate fallback branch is
/// needed. <see cref="SlideshowProject.BackgroundColor"/> (a WinRT <see cref="Color"/> struct)
/// is mapped to a plain hex string — it has no useful default JSON representation.
/// </summary>
public static class SlideshowProjectFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Save(SlideshowProject project, string path)
    {
        string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, ToJson(project, baseDirectory));
    }

    public static SlideshowProject Load(string path)
    {
        string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;

        try
        {
            return FromJson(File.ReadAllText(path), baseDirectory);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"'{path}' is not a valid ClickTo: Slideshow project file.");
        }
        catch (InvalidOperationException ex)
        {
            // FromJson has no path of its own to report (shared with the PPTX reader, which has
            // no comparable "path" for an embedded JSON blob) — restore it here for the one
            // caller that does.
            throw new InvalidOperationException($"'{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// The JSON representation on its own, addressed relative to <paramref name="baseDirectory"/>
    /// rather than a file path — shared by <see cref="Save"/> and the PPTX writer's embedded
    /// custom XML part (spec §4.8), so both use the exact same DTO mapping and relative-path
    /// convention.
    /// </summary>
    public static string ToJson(SlideshowProject project, string baseDirectory) =>
        ToJson(project, sourcePath => Path.GetRelativePath(baseDirectory, sourcePath));

    /// <summary>
    /// As above, but <paramref name="storedPath"/> decides what each source path is written as —
    /// the PPTX writer stores the bare file name so a shared deck doesn't disclose folder names.
    /// </summary>
    public static string ToJson(SlideshowProject project, Func<string, string> storedPath)
    {
        var dto = new ProjectDto
        {
            FormatVersion = project.FormatVersion,
            Slides = project.Slides
                .Select(slide => new SlideDto
                {
                    SourcePath = storedPath(slide.SourcePath),
                    DurationMs = slide.DurationMs,
                    RotationDegrees = slide.RotationDegrees,
                    TransitionIn = slide.TransitionIn,
                })
                .ToList(),
            AudioTracks = project.AudioTracks
                .Select(track => new AudioTrackDto
                {
                    SourcePath = storedPath(track.SourcePath),
                    DurationMs = track.DurationMs,
                })
                .ToList(),
            DefaultSlideMs = project.DefaultSlideMs,
            TransitionKind = project.TransitionKind,
            TransitionMs = project.TransitionMs,
            Resolution = project.Resolution,
            FitMode = project.FitMode,
            BackgroundColor = ColorHex.Format(project.BackgroundColor),
            AudioMatch = project.AudioMatch,
        };

        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    /// <summary>The inverse of <see cref="ToJson"/> — see its doc comment for why this is split out from <see cref="Load"/>.</summary>
    public static SlideshowProject FromJson(string json, string baseDirectory)
    {
        ProjectDto dto = JsonSerializer.Deserialize<ProjectDto>(json, JsonOptions)
            ?? throw new JsonException("Deserialized to a null project.");

        if (dto.FormatVersion > SlideshowProject.CurrentFormatVersion)
        {
            throw new InvalidOperationException(
                $"This project was saved by a newer version of ClickTo: Slideshow (format {dto.FormatVersion}) "
                + $"and can't be opened by this version (format {SlideshowProject.CurrentFormatVersion}).");
        }

        return new SlideshowProject
        {
            FormatVersion = dto.FormatVersion,
            Slides = dto.Slides
                .Select(slide => new Slide
                {
                    SourcePath = ResolvePath(baseDirectory, slide.SourcePath),
                    DurationMs = slide.DurationMs,
                    RotationDegrees = slide.RotationDegrees,
                    TransitionIn = slide.TransitionIn,
                })
                .ToList(),
            AudioTracks = dto.AudioTracks
                .Select(track => new AudioTrack
                {
                    SourcePath = ResolvePath(baseDirectory, track.SourcePath),
                    DurationMs = track.DurationMs,
                })
                .ToList(),
            DefaultSlideMs = dto.DefaultSlideMs,
            TransitionKind = dto.TransitionKind,
            TransitionMs = dto.TransitionMs,
            Resolution = dto.Resolution,
            FitMode = dto.FitMode,
            BackgroundColor = ColorHex.Parse(dto.BackgroundColor),
            AudioMatch = dto.AudioMatch,
        };
    }

    // Only string math — never touches the filesystem, so a moved/missing source doesn't affect
    // loading the project itself (per-slide missing-source reporting is the App layer's job,
    // via the same decode pipeline that already reports a codec-missing file).
    private static string ResolvePath(string baseDirectory, string storedPath) =>
        Path.IsPathRooted(storedPath) ? storedPath : Path.GetFullPath(Path.Combine(baseDirectory, storedPath));

    private sealed class ProjectDto
    {
        public int FormatVersion { get; set; }

        public List<SlideDto> Slides { get; set; } = [];

        public List<AudioTrackDto> AudioTracks { get; set; } = [];

        public int DefaultSlideMs { get; set; }

        public TransitionKind TransitionKind { get; set; }

        public int TransitionMs { get; set; }

        public Resolution Resolution { get; set; }

        public FitMode FitMode { get; set; }

        public string BackgroundColor { get; set; } = "#FF000000";

        public AudioMatch AudioMatch { get; set; }
    }

    private sealed class SlideDto
    {
        public string SourcePath { get; set; } = "";

        public int? DurationMs { get; set; }

        public int RotationDegrees { get; set; }

        public TransitionKind? TransitionIn { get; set; }
    }

    private sealed class AudioTrackDto
    {
        public string SourcePath { get; set; } = "";

        public int DurationMs { get; set; }
    }
}
