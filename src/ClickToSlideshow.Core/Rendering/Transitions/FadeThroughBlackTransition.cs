using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;

namespace ClickToSlideshow.Core.Rendering.Transitions;

/// <summary>Alpha ramp via the background colour, not a second image (spec §4.6).</summary>
public sealed class FadeThroughBlackTransition : ITransition
{
    public void Draw(
        CanvasDrawingSession session,
        CanvasBitmap from,
        Matrix3x2 fromTransform,
        CanvasBitmap to,
        Matrix3x2 toTransform,
        double progress,
        Size canvasSize,
        Color backgroundColor)
    {
        session.Clear(backgroundColor);

        if (progress < 0.5)
        {
            float opacity = 1f - (float)(progress / 0.5);
            session.Transform = fromTransform;
            session.DrawImage(from, Vector2.Zero, from.GetBounds(session), opacity);
        }
        else
        {
            float opacity = (float)((progress - 0.5) / 0.5);
            session.Transform = toTransform;
            session.DrawImage(to, Vector2.Zero, to.GetBounds(session), opacity);
        }

        session.Transform = Matrix3x2.Identity;
    }
}
