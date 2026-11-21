using System.Numerics;
using ClickToSlideshow.Core.Model;
using ClickToSlideshow.Core.Rendering.Transitions;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace ClickToSlideshow.Core.Rendering;

/// <summary>
/// Renders one frame of a <see cref="SlideshowProject"/> at a point in time. A pure function of
/// the timeline (spec §4.5): given the same project, time and already-decoded bitmaps, the
/// output is always identical, so preview (M3) and export share this exact code with only the
/// clock differing. Decoding and caching which bitmaps are resident is the caller's job — see
/// <see cref="Video.Mp4VideoRenderer"/> — so this method never touches disk or GPU-decode.
/// </summary>
public static class FrameComposer
{
    public static void Render(
        CanvasDrawingSession session,
        SlideshowProject project,
        TimeSpan t,
        Func<int, CanvasBitmap> bitmapForSlide,
        Size targetSize)
    {
        SlidePosition position = SlideTimeline.Locate(project, t);

        CanvasBitmap currentBitmap = bitmapForSlide(position.SlideIndex);
        Matrix3x2 currentTransform = SlideTransform.Compute(
            currentBitmap.Size, project.Slides[position.SlideIndex].RotationDegrees, project.FitMode, targetSize);

        if (position.NextSlideIndex is not { } nextIndex)
        {
            session.Clear(project.BackgroundColor);
            session.Transform = currentTransform;
            session.DrawImage(currentBitmap);
            session.Transform = Matrix3x2.Identity;
            return;
        }

        CanvasBitmap nextBitmap = bitmapForSlide(nextIndex);
        Matrix3x2 nextTransform = SlideTransform.Compute(
            nextBitmap.Size, project.Slides[nextIndex].RotationDegrees, project.FitMode, targetSize);

        ITransition transition = TransitionCatalog.For(project.TransitionInto(nextIndex));
        transition.Draw(
            session, currentBitmap, currentTransform, nextBitmap, nextTransform,
            position.TransitionProgress, targetSize, project.BackgroundColor);
    }
}
