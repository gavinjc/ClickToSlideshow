# Current Application

## Overview
**Released.** v1.0.0 passed WACK, was submitted on 2026-09-26 (M8), and is live in the Microsoft
Store (Store ID `9PLKC14MGC3N`; full identity in `docs/store/submission-checklist.md` §2.2). The
milestone history below is how it got there.

M1 (core render engine, `ClickToSlideshow.Cli`), M2 (WinUI 3 editor shell,
`ClickToSlideshow.App`), M3 (fullscreen playback, same `.App` project), M4 (save flows: MP4
and project JSON), M5 (PPTX writer), M6 (shell extension), and M7 (MSIX packaging) are all
built — see `docs/clickto-slideshow-spec.md` and `docs/clickto-slideshow-decisions.md` for the
full design. M5 is built and **confirmed against real PowerPoint** (2026-09-03) — see Known
Issues for two real bugs that verification caught (neither would have been caught by
`OpenXmlValidator` or the round-trip test alone); its audio and transition-duration gaps were
fixed 2026-09-19 (see the M5 entry). M6 is built and
**verified end-to-end** (2026-09-04) via direct COM instantiation of the registered
`IExplorerCommand` (`GetTitle`, `GetState`, `Invoke`), confirming the real launch pipeline
through to the app loading the file. M7 is built and **verified via a real MSIX deploy**
(2026-09-04) — the packaged app was registered and launched through its actual package
identity (`shell:AppsFolder`/AUMID activation), the gap M6 flagged as unverified.

## Architecture
All five planned projects exist: `ClickToSlideshow.Core` (timeline model, frame composer,
transitions, MP4 renderer, audio muxer, PPTX writer/reader, live-playback support),
`ClickToSlideshow.Cli` (headless render, no UI), `ClickToSlideshow.App` (WinUI 3 editor shell +
fullscreen player), `ClickToSlideshow.ShellExtension` (native C++/WRL `IExplorerCommand`), and
`ClickToSlideshow.Package` (wapproj → MSIX), plus `ClickToSlideshow.Core.Tests` and
`ClickToSlideshow.App.Tests` (xunit). `.App` is ported from `ClickToPDF.App`'s
`MainWindow`/`MainViewModel`
(CommunityToolkit.Mvvm, unpackaged `WindowsPackageType=None`, same `WindowsAppSDKSelfContained=false`
pairing as `.Cli`/M0), with Slide/AudioTrack-specific pieces (duration badges, transition
glyphs, the audio strip, the runtime bar) added on top, plus `PlayerWindow`/`PlayerViewModel`
(M3) as a second window — the one sanctioned exception to "no second window" (spec §2/§4.7).

## Key Files
- `src/ClickToSlideshow.Core/Rendering/FrameComposer.cs` — pure per-frame render function
  shared by export and (future) preview.
- `src/ClickToSlideshow.Core/Rendering/Transitions/` — `Cut`/`Crossfade`/`FadeThroughBlack`/
  `Push{Left,Right,Up,Down}`, all pixel-verified against synthetic test images.
- `src/ClickToSlideshow.Core/Imaging/SlideImageLoader.cs` — full decode for the renderer;
  deliberately bypasses Win2D/WIC's own EXIF auto-rotation (see Known Issues) so
  `Slide.RotationDegrees` stays the single source of truth for rotation.
- `src/ClickToSlideshow.Core/Imaging/SlideImageInspector.cs` — lightweight header-only
  inspection and thumbnail extraction for the grid (embedded-thumbnail-first, falling back to a
  scaled decode), ported from `ClickToPDF.Core`'s `ImageInspector`.
- `src/ClickToSlideshow.Core/Audio/OggAudioTranscoder.cs` — managed Ogg Vorbis/Opus decode to a
  cached temp WAV (Windows has no decoder for them); `AudioTrack.PlayablePath` is what every
  audio consumer opens, `SourcePath` is what gets saved.
- `src/ClickToSlideshow.Core/Audio/SoundtrackRenderer.cs` — mixes all tracks into one show-length
  MP3 for the PPTX writer via `FadedSoundtrackRenderer` (below), reusing the MP4 muxer's
  Loop/Trim/FitSlides sequencing; `CompositionFileRenderer` is the shared render-and-check-failure
  step.
- `RenderProgress` (Core) — phase-tagged progress (`Frames` then `Audio`, each 0-1) driving the
  save bar's "Rendering slides… / Adding music…" label; the audio re-encode after the frame pass
  is the long silent stretch it exists to cover.
- `src/ClickToSlideshow.Core/Video/Mp4VideoRenderer.cs` — Win2D → `MediaStreamSource` →
  `MediaTranscoder`, the M0-proven pipeline promoted to production code.
- `src/ClickToSlideshow.Core/Video/Mp4Profiles.cs` / `OriginalFrameSize.cs` (1.0.2) — the one
  place the output size is chosen, for the video pass and the PPTX slide size (the audio mux pass
  copies the video pass's size). Original resolution threw until 1.0.2; its rule (largest slide by
  area after rotation, capped at H.264 level 5.2) and the encoder limits behind it are in the
  decisions doc, "Original resolution".
- `src/ClickToSlideshow.Core/Audio/MediaCompositionAudioMuxer.cs` — second-pass audio mux
  (Loop/Trim/FitSlides per spec §4.2); attaches one already-faded soundtrack track produced by
  `FadedSoundtrackRenderer` rather than fading itself (see that class's own doc comment for why: a
  custom `IBasicAudioEffect` needs its own Windows Runtime Component project, whose activation
  under this app's unpackaged dev-launch path was never confirmed, so the fade is instead a plain
  gain ramp — `AudioFadeOut` — applied to raw PCM once sequencing has already happened).
- `src/ClickToSlideshow.Core/Audio/FadedSoundtrackRenderer.cs` (2026-09-22) — sequences a
  project's tracks to a plain PCM WAV (reusing `MediaCompositionAudioMuxer.AddBackgroundAudioAsync`
  unchanged), then bakes spec §4.2's fade-out into that WAV directly via `AudioFadeOut.ApplyInPlace`
  before either save path (MP4 mux, PPTX soundtrack) ever sees it — one implementation, both
  formats. `AudioFadeOut` is a pure, unit-tested linear gain ramp over the final 2s, applied
  unconditionally regardless of `AudioMatch` (mirrors `PlayerViewModel.UpdateFadeVolume`'s own
  reasoning: ramping down samples that are already silence is inaudible, so no branching needed).
  `WavPcm` is the shared RIFF/WAVE header reader+writer behind all three of this, `OggAudioTranscoder`
  and `AudioPeakExtractor` — one implementation instead of three near-identical ones.
