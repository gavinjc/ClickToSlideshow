# MediaComposition can't do transitions

*Draft 2 — code and gotchas now verified against `ClickToSlideshow.Core`. Remaining to-dos at the
bottom.*

---

I wanted a crossfade.

Not a complicated one. Two photographs, one second, the first dissolving into the second —
the single most common transition in the history of slideshows. I was building a Windows app
that turns a folder of photos and an MP3 into a video, and Windows ships an API that composes
video from clips, applies effects, mixes in background audio and renders the result to MP4.
`Windows.Media.Editing.MediaComposition`. Built in since Windows 10, no dependencies, uses the
OS encoder.

It cannot crossfade. Not "doesn't yet" — cannot, by construction. It took me a while to be sure
of that, because nothing says so anywhere, so here is the write-up I wanted to find.

## What MediaComposition is good at

Credit first, because it is genuinely useful. You build a `MediaComposition`, add `MediaClip`s —
from video files, from still images with a duration, from a flat colour — add
`BackgroundAudioTrack`s, and call `RenderToFileAsync`. You can preview the whole thing through
`GeneratePreviewMediaStreamSource` into a `MediaPlayerElement`. It is maybe forty lines to go
from a list of files to a playable MP4, hardware-encoded, with no third-party dependency and no
codec licensing to think about.

For assembling clips end to end, it is the right tool and I still use it. Just not for this.

## Three dead ends

**`IBasicVideoEffect` processes one stream.** This is the documented extensibility point —
implement the interface, add it to a clip's `VideoEffectDefinitions`, and you get each frame to
do what you like with. Except you get each frame *of that clip*. A transition is, definitionally,
two sources visible at once. There is no frame of clip B available while you are processing a
frame of clip A, because clip B is not playing yet.

**`MediaComposition.Clips` is strictly sequential.** Clips sit end to end on one timeline. There
is no overlap, no track index, no offset that lets B start before A finishes. The collection is
an ordered list, not a timeline with lanes. Whatever you were going to overlap, you cannot.

**`MediaOverlay` looks like the escape hatch, and isn't.** Overlays are the one place
MediaComposition composites two things simultaneously: you create a `MediaOverlayLayer`, add a
`MediaOverlay` wrapping a clip, and give it a `Delay`, a `Position` and an `Opacity`. So —
position clip B as an overlay starting one second before A ends, ramp its opacity from 0 to 1,
and there's your crossfade.

Except:

```csharp
public double Opacity { get; set; }
```

It's a `double`. One number, for the overlay's entire duration. There is no keyframe collection,
no animation object, no setter taking a curve or a function of time. The type signature is the
whole argument — a scalar cannot express a ramp. You can have an overlay at 50% opacity for a
second. You cannot have one that goes from 0% to 100% across that second.

## This isn't a missing feature

That distinction matters, because it determines whether you wait or rewrite.

A missing feature is something you work around, or ship without and add later. This is
architectural: the composition model has no concept of two sources overlapping in time under
author control. **One crossfade is already enough to require a different engine.** There is no
version of this where you get 80% of the way with MediaComposition and fill the gap.

I spent longer than I should have looking for the clever combination that would work. There
isn't one. If you are reading this because you are mid-search: stop, it's fine, go build the
other thing.

## The workaround to avoid

The one approach that technically works with MediaComposition: render each transition yourself —
somewhere, somehow — encode it as a short MP4, and splice those between your clips.

It does produce the right picture. It also means hundreds of tiny encodes for a long slideshow,
a temp file per join, and generation loss at every single one, because each spliced segment is a
re-encode of already-encoded frames. You have taken on all the complexity of rendering frames
yourself *and* kept all the constraints of a model that can't help you.

If you are going to render the frames anyway, render all of them.

## What to build instead

Own the frame pipeline. Composite each frame with Win2D, feed the frames to a
`MediaStreamSource`, and let `MediaTranscoder` encode with the same OS encoder MediaComposition
would have used.

