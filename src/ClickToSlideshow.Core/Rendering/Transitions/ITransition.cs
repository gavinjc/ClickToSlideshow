using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;

namespace ClickToSlideshow.Core.Rendering.Transitions;

/// <summary>
/// Draws one frame of a transition between two already-positioned slides. <paramref
/// name="fromTransform"/>/<paramref name="toTransform"/> already carry each bitmap's rotation,
/// fit/fill scale and centering (see <see cref="SlideTransform"/>) — a transition only adds
/// motion or opacity on top, never repeats that positioning math.
/// </summary>
public interface ITransition
{
    void Draw(
        CanvasDrawingSession session,
        CanvasBitmap from,
        Matrix3x2 fromTransform,
        CanvasBitmap to,
        Matrix3x2 toTransform,
        double progress,
        Size canvasSize,
        Color backgroundColor);
}