- `src/ClickToSlideshow.Core/SlideshowRenderer.cs` — `EffectiveProject(project)`: always returns
  an independent clone (FitSlides-scaled if applicable), shared by the MP4 exporter and the
  player so both compute `AudioMatch` identically and neither can be corrupted by a still-open
  editor mutating its live `SlideshowProject` mid-render/mid-playback. `Clone(project)` (M4) is
  the same always-clone guarantee *without* FitSlides scaling, for project-JSON save — a saved
  project must round-trip the user's own stored durations exactly (spec §4.2).
- `src/ClickToSlideshow.Core/Serialization/SlideshowProjectFile.cs` (M4) — project-JSON
  read/write (spec §4.8). Source paths are stored relative to the project file's own directory
  where possible (`Path.GetRelativePath`, which already falls back to absolute across different
  roots with no extra branching needed); `BackgroundColor` maps to a plain hex string since
  `System.Text.Json` has no useful default representation for the WinRT `Color` struct. Rejects
  loading a `FormatVersion` newer than `SlideshowProject.CurrentFormatVersion`. Per-slide
  missing-source detection is deliberately **not** done here — `MainViewModel.LoadProjectAsync`
  reuses the existing per-tile decode-failure pipeline for that instead (see below).
- `src/ClickToSlideshow.Core/Playback/AudioSequencer.cs` — pure function (M3): given
  `AudioMatch` and which track just ended, decides the next track index to play or `null`
  (stop/silence). Live-triggered mirror of the muxer's Loop/Trim sequencing, independently
  testable without a `MediaPlayer`.
- `src/ClickToSlideshow.Core/Pptx/PptxSlideGeometry.cs` (M5) — pure function computing a
  picture shape's off/ext/rotation/crop on a PPTX slide, mirroring `SlideTransform.Compute`'s
  Fit/Fill scale rule exactly so a PPTX slide and a Win2D-rendered frame show the same
  crop/letterbox. No OpenXml/WinRT dependency, so it's the one piece of M5 fully unit-tested
  with precise numeric assertions rather than schema/round-trip checks.
- `src/ClickToSlideshow.Core/Pptx/PptxSlideshowWriter.cs` (M5) — writes a `SlideshowProject` to
  PPTX (spec §4.8): full-bleed picture + background-colour rectangle per slide (using
  `PptxSlideGeometry`), the §4.6 transition mapping (`<p:transition>`, with the effect duration
  as a `p14:dur` extension element — not the SDK's own `Transition.Duration` property, which
  serializes as a bare attribute `OpenXmlValidator` rejects, found by running it, not assumed),
  a single background audio track spanning the show via a `<p:timing>` tree built from
  PowerPoint's own "play across slides" shape, per-slide notes (filename + Date Taken), and the
  project JSON embedded as a custom XML part. Images passthrough their original bytes for
  formats PowerPoint's image part supports (jpg/png/gif/bmp/tif); heic/webp/avif (which this app
  otherwise accepts, per M2's Known Issues) are decoded via `SlideImageLoader` and re-encoded to
  PNG first. Uses `SlideshowRenderer.EffectiveProject`, not `Clone` — PPTX is a rendering
  consumer like the MP4 exporter, not like JSON.
- `src/ClickToSlideshow.Core/Pptx/PptxProjectReader.cs` (M5) — the read half of the round-trip:
  extracts the embedded custom XML part and hands it to `SlideshowProjectFile.FromJson`, the
  exact DTO mapping the JSON loader uses (both loaders now go through `SlideshowProjectFile.ToJson`/
  `FromJson`, with `Save`/`Load` as thin file-I/O wrappers). The PPTX writer passes
  `Path.GetFileName` as the path mapping, so a deck stores bare filenames, not relative paths
  (a shared deck must not disclose folder names or the user name); on reopen they resolve against
  the deck's folder, so sources beside the .pptx are found and the rest are reported missing.
- `src/ClickToSlideshow.Cli/Program.cs` — CLI entry point (`--list`/`--audio`/`--output`/etc.).
- `src/ClickToSlideshow.App/ViewModels/MainViewModel.cs` — grid/audio-strip state: load (folder
  path or `--list`/`--skipped`), sort, drag-reorder, rotate, remove, undo (20-deep), audio-track
  management, and the project-settings proxies (default duration, transition, resolution, fit,
  background colour, `AudioMatch`) that drive the runtime bar. `LoadProjectAsync` (M4; extended
  M5 for `.pptx`) rebuilds the grid/audio-strip/options from a loaded `SlideshowProject` —
  modeled directly on the existing folder-load path, including reusing `LoadSlideAsync`'s
  per-tile error state for a missing image source, so "report missing sources per-slide" (spec
  §4.8) needed no new UI. Dispatches to `PptxProjectReader` or `SlideshowProjectFile.Load` by
  extension; `RenderPptxAsync` (M5) is the Save-menu counterpart to `RenderVideoAsync`.
  From 1.0.2 it reports to the shared message bar through `MessageShown`/`MessageClosed`
  (rows keyed by `MessageKeys`; `ShowError` also holds back the rating prompt), and the + buttons
  and an outside drop share one `AppendAsync` (one undo step, appended after what is loaded).
- `src/ClickToSlideshow.App/ViewModels/SupportedFiles.cs` (1.0.2) — the photo and music extension
  lists, moved out of `MainViewModel` so `ExpandedSelection` doesn't depend on the view model.
  Its static `ResourceLoader` is unreliable in the test host: its construction failed on first
  touch from one test class while others built a `MainViewModel` fine, and `GetString` always
  fails there. `SupportedFilesTests` keeps it equal to the shell extension's
  `SupportedExtensions.h`.
- `src/ClickToSlideshow.App/ViewModels/ExpandedSelection.cs` (1.0.2) — what a drop from File
  Explorer comes to: the shell extension's folder expansion rules (`Invoke`/`ExpandFolder`)
  ported to C#, so a drop and the right-click menu add the same things.
- `src/ClickToSlideshow.App/ViewModels/SkippedFilesText.cs` (1.0.2) — the family's skipped-row
  wording, a resw string for one file and another for many.
