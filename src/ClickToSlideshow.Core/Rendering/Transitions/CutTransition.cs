using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;

namespace ClickToSlideshow.Core.Rendering.Transitions;

/// <summary>No blend — holds the outgoing slide, then snaps to the incoming one (spec §4.6).</summary>
public sealed class CutTransition : ITransition
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

        bool showIncoming = progress >= 1.0;
        session.Transform = showIncoming ? toTransform : fromTransform;
        session.DrawImage(showIncoming ? to : from);
        session.Transform = Matrix3x2.Identity;
    }
}
