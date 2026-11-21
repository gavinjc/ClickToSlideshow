using Microsoft.UI.Xaml.Data;

namespace ClickToSlideshow.App.Converters;

// Tile duration badge (spec §4.3). Only shown for a per-slide override — null (use the project
// default) renders as nothing, matching TransitionGlyphConverter's reasoning: the badge exists to
// flag the exceptions, not to repeat the same default value across every tile in the grid.
public sealed class DurationBadgeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not int durationMs)
        {
            return "";
        }

        return $"{durationMs / 1000.0:0.#}s";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
