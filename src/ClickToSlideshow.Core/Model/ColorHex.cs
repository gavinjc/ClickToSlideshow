using Windows.UI;

namespace ClickToSlideshow.Core.Model;

/// <summary>
/// Converts a <see cref="Color"/> to and from its "#AARRGGBB" hex string form — shared by
/// project-JSON serialization and the App's colour swatches so both agree on one format.
/// </summary>
public static class ColorHex
{
    public static string Format(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    public static Color Parse(string hex) => new()
    {
        A = Convert.ToByte(hex.Substring(1, 2), 16),
        R = Convert.ToByte(hex.Substring(3, 2), 16),
        G = Convert.ToByte(hex.Substring(5, 2), 16),
        B = Convert.ToByte(hex.Substring(7, 2), 16),
    };
}
