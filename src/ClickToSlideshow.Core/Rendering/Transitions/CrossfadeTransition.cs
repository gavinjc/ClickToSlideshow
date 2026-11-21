using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;

namespace ClickToSlideshow.Core.Rendering.Transitions;

/// <summary>Two DrawImage calls with an alpha ramp — the shallowest possible use of Win2D (spec §4.6).</summary>
public sealed class CrossfadeTransition : ITransition
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

        session.Transform = fromTransform;
        session.DrawImage(from, Vector2.Zero, from.GetBounds(session), 1f);

        session.Transform = toTransform;
        session.DrawImage(to, Vector2.Zero, to.GetBounds(session), (float)progress);

        session.Transform = Matrix3x2.Identity;
    }
}
