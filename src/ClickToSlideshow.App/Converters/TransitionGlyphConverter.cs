using ClickToSlideshow.Core.Model;
using Microsoft.UI.Xaml.Data;

namespace ClickToSlideshow.App.Converters;

// Per-tile leading-edge transition indicator (spec §4.3). Only a per-slide TransitionIn override
// is shown — null (use the project default) renders as nothing, since flagging every tile with
// the same glyph the whole show already uses would be noise, not information; the glyph exists
// to flag the exceptions.
public sealed class TransitionGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        TransitionKind.Cut => "✂",
        TransitionKind.Crossfade => "⇄",
        TransitionKind.FadeThroughBlack => "◐",
        TransitionKind.PushLeft => "←",
        TransitionKind.PushRight => "→",
        TransitionKind.PushUp => "↑",
        TransitionKind.PushDown => "↓",
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