- `src/ClickToSlideshow.App/State/SaveFileNaming.cs` (M4) — default output filename
  (`Slideshow_yyyy-MM-dd{extension}`) and `" (2)"`/`" (3)"` collision resolution, ported from
  `ClickToPDF.App`'s file of the same name (its own doc comment explains why a date stamp beats a
  source-derived name: it avoids the Save dialog's own folder-name-autocomplete collision).
- `src/ClickToSlideshow.App/ViewModels/SlideItem.cs` — wraps one `Slide`; domain fields
  (duration/rotation/transition override) write through to the underlying `Slide`, UI-only
  fields (thumbnail, loading/error state, selection) stay local. The tile's chrome pill has a
  "Set transition into this slide" menu (`TileTransition_Click`) that sets or clears the
  override; the tile's filename shows the full name in a tooltip when trimmed.
- `src/ClickToSlideshow.App/MainWindow.xaml(.cs)` — the single editor window: toolbar, options
  expander, slide `GridView`, audio strip, runtime bar, preview overlay. From 1.0.2 the content
  row (`ContentGrid`) takes files and folders dropped from File Explorer (handlers attached with
  `handledEventsToo`, acting only on drags that carry storage items, so the lists' own reorder
  is untouched), and the shared `MessageBar` floats with the save progress row at its bottom.
  Video and PowerPoint saves share `SaveWithProgressAsync`.
- `src/ClickToSlideshow.App/App.xaml.cs` — unpackaged launch: `--list`/`--skipped` (the M6
  shell-extension contract, now real) or a positional folder path (the pre-M6 dev-launch path,
  still used directly by `dotnet run`).
