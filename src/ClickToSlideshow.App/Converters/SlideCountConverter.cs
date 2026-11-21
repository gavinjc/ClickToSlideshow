using Microsoft.UI.Xaml.Data;
using Microsoft.Windows.ApplicationModel.Resources;

namespace ClickToSlideshow.App.Converters;

public sealed class SlideCountConverter : IValueConverter
{
    private static readonly ResourceLoader ResourceLoader = new();

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        int count = value is int i ? i : 0;
        return count == 1
            ? ResourceLoader.GetString("SlideCountSingular")
            : string.Format(ResourceLoader.GetString("SlideCountPluralFormat"), count);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
