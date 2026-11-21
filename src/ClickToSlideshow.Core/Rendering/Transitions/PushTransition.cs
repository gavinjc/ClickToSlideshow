using System.Numerics;
using ClickToSlideshow.Core.Model;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;

namespace ClickToSlideshow.Core.Rendering.Transitions;

/// <summary>Offset transform on both sources — one direction per instance (spec §4.6).</summary>
public sealed class PushTransition : ITransition
{
    private readonly TransitionKind direction;

    public PushTransition(TransitionKind direction)
    {
        if (direction is not (TransitionKind.PushLeft or TransitionKind.PushRight
            or TransitionKind.PushUp or TransitionKind.PushDown))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Not a push direction.");
        }

        this.direction = direction;
    }

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
        Vector2 axis = direction is TransitionKind.PushLeft or TransitionKind.PushRight
            ? new Vector2((float)canvasSize.Width, 0)
            : new Vector2(0, (float)canvasSize.Height);

        float sign = direction is TransitionKind.PushLeft or TransitionKind.PushUp ? -1f : 1f;
        Vector2 outgoingOffset = axis * sign * (float)progress;
        Vector2 incomingOffset = outgoingOffset - (axis * sign);

        session.Clear(backgroundColor);

        session.Transform = fromTransform * Matrix3x2.CreateTranslation(outgoingOffset);
        session.DrawImage(from, Vector2.Zero, from.GetBounds(session), 1f);

        session.Transform = toTransform * Matrix3x2.CreateTranslation(incomingOffset);
        session.DrawImage(to, Vector2.Zero, to.GetBounds(session), 1f);

        session.Transform = Matrix3x2.Identity;
    }
}