```
            timeline model
                  │
                  ▼
           FrameComposer          Win2D — given time t, produce one frame
                  │
       ┌──────────┴──────────┐
       ▼                     ▼
  live playback        MediaStreamSource
                             │
                             ▼
       MediaTranscoder.PrepareMediaStreamSourceTranscodeAsync
                             │
                             ▼
                            MP4
```

One dependency: `Microsoft.Graphics.Win2D` (1.2.0 here), MIT, from Microsoft. Still no FFmpeg,
still the OS encoder, still hardware-accelerated.

### The idea that makes it tractable

`FrameComposer.Render(session, project, t, …)` is a **pure function of the timeline**. Give it a
time, it draws the frame at that time. It does not know whether it is being called by a player or
an encoder.

Everything good falls out of that:

- Preview and export are the same code with different clocks, so they **cannot** drift apart.
  This is the bug class you most want to eliminate in a renderer, and it's eliminated by
  construction rather than by discipline.
- Scrubbing is free. Seek is just calling it with a different `t`.
- A new transition is one class and no engine change. Crossfade is two `DrawImage` calls with an
  alpha value.
- Pan and zoom is a source-rect transform, not a special case.

### The surface-handoff nobody documents

Win2D's `CanvasRenderTarget` **casts directly to `IDirect3DSurface`**. No interop helper, no
`QueryInterface` dance, no copy:

```csharp
var renderTarget = new CanvasRenderTarget(device, width, height, 96);
using (CanvasDrawingSession session = renderTarget.CreateDrawingSession())
{
    FrameComposer.Render(session, project, t, …);
}

var surface = (IDirect3DSurface)renderTarget;
MediaStreamSample sample = MediaStreamSample.CreateFromDirect3D11Surface(surface, t);
sample.Duration = FrameDuration;
```

That one line is most of why this approach is viable at all — the frame never leaves the GPU on
its way to the encoder.

### Supplying the frames

```csharp
MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(quality);
profile.Audio = null;                       // video-only; audio is a second pass

// Derive the render size FROM the profile rather than computing it independently.
int width  = (int)profile.Video.Width;
int height = (int)profile.Video.Height;

var videoProperties = VideoEncodingProperties.CreateUncompressed(
    MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);

var streamSource = new MediaStreamSource(new VideoStreamDescriptor(videoProperties))
{
    BufferTime = TimeSpan.Zero,
    Duration = project.Runtime,
    IsLive = false,
};

streamSource.SampleRequested += (sender, args) =>
{
    MediaStreamSourceSampleRequest request = args.Request;
    MediaStreamSourceSampleRequestDeferral deferral = request.GetDeferral();

    _ = Task.Run(async () =>
    {
        await frameGate.WaitAsync(cancellationToken);
        try
        {
            if (nextTimestamp >= project.Runtime)
            {
                return;                      // leaving Sample unset signals end-of-stream
            }
            // … decode window, render, build sample …
            request.Sample = sample;
        }
        finally
        {
            frameGate.Release();
            deferral.Complete();
        }
    }, cancellationToken);
};

var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
PrepareTranscodeResult prepared =
    await transcoder.PrepareMediaStreamSourceTranscodeAsync(streamSource, outputStream, profile);
if (!prepared.CanTranscode)
{
    throw new InvalidOperationException($"Cannot transcode: {prepared.FailureReason}.");
}
await prepared.TranscodeAsync();
```

Five things in there that each cost me time:

**Derive the dimensions from the encoding profile.** Do not compute the render size independently
and hope it matches. A mismatch between your `VideoEncodingProperties` and the profile's own
`Video.Width`/`Height` surfaces as `CanTranscode == false` with a `FailureReason` that does not
obviously point at it. Letting the profile decide also sidesteps the separate rule that some
hardware encoders reject odd dimensions.

**Always check `CanTranscode` before `TranscodeAsync`.** `FailureReason` is the only thing that
will tell you what's wrong, and skipping the check gets you a failure much further downstream.

**`SampleRequested` can fire concurrently.** It is not serialised for you. Anything shared — a
timestamp counter, a decode window, a cache — needs a gate. And **both the gate and the deferral
must be released on every exit path**, including the early return at end-of-stream. Miss one and
the next frame request waits forever on a gate nothing will give back; the render just stops, with
no exception and no error. That one cost me an evening.

