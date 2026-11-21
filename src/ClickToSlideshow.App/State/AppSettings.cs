using System.Text.Json;
using ClickToSlideshow.Core;

namespace ClickToSlideshow.App.State;

/// <summary>Zoom slider size and last-picked Sort order — the app's only persisted state.</summary>
public sealed record AppSettingsData(double ZoomValue = 200, SortOrder LastSortOrder = SortOrder.NameNatural);

public static class AppSettings
{
    private static readonly string DefaultFilePath = Path.Combine(AppDataFolders.LocalRoot, "settings.json");

    /// <summary>Best-effort: a missing or corrupt settings file returns defaults rather than throwing.</summary>
    public static AppSettingsData Load(string? filePath = null)
    {
        try
        {
            string path = filePath ?? DefaultFilePath;
            if (!File.Exists(path))
            {
                return new AppSettingsData();
            }

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettingsData>(json) ?? new AppSettingsData();
        }
        catch (Exception)
        {
            return new AppSettingsData();
        }
    }

    /// <summary>Best-effort: a failed write is swallowed rather than interrupting shutdown.</summary>
    public static void Save(AppSettingsData data, string? filePath = null)
    {
        try
        {
            string path = filePath ?? DefaultFilePath;
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(data));
        }
        catch (Exception)
        {
            // Untidy leftover/missing settings file is harmless — never block on it.
        }
    }
}
