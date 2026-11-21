using Microsoft.UI.Xaml.Data;

namespace ClickToSlideshow.App.Converters;

// Audio card's per-track preview button glyph (audio-section-brief.md §3.4) — toggles between
// the play triangle and the pause bars as AudioTrackItem.IsPlaying changes.
public sealed class PlayPauseGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? "" : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