**Leaving `request.Sample` unset is how you signal end-of-stream.** There's no explicit "done"
call.

**Don't dispose the `CanvasRenderTarget` after building the sample.** The transcoder reads the
underlying Direct3D surface *asynchronously*, after your handler has returned. Disposing eagerly
is a use-after-dispose on the GPU side. Leaving it to the GC works, but it's the part of this I'd
profile hardest on a long render.

**You do not need the Graphics Capture capability.** Every sample of this pattern is about screen
recording and declares it. We are synthesising frames, not capturing a screen — so declaring it
would be requesting a permission you never use, which matters if, like this app, your whole pitch
is that it declares no capabilities it doesn't need.

### Where MediaComposition still wins

Audio. Rendering video-only and then handing that MP4 back to MediaComposition as a single clip,
with `BackgroundAudioTrack`s attached, gets you mixing, looping-to-fill and muxing for free. It
costs one re-encode of the video, which you can remove later by carrying an audio descriptor on
the same `MediaStreamSource` — but it's the right first version, and it means the API still earns
its place in the project.

It just isn't the renderer.

## The bug that actually cost me a day

Not the architecture. This:

**Win2D and WIC both silently apply EXIF orientation on decode.** `CanvasBitmap.LoadAsync`
pre-rotates pixel data to match the file's EXIF tag, by default, without saying so — and contrary
to what its own documentation implies.

My pipeline also applied its own rotation, detected separately at import and applied by the
composer on every frame. So every EXIF-tagged photograph was rotated twice.

What found it: rendering a synthetic 90°-tagged test image and *looking at the output*. It came
out 180° rotated, not 90°. Confirmed by measuring `CanvasBitmap`'s reported `Size` against a
known-tagged file — the decoder had already swapped width and height before I touched it.

The fix is to decode through `BitmapDecoder.GetSoftwareBitmapAsync` with
`ExifOrientationMode.IgnoreExifOrientation`, forced to `Bgra8`/`Premultiplied` for Win2D interop,
so that the explicit rotation value stays the single source of truth:

```csharp
using SoftwareBitmap raw = await decoder.GetSoftwareBitmapAsync(
    BitmapPixelFormat.Bgra8,
    BitmapAlphaMode.Premultiplied,
    new BitmapTransform(),
    ExifOrientationMode.IgnoreExifOrientation,
    ColorManagementMode.DoNotColorManage);
return CanvasBitmap.CreateFromSoftwareBitmap(resourceCreator, raw);
```

Twelve unit tests passed throughout, because not one of them decoded a real file.

That's the second time this project taught me the same lesson — the first was the deferral
deadlock above — and it's worth stating plainly: **for a render pipeline, `dotnet test` passing
is necessary and nowhere near sufficient. Render real output and look at it.**

## The short version

- `MediaComposition` cannot produce a transition. The clip model has no overlap,
  `IBasicVideoEffect` sees one stream, and `MediaOverlay.Opacity` is a `double`.
- One crossfade is enough to require owning the frame pipeline. There's no partial path.
- Win2D → `MediaStreamSource` → `MediaTranscoder` gets you there with one MIT dependency and the
  same OS encoder. `CanvasRenderTarget` casts straight to `IDirect3DSurface`.
- Make your frame function pure in `t` and preview-versus-export drift stops being a bug class.
- Take the render size from the encoding profile, check `CanTranscode`, release the deferral on
  every path, and don't dispose the render target.
- Decode with EXIF orientation explicitly ignored, and look at your output.

---

`[TO DO before publishing]`
- Add the Windows version and build this was developed and tested against
- Decide whether to link the apps. Suggested: one line at the end, not in the opening — the post
  is stronger as a finding than as a product story, and it'll carry the apps further that way
- Consider whether the `FrameComposer`/`SlideTimeline` split is worth a short section; it's the
  part a reader would need to rebuild this, but it may be one section too many
