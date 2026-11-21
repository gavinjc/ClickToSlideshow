using ClickToSlideshow.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace ClickToSlideshow.App.Converters;

// Audio card's playback position indicator (a thin bar overlaid on the waveform) — converts
// AudioTrackItem.PlaybackProgress (0..1) into a left Margin across the waveform's own fixed
// pixel width, since Thickness isn't directly bindable to a double.
public sealed class PlaybackProgressToIndicatorMarginConverter : IValueConverter
{
    // Keep in sync with the indicator Rectangle's Width in MainWindow.xaml.
    private const double IndicatorWidth = 2.0;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        double progress = value is double d ? d : 0.0;
        // Minus the indicator's own width, so it ends flush with the waveform's right edge at 1.0.
        return new Thickness(progress * (AudioTrackItem.WaveformWidth - IndicatorWidth), 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