- `src/ClickToSlideshow.App/ViewModels/PlayerViewModel.cs` (M3) — owns the fullscreen player's
  wall clock (a manually seekable, pausable `Stopwatch` — deliberately not driven off
  `MediaPlayer.PlaybackSession.Position`, since audio must track the frame clock per spec §4.7,
  not the reverse), the non-blocking decode-ahead bitmap cache (mirrors
  `Mp4VideoRenderer`'s current-and-next-only rule), and the `MediaPlayer`/`AudioSequencer`
  wiring for multi-track Loop/Trim playback plus the live fade-out. ←/→ skip reseeks the audio to
  the equivalent point via `AudioSequencer.Locate` (2026-09-23), not just the video clock. A
  user-set `Volume` (bound from the player's own volume slider) multiplies against the same
  fade-out factor rather than fighting it.
- `src/ClickToSlideshow.App/PlayerWindow.xaml(.cs)` (M3) — the fullscreen player window.
  **Not** built on Win2D's own `CanvasAnimatedControl`/`CanvasControl` XAML controls — confirmed
  by trying, not assumed, that `Microsoft.Graphics.Canvas.UI.Xaml` types are unusable in this
  unpackaged WinUI3 app with the pinned Win2D 1.2.0 package (the type names exist in the
  package's WinRT metadata, but neither the XAML nor C# compiler can resolve them outside UWP).
  Renders instead into an offscreen `CanvasRenderTarget` (the same surface type
  `Mp4VideoRenderer` already uses) and presents each frame as a `WriteableBitmap` on a plain
  `Image`, driven by a `DispatcherTimer` throttled to the spec's 30fps target rather than
  uncapped `CompositionTarget.Rendering` — see Known Issues for the real-hardware performance
  numbers this was measured against (comfortably passes).
- `src/ClickToSlideshow.ShellExtension/ExplorerCommand.cpp` (M6) — `IExplorerCommand` (WRL,
  ported ~95% from `ClickToPDF.ShellExtension` per the decisions doc's reuse table): fixed title
  "Create slideshow" for any selection count (spec §4.1, no per-count variant), `GetState`
  gated on `SupportedExtensions.h`'s combined image+audio table (kept in lockstep with
  `SupportedFiles.ImageExtensions`/`AudioExtensions` — the extension never
  classifies image-vs-audio itself, only visibility; the App owns classification, per spec
  §4.1's own reasoning against duplicating the audio table into C++), non-recursive folder
  expansion, and the same `%LOCALAPPDATA%\Temp\ClickToSlideshow\{guid}.list`/`.skipped`
  temp-file IPC (never paths on the command line — 300-photo selections are the normal case).
  New CLSID (`{C9E2689D-69D4-4A4F-85B0-9F7CFA6F3726}`), registered per-user under
  `AllFilesystemObjects\shell\ClickToSlideshow` (HKCU, `regsvr32`, no admin — dev/test only)
  or, once packaged, via M7's MSIX manifest `windows.comServer`/`windows.fileExplorerContextMenus`
  extensions instead (no registry writes at all).
- `src/ClickToSlideshow.Package/Package.appxmanifest` (M7) — declares the packaged identity,
  the Start-tile/taskbar image set, the `windows.comServer` extension resolving the shell
  extension's CLSID to `ClickToSlideshow.ShellExtension.dll` via a surrogate host (no registry
  writes), and `windows.fileExplorerContextMenus` binding that same CLSID to both `Type="*"`
  and `Type="Directory"` selections. `Identity Name`/`Publisher` are the real Partner Center
  Product identity values (confirmed 2026-09-26); `Publisher` is per account, so it is the same
  as `ClickToPDF.Package`'s. `tools/generate-icons.py` (ported from `ClickToPDF`'s script, geometry kept in lockstep
  with `docs/icon-fanned-frames_1.svg`) generates the full Images/ asset set from one vector
  definition: a fanned stack of three landscape frames on a teal (`#0F6E63`) tile, the front
  frame carrying an amber (`#F2A03D`) foot band with a white play-triangle knockout.

## External Dependencies
`Microsoft.Graphics.Win2D` 1.2.0 (frame compositing), `Microsoft.WindowsAppSDK` 2.3.1 (pinned —
see Known Issues), `CommunityToolkit.Mvvm` 8.4.2 (`.App` only — `ObservableObject`/
`[ObservableProperty]`, no `RelayCommand`/DI container, matching `ClickToPDF.App`'s usage).
`DocumentFormat.OpenXml` 3.5.1 (PPTX read/write, `.Core` only — pinned to the resolved version;
3.5.0 wasn't available on nuget.org at the time this was added).
Bureau.Common (git submodule at `external/Bureau.Common`, from 1.0.1): shared About dialog,
cross-promo panel, Store rating prompt (offered once, after a clean MP4 or PPTX save),
title-bar theming, surface palette and `IconButtonStyle`. From 1.0.2 also the Help window, its
? button and F1 (editor only; topics are this app's resw strings, listed in `HelpTopicKeys`).

## Known Issues / Constraints

- **M0 (Win2D feasibility spike) passed (2026-09-01).** Win2D `CanvasRenderTarget` →
  `MediaStreamSample` → `MediaTranscoder` → MP4 confirmed working, both unpackaged and via real
  MSIX activation — see `docs/clickto-slideshow-decisions.md`'s "Next step" entry for the
  implementation notes carried forward to M1. Spike code was thrown away per spec §6.
- **M1 (core render engine) built and fully verified (2026-09-02).** All transition kinds
  (Cut/Crossfade/FadeThroughBlack/all four Push directions), UHD2160p, FitMode.Fill,
  audio muxing (both Loop-to-fill and Trim/truncate paths), EXIF-rotated photos, and
  performance/memory at 200-photo scale were each confirmed against actual render output
  (pixel-sampled or measured), not just `dotnet build`/`dotnet test` passing.
- **Win2D/WIC auto-apply EXIF orientation on decode — the opposite of what
  `SlideImageLoader.cs`'s original doc comment assumed.** Both `CanvasBitmap.LoadAsync` and
  `BitmapDecoder.GetSoftwareBitmapAsync()`'s default `ExifOrientationMode` silently pre-rotate
  pixel data to match the file's EXIF tag. Combined with the pipeline's own explicit
  `Slide.RotationDegrees` transform (detected separately via
  `SlideImageLoader.DetectExifRotationDegreesAsync` and applied by `FrameComposer`), this
  double-rotated every EXIF-tagged photo — confirmed by rendering a 90°-tagged synthetic test
  image and finding the output content rotated 180° net, not 90°. Fixed by decoding through
  `GetSoftwareBitmapAsync(..., ExifOrientationMode.IgnoreExifOrientation, ...)` in
  `SlideImageLoader.LoadBitmapAsync`, forced to `Bgra8`/`Premultiplied` for Win2D interop.
  Regression-covered by `SlideImageLoaderTests.LoadBitmapAsync_ExifTaggedPortraitPhoto_ReturnsRawUnrotatedDimensions`
  (confirmed to fail against the old code before the fix, via `git stash`). All 12 prior unit
  tests never exercised this path (none decode a real file), matching the earlier semaphore-
  deadlock lesson: `dotnet test` passing is necessary but not sufficient for this pipeline —
  render real output and inspect it.
- A real phone-camera photo with genuine (non-synthetic) EXIF data and multi-track audio
  playlists, both unexercised after the M1 pass, **passed by hand on 2026-09-26**
  (`docs/store/submission-checklist.md` §8). (The audio fade-out gap this bullet used to flag is
  closed — see `FadedSoundtrackRenderer` above and the dated entry under M4/M5 below.)
- **M2 (WinUI 3 editor shell) built and verified (2026-09-02).** Folder-path launch and
  `--list`/`--skipped` launch, thumbnail loading, the slide grid (numbering, duration badges,
  transition glyphs, drag-reorder), the audio strip, the runtime bar's proportional widths, the
  `AudioMatch` control, and the skipped-files banner were each confirmed against the actual
  running window, not just `dotnet build`/`dotnet test` passing. Two deliberate scope cuts, both
  user-confirmed:
  - **No Play/Save UI in M2.** Play is M3, Save (MP4/PPTX/JSON) is M4 — each independently
    runnable per spec §6. `MainViewModel` has no `SaveAsync`; that lands with M4's save-progress
    UI (reusing `Img2PDF.App`'s existing pattern, per the decisions doc's reuse table).
  - **Audio strip supports `.mp3`/`.wav`/`.flac`/`.m4a`/`.aac`/`.wma`** (2026-09-19: extended
    from the original `.mp3`/`.wav`/`.flac`-only tier — spec §4.4's claim that `.m4a`/`.aac`/`.wma`
    need a `MediaTranscoder` transcode-on-import pass before `MediaCompositionAudioMuxer` would
    accept them was verified false against real files; `BackgroundAudioTrack.CreateFromFileAsync`
    already accepts all three directly via Media Foundation's in-box AAC/WMA decoders).
    **`.ogg`/`.opus` are supported (2026-09-19)** but do need a decode: Media Foundation has no
    Ogg/Vorbis/Opus decoder (`BackgroundAudioTrack` and `MediaTranscoder` both fail, even with
    Microsoft's Web Media Extensions installed, so a free Store extension is not an option here).
    `OggAudioTranscoder` decodes them in managed code (NVorbis for Vorbis, Concentus.OggFile for
    Opus — both small, pure C#, MIT/BSD-3, credited in the About box) to a cached temp WAV, and
    `AudioTrack.PlayablePath` points every consumer at it while `SourcePath` (what projects and
    PPTX metadata store) stays the user's file. Verified end to end: a 440 Hz Vorbis and a 550 Hz
    Opus tone each rendered through the CLI into an MP4 came out at the right pitch and length,
    and a PPTX built from the .ogg embeds a 440 Hz soundtrack. An undecodable Ogg file is skipped
    and reported rather than failing the load. Tested through the WinUI picker by hand on
    2026-09-26 (`submission-checklist.md` §8).
  - **Real bug found and fixed while verifying the above (2026-09-19):** `AudioPeakExtractor`'s
    waveform generation was silently broken for *every* compressed source format, `.mp3` included
    — not a new-format gap at all. `MediaEncodingProfile.CreateWav(AudioEncodingQuality.Auto)`
    fails `MediaTranscoder.PrepareFileTranscodeAsync` (`CanTranscode=false`, `FailureReason=Unknown`)
    whenever the source isn't already WAV, because WAV has no inherent quality tier for `Auto` to
    match against a lossy input. The code never checked `CanTranscode` before calling
    `TranscodeAsync()`, which is what turned a clean "unsupported" signal into an opaque
    `NullReferenceException` several frames deep in the WinRT projection layer instead of a
    logged failure reason. Fixed by using a fixed `AudioEncodingQuality.High` and checking
    `CanTranscode` first. Confirmed via the real running app's own log (zero warnings) and a
    screenshot showing real peak data, not the flat placeholder, for all six formats.
  - **Camera RAW is supported (2026-09-19)** via Microsoft's free "Raw Image Extension" (no
    bundled decoder). Spec §5's prediction that RAW would break the thumbnail budget did not hold:
    against real Canon CR2, Nikon NEF and Sony ARW samples the embedded preview loads in 16-41 ms
    and a full 24 MP decode takes 0.4-1.0 s, so RAW shares the JPEG concurrency limit. Extensions
    live in `RawImageExtensions` (and `SupportedExtensions.h`); a missing extension takes the same
    per-tile "install this from the Store" path as HEIC (`CodecFixLinks`, `RawCodecMissingMessage`).
    Not tested: the extension-missing case itself (it is installed on the dev machine), or
    manufacturers beyond those three. A 1 s decode also means slides shorter than ~1 s could hold
    the previous frame in the player until the next one is ready.
  - HEIC/HEIF/WEBP/AVIF *are* included in the supported list — their codec-missing path is
    exercised by the ported `CodecFixLinks`/`DescribeLoadError` machinery, so pre-filtering them
    would have made that machinery dead code rather than actually covering the case spec §4.4
    describes.
  - **Interactive behaviour click-tested end to end (2026-09-22), via UI Automation against the
    real running app** (`SelectionItemPattern`/`RangeValuePattern`/`ExpandCollapsePattern`
    queried directly rather than trusting screenshots alone, after a pixel misread during this
    same pass falsely suggested a selection bug that a property query then ruled out). Confirmed
    working: the rotate/remove/undo tile buttons and their undo-stack integration; the sort
    menu's all four orderings plus renumbering; every options-expander control (`DefaultSlideMs`/
    `TransitionMs` `NumberBox`es via `RangeValuePattern`, the four `ComboBox`es, the ten
    background-colour swatches, and the custom-colour `ColorPicker` ring, all recalculating the
    runtime total correctly); keyboard `R` (rotate), `Delete` (remove), `Ctrl+Z` (undo), and
    `Ctrl+A` (select all); and the preview overlay's open/close (double-click, the preview
    button, the close button, click-outside, Escape).
  - **Two real, previously-invisible bugs found and fixed this pass:** `Ctrl+Left`/`Right`
    (`MoveSelected`, spec's keyboard slide-reorder) and `Left`/`Right`-to-step while the preview
    overlay is open (`StepPreviewAsync`) were both dead code. `SlidesGridView`'s own built-in
    keyboard handling claims every arrow key — Ctrl or not — for its standard
    ListViewBase/GridView "move focus without changing selection" accessibility convention, and
    marks the event `Handled` before a plain XAML `KeyDown="..."` bubble handler ever runs;
    `SlidesGridView_KeyDown`'s cases for those keys were simply never reached. Confirmed via
    `AutomationElement.FocusedElement`: focus visibly advanced to the next tile while selection
    and the preview index stayed put. Only non-arrow shortcuts (`R`, `Delete`, `Ctrl+Z`, `Ctrl+A`)
    were ever actually reachable. Fixed in `MainWindow.xaml.cs` by registering the same handler
    via `SlidesGridView.AddHandler(UIElement.KeyDownEvent, ..., handledEventsToo: true)` instead
    of the XAML attribute, so it now runs regardless of GridView's own default handling; re-ran
    both scenarios afterward and confirmed the reorder and the preview step now actually happen.
  - **Drag-reorder itself (mouse) is confirmed working by hand (user, 2026-09-22)**, though this
    pass's own UI Automation couldn't drive it: neither `mouse_event` nor `SendInput`-based
    synthetic drags (with an initial jiggle to cross the drag-start threshold) triggered
    `GridView`'s `DragItemsStarting`/reorder — no drag visual ever appeared, and the
    `GridViewItem` exposes no UIA `DragPattern` to drive directly. That's a tooling limitation,
    not a real gap: WinUI3's DirectManipulation-driven drag gesture recognizer is a known hard
    case for synthetic input in this environment (same class of issue as the already-documented
    Explorer context-menu automation limitation, M6 above), and a real mouse exercises it fine.
    **Update 2026-11-02:** injected *touch* input does drive it. `InjectTouchInput` (one contact,
    held still for about a second, then moved to the target tile and lifted) reordered the grid
    and renumbered the tiles. A drop from outside is driven by a small WinForms window calling
    `DoDragDrop` with a `FileDrop` `DataObject` while another thread moves the cursor and releases
    the button; the drop target sees what File Explorer would give it.
    The underlying mechanism — `OnSlidesCollectionChanged` treating a
    `NotifyCollectionChangedAction.Move` from the bound `ObservableCollection` as one undo step
    and renumbering — is the same `Slides.Move` + undo + renumber path the keyboard-reorder fix
    above exercised directly.
  - **Minor, unfixed nuance noticed while verifying the keyboard-reorder fix:** after
    `Ctrl+Left`/`Right` moves the selected tile, its selection highlight clears rather than
    following the tile to its new slot (keyboard focus moves there instead, per GridView's own
    convention). Cosmetic only — the reorder, renumbering, and undo are all correct — and left
    alone rather than folded into the keyboard-routing fix above.
- **M3 (fullscreen playback) built and verified (2026-09-02).** Unlike M2, this one *was*
  interactively exercised — launch, fullscreen, real frame rendering with correct `FitMode`
  letterboxing, wall-clock slide progression, Space (play/pause, including the chrome
  play/pause icon updating), the 3s chrome auto-hide and its reappearance on input, ←
  (previous-slide, including correct clamping at the first slide), and Esc (clean close —
  confirmed no leftover `MediaPlayer`/window after exit) were each confirmed against the
  actual running window, not just `dotnet build`/`dotnet test` passing.
  - **`CanvasAnimatedControl`/`CanvasControl` don't work here — see the `PlayerWindow.xaml(.cs)`
    Key Files entry above.** This was discovered empirically mid-implementation (the plan's
    original design assumed one of the two would work), not anticipated in advance. The
    fallback (offscreen `CanvasRenderTarget` → `WriteableBitmap`) works and was verified
    end-to-end.
  - **Real-hardware performance against the spec §5 "sustained 30fps at 1080p" target measured
    and passed comfortably (2026-09-22)**, closing the gap above (the M0-era verification only
    used an 800×600 test image). Measured by instrumenting `RenderTimer_Tick` with a `Stopwatch`
    around the whole per-frame cost (`RenderFrame` draw calls plus `GetPixelBytes()`'s CPU↔GPU
    readback plus the `WriteableBitmap` copy) and driving 20s of real fullscreen playback via UI
    Automation, then removing the instrumentation once the numbers were captured. Result, at this
    machine's native **2560×1600** — chosen deliberately over windowing down to exactly 1080p,
    since it's ~2x the pixel count and so a strictly harder test that a 1080p pass follows from:
    1,338 frames captured, average **7.8ms**, max **14.9ms**, P99 **10.5ms**, against the 33.3ms
    (30fps) budget — **zero frames exceeded budget**. The readback cost this milestone worried
    about is real but small relative to the budget; the documented `CanvasSwapChain` fallback is
    not needed.
  - **Two real bugs were caught only by running the app, not by `dotnet build`/`dotnet test`**
    (both now fixed): (1) `PlayerWindow` originally called `AppWindow.SetPresenter(FullScreen)`
    in its constructor *before* the window had ever been shown, leaving it fullscreen-configured
    but genuinely invisible — fixed by calling `Activate()` first. (2)
    `DisplayArea.FindAll().FirstOrDefault(d => !d.IsPrimary)` throws `InvalidCastException` from
    deep inside the CsWinRT-generated `IEnumerable<DisplayArea>` projection (confirmed via a live
    crash log, not guessed) — exactly the category of CsWinRT issue the M0 spike's own notes
    flagged as a risk. Fixed by replacing the LINQ call with a plain indexed loop (indexer/Count
    access on the same WinRT list work fine; only its `IEnumerable<T>` projection is broken).
    Both bugs were silent: the app's global `UnhandledException` handler (by design, to protect
    an in-progress editing session — see `App.xaml.cs`) caught and logged them without crashing,
    so the player window simply failed to work correctly with no visible error dialog.
  - **Known, deliberately out-of-scope for this pass** (confirmed with the user before
    implementation): the hover scrub bar is display-only, no click/drag-to-seek. ←/→ originally
    moved only the video clock; since 2026-09-23 they also reseek the audio (see
    `PlayerViewModel` under Key Files). F (fullscreen toggle) and → (next-slide) were
    code-reviewed and share code paths already exercised (the same `SetPresenter` call; the same
    `SeekToSlide` as ←) but weren't independently screenshot-confirmed this pass. Multi-monitor
    default-to-non-primary logic is code-reviewed only — this dev machine has a single display.
- **M4 (save flows: MP4 and project JSON) built and verified (2026-09-02).** Interactively
  exercised end-to-end: Save → Video (MP4) (picker with the correct default name/type, progress
  bar advancing, a real playable 3840×2160 H.264/AAC output confirmed via `ffprobe`), Save →
  Project (JSON) (picker, then the written file inspected directly — correct relative source
  paths, human-readable enum strings, every option field present), Open (full grid/audio-strip/
  options reload from that same file, including all four previously un-bound `ComboBox`es —
  `TransitionKind`/`Resolution`/`FitMode`/`AudioMatch` — correctly re-selecting the saved value),
  and the missing-source path (moved the source image and audio file on disk between save and
  reopen: the image tile showed the new "file not found" message via its existing per-tile error
  flyout, the audio track was silently dropped and reported through the skipped-files banner,
  and the rest of the project loaded normally rather than refusing outright, per spec §4.8).
  - **Cancellation and partial-output cleanup were verified directly against
    `SlideshowRenderer.RenderAsync`** (a token cancelled 50ms in) rather than by winning a race
    against the on-screen Cancel button — the 3-slide/14-second test show transcodes fast enough
    at hardware-accelerated speed that reliably clicking Cancel mid-export through UI Automation
    before it finished wasn't achievable. Confirmed: `OperationCanceledException` propagates, the
    output path is never left behind, and (on an uncancelled run) `IProgress<double>` reports 421
    monotonically increasing values ending at 1.0 for that same show. The Cancel *button's*
    on-screen click was code-reviewed (`_saveCts?.Cancel()`) but not independently exercised.
  - **Unsaved-changes warning on Open, added 2026-09-22.** `OpenButton_Click` now confirms
    (`DiscardChangesDialog`, a plain ContentDialog) before loading another project whenever the
    grid or audio strip already holds anything. Deliberately gated on "is there something to
    lose", not precise dirty-tracking against the undo stack — a per-slide duration edit
    (`MainWindow.xaml.cs`'s `NumberBox` handler) writes straight to `Slide.DurationMs` without
    ever pushing an undo entry, so tracking dirty state off the undo stack would have missed it
    and silently under-warned. The coarser check can only ever over-warn (e.g. reopening the same
    project you just loaded), never under-warn. Verified against the real running app via UI
    Automation: the dialog appears with the loaded slide still visible behind it, Cancel leaves
    the project untouched, and "Open anyway" proceeds to the real file picker.
  - **Audio fade-out over the final 2s implemented for both MP4 and PPTX (2026-09-22)** — the
    gap M1/M4's own doc comments had flagged and left unimplemented (a custom
    `Windows.Media.Effects.IBasicAudioEffect` was rejected: it needs its own Windows Runtime
    Component project, and confirming that class's activation would work under this app's
    unpackaged dev-launch path, not just a packaged MSIX install, was real, unbounded risk).
    Implemented instead as `FadedSoundtrackRenderer`: render the already-correct
    Loop/Trim/FitSlides-sequenced tracks to a plain PCM WAV, apply `AudioFadeOut`'s linear gain
    ramp directly to the raw samples, then hand that one already-faded WAV to the MP4 muxer or
    the PPTX soundtrack encoder — no custom effect, no extra project, no packaging dependency.
    Applied unconditionally regardless of `AudioMatch`, mirroring
    `PlayerViewModel.UpdateFadeVolume`'s existing live-player reasoning: ramping down samples
    that are already silence (Trim's shorter-than-show case) is inaudible, so no branching is
    needed. Verified against real rendered output, not just `dotnet test`: a 3-slide/10s show
    with a constant-amplitude 10s test tone, rendered through the real Cli and through
    `PptxSlideshowWriter`, then measured with `AudioPeakExtractor` on the finished MP4 and on
    the MP3 extracted from the finished PPTX — both show flat amplitude for the first 8s and a
    clean linear decay to near-silence over the final 2s. `AudioFadeOut`'s own gain-ramp math is
    additionally unit-tested with exact expected values. `WavPcm` (new) is a shared RIFF/WAVE
    header reader+writer factored out of what were previously three separate near-identical
    implementations (`OggAudioTranscoder`, `AudioPeakExtractor`, and this).
- **M5 (PPTX writer) built and confirmed in real PowerPoint (2026-09-03).** `OpenXmlValidator`
  and a round-trip test were necessary but nowhere near sufficient — the M1/M3 lesson ("dotnet
  test passing is not sufficient for this pipeline") held here too. Two real bugs were caught
  only by opening an actual generated deck in real PowerPoint, both now fixed:
  - **Every 90°/270°-rotated picture was mispositioned** — rendered as a small image
    corner-anchored on the slide instead of filling the frame. Root cause in
    `PptxSlideGeometry.cs`: the picture's `off` (top-left position) was computed from the
    *rotated* bounding box instead of the shape's own *local* (unrotated) box — OOXML rotates a
    shape about the center of its local box, so centering must use the local box's center, not
    the rotated bounding box's position (they're different points whenever rotation swaps
    width/height). The two existing 90°-rotation unit tests had hand-derived expected values
    from the same flawed reasoning as the bug, so they passed anyway — a
    `Compute_AnyRotationOrFitMode_LocalBoxCenterIsCanvasCenter` theory test now checks the
    underlying invariant directly (local box center == canvas center) rather than a per-case
    magic number, specifically so this class of bug can't hide behind a self-consistent wrong
    derivation again.
  - **The file triggered PowerPoint's "found a problem, can repair" prompt on every open** —
    `OpenXmlValidator` stayed silent. Root cause: `SlideLayoutPart` never got the required
    back-reference relationship to its `SlideMasterPart` (only the forward master→layout
    relationship existed, needed for `SlideLayoutIdList`). Fixed with one line
    (`layoutPart.AddPart(masterPart)`); regression-covered by
    `WriteAsync_SlideLayoutPart_HasBackReferenceToItsSlideMaster`.
  - **Confirmed working**: clean open with no repair prompt; slide order; transition *type* per
    slide (Push, Fade); the `advTm` slide-display timing (read back correctly as configured,
    e.g. 3.00s); the background-audio shape survives as a real (hidden) object in the slide
    (checked via PowerPoint's own Selection Pane); Slide Show mode starts and advances through
    all three slides with no crash or error dialog — confirming the `<p:timing>` tree, the
    trickiest piece of OOXML in this milestone, doesn't break playback.
  - **Transition effect duration fixed (2026-09-19).** It read back as PowerPoint's default 0.50s
    because the writer put a made-up `<p14:dur>` element in `<p:extLst>`; the real form
    (MS-PPTX 2.3.2.3) is a `p14:dur` *attribute* on `<p:transition>` inside `mc:AlternateContent`
    with a plain fallback. Confirmed through PowerPoint's object model: 800ms configured reads
    back 0.8s (Fade only — Push directions were not separately re-read). Slide 1's entry fade
    still uses PowerPoint's default duration.
  - **Audio: previously one track, embedded as-is, and never actually played (fixed
    2026-09-19).** The writer embedded only the first track's original bytes (labelling anything
    other than mp3/wav `audio/flac`), and — found while fixing that — PowerPoint read *none* of the
    audio settings: reading the deck back through its object model gave empty `PlayOnEntry`/
    `StopAfterSlides` and a media length of 0. The hand-built timing tree opened without complaint,
    which is why the 2026-09-03 check ("plays through with no error") missed it. Now: every track is
    mixed (same Loop/Trim/FitSlides sequencing as the MP4) into one show-length MP3 by
    `SoundtrackRenderer` and embedded as `audio/mpeg`, so playback never depends on the recipient
    having a decoder for the original format; the shape, both relationships (`audio` link plus
    `p14:media` embed) and timing tree are copied from a file PowerPoint itself saved with
    "play across slides, start automatically, hide during show". Read back through PowerPoint's
    object model the deck now reports media length = show length, `PlayOnEntry` on,
    `StopAfterSlides` 999. The user listened to a deck and the audio played.
  - **Still a scope cut:**
    - No PPTX equivalent for the spec's "fade-out at the end" (only "fade-in at the start" via
      slide 1's own transition) — there's no slide after the last one to carry a transition into.
  - Formats PowerPoint's native image part doesn't support (heic/webp/avif — all accepted
    elsewhere in this app per M2's Known Issues) are decoded and re-encoded to PNG before
    embedding; jpg/png/gif/bmp/tif pass through as the original bytes untouched. Not
    independently re-verified against real PowerPoint this pass (the demo deck used only jpg).
- **M6 (shell extension, `ClickToSlideshow.ShellExtension`) built and verified (2026-09-04).**
  Ported from `Img2PDF.ShellExtension` per the decisions doc's reuse table (~95%: new CLSID,
  exe name, fixed title, combined image+audio extension table). Both Debug and Release build
  clean with `WarningLevel4`/`SDLCheck`, no new warnings.
  - **Verified via direct COM instantiation, not through Explorer's UI** — `CoCreateInstance`
    of the registered CLSID from a small throwaway harness, then calling `GetTitle`/`GetState`/
    `Invoke` against a real `IShellItemArray` built from an actual test file. Confirmed:
    `GetTitle` returns "Create slideshow"; `GetState` returns `ECS_ENABLED` for a supported
    image; `Invoke` returns `S_OK` and genuinely launches `ClickToSlideshow.App.exe` with the
    `--list` temp-file argument, which the app then reads and loads into the grid (screenshot-
    confirmed: the test image appeared as slide 1). This exercises the exact same code path
    Explorer would use — `CoCreateInstance` → the three `IExplorerCommand` methods — without
    depending on Explorer's own context-menu UI, which turned out to be a much less reliable
    verification surface (see below).
  - **Folder selections (2026-09-19):** `GetState` originally showed the entry for *any* folder
    without looking inside (to stay fast), so a folder with no photos or music offered a menu item
    that silently did nothing — contradicting spec §4.1 ("a folder directly containing one").
    It now looks at up to 100 entries, stopping at the first supported file (non-recursive, like
    `Invoke`); a folder with no match in that window is still shown (fails open — never hides a
    folder that might have something usable). Verified against the built DLL directly (loaded via
    `LoadLibrary` + `DllGetClassObject`, which needs no registration and so works even though
    `CoCreateInstance` doesn't for the packaged CLSID): empty folder, folder of only unsupported
    files and a folder holding only a subfolder are hidden; folders with photos, audio, RAW or 300
    files are shown; 50 unsupported files plus one photo is found; 150 unsupported files with no
    photo is shown by design. Explorer's own rendering of this was not observed by me.
  - **Abandoned as a verification method: driving Explorer's actual right-click menu via UI
    Automation/simulated mouse input.** After registering the extension per-user (HKCU,
    `regsvr32`) and restarting `explorer.exe` (required — Explorer caches its verb list at
    startup and does not pick up a newly-registered `AllFilesystemObjects\shell` entry without
    a restart), the item still didn't appear in repeated screenshots of the context menu.
    Blind pixel-offset clicking to reach "Show more options" then landed on an unrelated window
    (this session's own VS Code) once the earlier menu had already closed — confirmed harmless
    (no keystrokes sent, just a stray click) but confirmed the technique itself was unsound for
    this desktop. The direct-COM approach above replaced it entirely and is the more rigorous
    test regardless: it exercises the actual compiled logic (`GetSelectedPaths`,
    `IsSupportedExtension`, `IsUsableSelection`, the temp-file writer, `ShellExecuteW`) rather
    than depending on a specific Explorer build/theme/third-party shell customization actually
    rendering the merged verb list a given way.
  - **Real bug caught only by this direct-COM test, not by code review:** the test harness's
    first version marshaled the `IShellItemArray*` parameter as a bare `object` across the
    P/Invoke boundary, which .NET interop resolves to an `IDispatch` pointer rather than the
    real interface — `items->GetCount()` inside `GetSelectedPaths` returned `E_NOTIMPL`, caught
    by `GetState`'s own catch-all and silently degraded to `ECS_HIDDEN`, exactly the "any
    failure hides the item" design spec §10 asks for. This was a bug in the *test harness*, not
    the shipped C++ (confirmed by declaring `IShellItemArray` as a proper `[ComImport]`
    interface in the harness instead of `object`, after which `GetState` returned `ECS_ENABLED`
    correctly) — but it's recorded here because it demonstrates the catch-all's designed
    behavior firing on a real (if harness-side) failure, not just in theory.
  - **Was not yet verified at M6 time, now resolved by M7:** the packaged-app fallback path in
    `GetAppExePath`/`ActivatePackagedApp` (the MSIX sibling-folder layout) — no package existed
    until M7. See the M7 entry below for what packaged deployment actually confirmed. Explorer's
    own live rendering of the registered verb in its context menu remains unconfirmed — see M7.
- **M7 (MSIX packaging, `ClickToSlideshow.Package`) built and verified via a real deploy
  (2026-09-04).** Ported the wapproj/manifest shape from `Img2PDF.Package`; both Debug and
  Release build clean (`MSBuild.exe`, not `dotnet build` — the wapproj target). Icon set
  generated by `tools/generate-icons.py` against `docs/icon-fanned-frames_1.svg`'s design (see
  Key Files); the exe/window icon (`AppWindow.SetIcon`, ported from `Img2PDF.App`'s
  `MainWindow.xaml.cs` pattern) was wired into `.App` at the same time since it shares the
  `Assets\*.ico` mechanism with the package's own icon set.
  - **Real bug caught by independent `slop-review`, not by the visual spot-check that preceded
    it:** the script's first version copied the SVG's literal `rotate(-15 ...)`/
    `rotate(-7.5 ...)` angle values straight into PIL's `Image.rotate()`, but SVG and PIL use
    opposite sign conventions for the same visual rotation (SVG positive = clockwise, since its
    y-axis points down; PIL positive = counter-clockwise) — mirroring the ghost frames' fan
    direction from the documented design. Confirmed empirically (rotating a marker pixel both
    ways and comparing against the SVG's own rotation matrix applied to the same test point)
    before fixing, then fixed by flipping to positive angles and regenerating every asset.
  - **Verified via the loose-registration deploy trick** (`Add-AppxPackage -Register` against
    the wapproj's loose build output `AppxManifest.xml`, not the `.msix` itself — no signing
    cert needed, same mechanism Visual Studio's own F5 deploy uses): the package registered
    with a real identity (`Get-AppxPackage` showed a genuine `PackageFullName`/
    `PackageFamilyName`), and launching it via `shell:AppsFolder\<PackageFamilyName>!App`
    produced a real running `ClickToSlideshow.App` process with the correct window title —
    confirming WinUI3/WindowsAppRuntime activation genuinely works through the packaged
    identity, not just unpackaged (`WindowsAppSDKSelfContained=false`'s packaged-vs-unpackaged
    branch was exactly the thing M6 flagged as unverified). Closed gracefully
    (`CloseMainWindow`, not a force-kill — see the `dont-force-kill-winappsdk-processes`
    memory) and unregistered afterward with no residue.
  - **Explorer's main-menu entry confirmed by the user (2026-09-19)** on the signed packaged
    install: "Create slideshow" appears where the manifest's `windows.fileExplorerContextMenus`
    extension puts it. (Earlier passes could not verify this — an attempt to drive Explorer's
    right-click menu with simulated input was blocked and, rightly, not worked around.)
  - **Real self-signed dev cert generated and trusted machine-wide (2026-09-04),** at the
    user's explicit request — this is what the "signed packaged install" above is running on.
    `New-SelfSignedCertificate` with `-Subject` matching `Package.appxmanifest`'s `Publisher`
    exactly, exported to `src/ClickToSlideshow.Package/ClickToSlideshow.Package_TemporaryKey.pfx`
    (gitignored, per-machine, empty password). `CurrentUser\TrustedPeople` alone was not enough
    for a genuinely signed `Add-AppxPackage` install (`0x800B0109`, "root certificate must be
    trusted") — the cert also had to go into `LocalMachine\TrustedPeople`, which needs
    elevation. Re-testing after a rebuild just needs a fresh `.msix` build and re-running
    `Add-AppxPackage`; regenerating the cert is only needed if the manifest's `Publisher`
    changes.
  - **`Identity Name`/`Publisher` in `Package.appxmanifest` match Partner Center** (checked
    2026-09-26 against the Product identity page, including the Package Family Name of the
    installed build). The local signing cert is this app's own self-signed one (same Publisher
    subject as ClickTo: PDF's, but a separate key).
  - **A build-order gotcha worth knowing:** a plain `dotnet build`/`dotnet restore` on `.App`
    without `-r win-x64` regenerates `.Core`'s `obj/project.assets.json` *without* the
    `net8.0-windows10.0.19041/win-x64` restore target the wapproj's publish-profile build
    requires, which then fails the wapproj build with `NETSDK1047` even though `.App`/`.Core`
    build individually just fine. Fix: `dotnet restore src/ClickToSlideshow.App/ClickToSlideshow.App.csproj -r win-x64`
    before invoking `MSBuild.exe` on the wapproj, any time `.App`/`.Core` were rebuilt in
    between via plain `dotnet build`.
