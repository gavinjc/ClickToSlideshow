# ClickTo: Slideshow — Technical Specification

**Internal codename:** `ClickToSlideshow` *(settled 2026-09-01 — repo, solution, projects, and
namespaces; supersedes the `Img2Show` codename this doc originally proposed)*
**Product name:** ClickTo: Slideshow
**Target:** Windows 11 (22H2+), Windows 10 20H1+ as secondary
**Distribution:** Microsoft Store
**Status:** greenfield; heavy source-level reuse from ClickTo: PDF (`ClickToPDF`)
**Companion document:** `clickto-slideshow/decisions.md` — the decision record this spec implements

---

## 1. Problem statement

You have forty photos from a weekend and you want to watch them, with music, on the TV in
the lounge room. On Windows 11 today that is:

- **Photos app** — slideshow works, but it is bound to a folder or an album. An arbitrary
  cross-folder selection means building an album first. No music without editing the album
  into a video project.
- **File Explorer** — Windows 10's "Slide show" ribbon tab is gone in Windows 11's Explorer.
  There is no longer a right-click path to "just play these."
- **Clipchamp** — the Windows built-in. A full timeline video editor: import, drag to
  timeline, set durations individually. Account-gated for parts of export. Enormous overkill
  for "play these in order with a song."
- **PowerPoint** — works, and produces a genuinely portable artifact, but building the deck
  by hand for forty photos is tedious and nobody does it.

None of them start from the selection you already made in Explorer. That is the gap.

## 2. Product definition

A single-purpose utility. Select image files (and optionally audio files) in File Explorer →
right-click → **Create slideshow** → a window opens showing readable thumbnails in a
reorderable grid with per-slide durations and an audio strip → adjust order, timing and
transitions → **Play** fullscreen, or **Save** as MP4, PPTX, or a project file.

**The reorder grid plus the runtime/audio bar is the product.** Everything else is optional
polish and must not complicate that flow.

The deliberate design inheritance from ClickTo: PDF is that *no import step exists*. The
files stay where they are; the app never copies them into a library.

### Explicit non-goals for v1

- Not a video editor: no trimming, no multi-track timeline, no keyframes, no titles or text
  overlays, no colour grading
- No video clips as slides (see §8 — explicitly an edge case, not a main feature)
- No per-slide audio, voiceover, or narration recording
- No Ken Burns pan/zoom (see §8 — the pipeline gives it cheaply later)
- No casting, DLNA, or TV discovery
- No cloud, no accounts, no telemetry, no auto-updater

### The one deliberate divergence from ClickTo: PDF

ClickTo: PDF's spec says *"if a feature request would add a second window, it is out of
scope."* ClickTo: Slideshow **does** add a second window: the fullscreen player. This is the
single exception and it is justified — fullscreen playback cannot be delivered inside the
editor window without either letterboxing the show or hijacking the editor's layout. No
other second window is permitted.

---

## 3. Architecture

Five projects in one MSIX package, mirroring the ClickTo: PDF solution layout.

```
ClickToSlideshow.sln
├── ClickToSlideshow.Core/             C# / .NET 8 — timeline model, frame composer, renderers
├── ClickToSlideshow.Cli/              C# console — headless render, the M1 proving ground
├── ClickToSlideshow.App/              C# / .NET 8 / WinUI 3 — editor window + fullscreen player
├── ClickToSlideshow.ShellExtension/   C++ (WRL) DLL — IExplorerCommand, launches the app
└── ClickToSlideshow.Package/          Windows Application Packaging Project → MSIX
```

### 3.1 Why this split

Identical reasoning to ClickTo: PDF §3.1 — the Windows 11 *main* context menu requires a
native in-proc `IExplorerCommand` registered through an MSIX manifest, so the extension must
be small, native, fast, and must never throw. It does nothing but collect paths and start
the app.

`ClickToSlideshow.Cli` earns its place beyond dev convenience: it is the render pipeline with no
XAML attached, which makes M1 verifiable before any UI exists and keeps the composer honestly
decoupled from WinUI.

### 3.2 Technology choices

| Concern | Choice | Rationale |
|---|---|---|
| Shell extension | Native C++ (WRL), `IExplorerCommand` | Required for Win11 main menu; ports ~95% unchanged from ClickTo: PDF |
| App UI | WinUI 3 (WindowsAppSDK 2.3.1), .NET 8 | Same as ClickTo: PDF; `GridView` gives drag-reorder free |
| Image decode | `Windows.Graphics.Imaging.BitmapDecoder` (WIC) | Extensible via Store codec packs — format coverage costs an array entry, not a decoder (§4.4) |
| Frame compositing | **Win2D** (`Microsoft.Graphics.Win2D`, MIT) | GPU compositing; the only way to get transitions at all (§4.5) |
| Video encode | `MediaStreamSource` → `MediaTranscoder` | OS H.264 encoder, hardware accelerated, no codec licensing |
| Audio mux | `Windows.Media.Editing` `MediaComposition` | Used **only** as a muxer for the audio track and its loop logic |
| Audio transcode | `MediaTranscoder` | `.m4a`/`.aac`/`.wma` → a format `BackgroundAudioTrack` accepts |
| PPTX writing | `DocumentFormat.OpenXml` 3.5.x (MIT) | First-party, pure managed, no native binaries |
| Packaging | Windows Application Packaging Project | Standard MSIX for Partner Center |

**Do not** use FFmpeg (a ~70 MB bundled binary plus LGPL compliance obligations on the Store
listing), and **do not** attempt to use `MediaComposition` as the render engine — §4.5
explains why it structurally cannot produce a transition.

Total new third-party surface versus ClickTo: PDF: two MIT packages, both from Microsoft,
both pure managed. The About box's third-party list grows by two lines.

---

## 4. Component specs

### 4.1 Shell extension (`ClickToSlideshow.ShellExtension`)

Ported from `ClickToPDF.ShellExtension`. New CLSID, new exe name, new titles, new extension
table. Everything else — module-path resolution, the packaged-vs-flat exe layout fallback,
`ActivatePackagedApp` with `CoAllowSetForegroundWindow`, `GetSelectedPaths`, `ExpandFolder`,
`WriteLinesFile` — carries over unchanged.

**Registration** — declared under `desktop4:FileExplorerContextMenus`, associated with
`AllFilesystemObjects` (not per-extension; see ClickTo: PDF §4.1 for why per-extension
registration is pruned by Explorer's multi-selection merge).

**Title** (`GetTitle`): **Create slideshow**, for any selection count. Unlike ClickTo: PDF
there is no meaningful single-item variant — a one-photo slideshow is a degenerate case, not
a different verb.

**Visibility** (`GetState`): `ECS_ENABLED` when at least one selected item is a supported
image, a supported audio file, or a folder directly containing one. `ECS_HIDDEN` otherwise.
Never throws; any failure degrades to hidden.

> An audio-only selection **does** show the command — the user gets the editor with an audio
> track loaded and an empty grid, and adds photos from inside the app. This is a legitimate
> starting point and cheaper to allow than to special-case.

**Invoke** — must return in well under 100 ms:

1. Enumerate the `IShellItemArray`, classifying each item: supported file used as-is;
   folder expanded non-recursively into its supported files; anything else recorded by
   filename as skipped.
2. Write supported paths UTF-8, one per line, to
   `%LOCALAPPDATA%\Temp\ClickToSlideshow\{guid}.list`; skipped filenames to a `.skipped` file.
3. Launch `ClickToSlideshow.exe --list "<path>" [--skipped "<path>"]`.
4. Return `S_OK`.

> **Do not pass file paths on the command line.** Inherited non-negotiably from ClickTo: PDF
> and more important here — 300-photo selections are the normal case, not the extreme.

**One classification decision:** the `.list` file contains **both images and audio, mixed**.
The extension gates *visibility* using a combined supported-extension set; the App does the
image-vs-audio classification. The App must own that logic regardless, because files added
from inside the app go through the same path — duplicating the audio table into C++ would be
a near-duplicate of the kind `CLAUDE.md` explicitly calls a defect.

### 4.2 Timeline model (`ClickToSlideshow.Core`)

One model, three consumers (player, MP4 renderer, PPTX writer). This is the spine of the app.

```
SlideshowProject
  FormatVersion        int
  Slides               [ Slide ]
  AudioTracks          [ AudioTrack ]
  DefaultSlideMs       int      (default 4000)
  TransitionKind       enum     (default Crossfade)
  TransitionMs         int      (default 1000)
  Resolution           enum     (Original | UHD2160p | HD1080p; default HD1080p. Original = the
                                 largest slide by area after rotation, capped at H.264 level 5.2
                                 (36,864 macroblocks, 4096 a side, 128 minimum), even sizes —
                                 see decisions.md, "Original resolution")
  FitMode              enum     (Fit | Fill; default Fit)
  BackgroundColor      colour   (default black)
  AudioMatch           enum     (Loop | Trim | FitSlides; default Loop)

Slide
  SourcePath           string   (relative where a common root exists, else absolute)
  DurationMs           int      (per-slide override; defaults to DefaultSlideMs)
  RotationDegrees      int      (EXIF baseline plus user adjustment)
  TransitionIn         enum?    (per-slide override; null = project default)

AudioTrack
  SourcePath           string
  DurationMs           int      (probed on import)
```

**Runtime arithmetic — decided, and reversible if it tests badly.** Transition time is
**additive**, not carved out of slide duration:

```
Runtime = Σ(slide.DurationMs) + (slideCount − 1) × TransitionMs
```

A slide is fully visible for its stated duration; transitions happen *between* slides. Two
reasons: it is what a user setting "4 seconds per slide" expects, and it matches PPTX
semantics, where `advTm` (dwell) and the transition's own duration are separate values — so
the archived deck has the same runtime as the MP4. Constraint: `TransitionMs < DefaultSlideMs`,
enforced in the UI.

**Audio reconciliation** — `AudioMatch` resolves the runtime-versus-audio-length mismatch:

| Mode | Audio shorter than show | Audio longer than show |
|---|---|---|
| `Loop` *(default)* | Repeat tracks to fill; fade out over the final 2 s | Trim at runtime; fade out over the final 2 s |
| `Trim` | Play once, then silence | Trim at runtime with fade-out |
| `FitSlides` | Scale every slide duration proportionally so the show matches the audio | Same |

`FitSlides` scales `DurationMs` for display only; it never rewrites the stored per-slide
values, so switching back to `Loop` restores the user's timings exactly.

### 4.3 Main app — editor window (`ClickToSlideshow.App`)

Single window, ported from `ClickToPDF.App`'s `MainWindow`. No menu bar, no ribbon, no settings
dialog beyond the inline expander.

**Slide grid.** Virtualised `GridView`, drag-reorder, multi-select, sort (name-natural, date
taken, date modified, reverse), rotate ±90°, remove, undo (20-deep). Each tile shows the
thumbnail, slide number, a **duration badge**, and a small transition glyph on its leading
edge. Per-tile buttons: preview, rotate, remove, and set-duration.

**Audio strip.** Beneath the grid. Lists added tracks in order with individual lengths and a
total. Drag-reorder, remove. Empty state invites the user to add a track.

**Runtime bar.** The one genuinely new piece of UI. Two proportional bars, aligned on a
common time axis: show runtime above, audio length below, with the shorter one visibly
short. The `AudioMatch` control sits directly beside it, so the consequence of each mode is
visible rather than described. This is the element that makes the app feel considered — spend
design effort here.

**Options expander.** Default slide duration, transition kind, transition duration,
resolution, fit mode (fit/fill), background colour. Collapsed by default.

**Primary actions.** `Play` and a split-button `Save` → *Video (MP4)* / *PowerPoint (PPTX)* /
*Project (JSON)*. Progress bar and cancel during any save, reusing ClickTo: PDF's existing
save-progress UI. The PPTX save shares the MP4's progress row and Cancel (from 1.0.2): the
soundtrack first ("Adding music"), then one step per slide ("Rendering slides").

**Adding files.** The small + on the SLIDES and AUDIO headers (not the family's round add
button), and, from 1.0.2, files and folders dragged in from File Explorer anywhere over the
slides and audio. A drop follows the right-click menu's rules exactly: a folder gives the
supported files directly inside it, its other files are reported as skipped, subfolders are
ignored, and a folder with nothing usable is skipped by its own name. Dropped photos go in
natural name order after what is loaded; one drop is one undo step. Repeats are allowed (the
family's one exception to skipping duplicates).

**Messages.** From 1.0.2, every message goes through Bureau.Common's shared message bar,
floating over the bottom of the content row so it never covers the runtime footer: saved (with
Open and Show in folder), skipped files, music that couldn't be found or read, and errors. One
row per kind; each load or add closes the rows the previous one left. Skipped wording follows
the family rule ("Skipped notes.txt", "2 files skipped: a.txt, b.png", never "(s)").
**Per-tile error states** carry over from ClickTo: PDF, including the codec-missing message
path (§4.4); the codec messages tell the user to remove the slide and add the photo again.

### 4.4 Input formats

`ImageInspector` already decodes via WinRT `BitmapDecoder`, which is WIC — and WIC is
extensible, with Microsoft Store codec packs registering *into* it. Widening format coverage
is therefore an extension-list change, not a decoder.

| Tier | Extensions |
|---|---|
| In-box, all Win10/11 | `.jpg .jpeg .jfif .png .bmp .tif .tiff .jxr .wdp` |
| Free Store codec pack | `.webp .avif` |
| HEIF Image Extensions **+ an HEVC decoder** | `.heic .heif` |
| Raw Image Extension | `.arw .cr2 .cr3 .crw .dng .erf .kdc .mrw .nef .nrw .orf .pef .raf .raw .rw2 .sr2 .srf .srw .x3f` |

Excluded despite being WIC-reachable, as implausible in a photo slideshow: `.ico`, `.dds`.

**`.gif` is deliberately excluded** — a divergence from ClickTo: PDF, which includes it. For
a PDF, silently taking frame 0 is invisible; in a slideshow a user who adds a GIF expects
motion. Better to not offer it than to offer it wrong.

**Audio:**

| Handling | Extensions |
|---|---|
| Native to `BackgroundAudioTrack` | `.mp3 .wav .flac` |
| Transcoded to a temp file on import via `MediaTranscoder` | `.m4a .aac .wma .ogg .opus` |

`.m4a` is the single most likely user file (voice memos, Apple Music rips) and is *not*
natively accepted, so the transcode path is required, not optional. Transcoded temp files
live under `%LOCALAPPDATA%\Temp\ClickToSlideshow\` and are deleted on clean exit.

**Codec availability is a runtime condition, not a compile-time one.** Probe once when the
project loads and again immediately before any Save, and surface a single actionable banner
rather than per-file errors discovered mid-render. `WINCODEC_ERR_COMPONENTNOTFOUND`
(`0x88982F50`) is the signal, and it applies to WebP, AVIF and RAW as well as HEIF — the
guard must be format-agnostic, keyed on the failing file's own extension.

### 4.5 Render pipeline

**Why `MediaComposition` is not the render engine.** It structurally cannot produce a
transition — this is not a missing feature that could be added later:

- `IBasicVideoEffect` processes frames of **one** clip; a transition needs two sources
  overlapping in time.
- `MediaComposition.Clips` is strictly sequential; clips cannot overlap.
- `MediaOverlay` can be time-positioned and has an `Opacity`, but that is a **static** value,
  not animatable across the overlay's duration.

A single crossfade is already enough to require owning the pipeline.

**The engine:**

```
          SlideshowProject
                 │
                 ▼
          FrameComposer            Win2D — given time t, produce one frame
                 │
      ┌──────────┴──────────┐
      ▼                     ▼
 live playback         MediaStreamSource
 (fullscreen)                │
                             ▼
              MediaTranscoder.PrepareMediaStreamSourceTranscodeAsync
                             │
                             ▼
                     MP4 (video only)
                             │
                             ▼
              MediaComposition — attach audio → final MP4
```

`FrameComposer.Render(t)` is a **pure function of the timeline**. Everything follows from
that: preview and export are the same code with different clocks, so they cannot drift apart;
scrubbing is free; adding a transition is one class and no engine change.

**Frame supply** follows the documented screen-capture-to-video pattern:
`MediaStreamSample.CreateFromDirect3D11Surface` on `SampleRequested`, with
`MediaTranscoder.HardwareAccelerationEnabled = true`. Two known gotchas from that pattern
apply directly: **output dimensions must be even numbers**, and `MediaStreamSource.BufferTime`
should be zero.

> We synthesise frames rather than capturing them, so — unlike that sample — the app does
> **not** need the Graphics Capture capability in its manifest. Do not add it.

**Audio muxing is two-pass**, by decision: Win2D renders video-only, then `MediaComposition`
attaches `BackgroundAudioTracks` with the loop/trim/fade logic and muxes. Far less code than
a single `MediaStreamSource` carrying both video and audio descriptors, at the cost of one
re-encode of the video. Keep muxing behind an interface so a single-pass implementation can
replace it if the generation loss proves visible in practice.

**Decode caching.** Decode each slide to the target resolution on demand and hold a small LRU
of the current and next slides only. Never hold all slides decoded — a 300-photo project at
4K would not fit in memory.

### 4.6 Transitions

Deliberately small. Every one maps exactly onto an OOXML element, so transitions survive into
the PPTX archive with **no degradation** — a larger set would force exotic transitions to
collapse to `fade` on export, quietly making the archive unfaithful to the project.

| Transition | Win2D implementation | PPTX element |
|---|---|---|
| Cut | none — advance | *(element omitted)* |
| Crossfade / dissolve | two `DrawImage` calls, alpha ramp | `<p:fade thruBlk="0"/>` |
| Fade through black | alpha ramp via the background colour | `<p:fade thruBlk="1"/>` |
| Push (L/R/U/D) | offset transform on both sources | `<p:push dir="l"/>` |

Plus a global fade-in at the start and fade-out at the end of the show.

None of these needs Win2D's effect graph, masks, or geometry clipping — a crossfade is two
`DrawImage` calls with an alpha value. This is the shallowest possible use of Win2D, which
materially reduces exposure to the work-in-progress risk in §10.

### 4.7 Fullscreen player

Second window (the sanctioned exception, §2). Borderless, on a user-chosen monitor, black
background.

- Renders from the same `FrameComposer` as the export path, driven by a wall clock
- Space = play/pause, ←/→ = previous/next slide, Esc = exit, F = toggle fullscreen
- Controls auto-hide after 3 s of no pointer movement; a thin scrub bar on hover
- Audio plays through `MediaPlayer` in sync with the frame clock
- Deliberately stays out of Windows' media controls (`CommandManager.IsEnabled = false`), as
  does the editor's track preview: only the music is in a `MediaPlayer`, so a pause from that
  panel or a media key would stop the music while the slides ran on
- Multi-monitor: if more than one display is present, offer the non-primary by default so the
  editor stays visible on the primary

### 4.8 Outputs

Three writers over one model. Each covers a property the others don't.

| Output | Self-contained | Editable | Open standard | Opens anywhere |
|---|---|---|---|---|
| MP4 | yes | no | yes | yes |
| PPTX | yes | yes | yes (ISO/IEC 29500) | needs a viewer |
| JSON | **no** — references originals | yes | yes (ours) | no |

**MP4** — §4.5. H.264 in MP4, AAC audio, at the project's resolution, 30 fps.

**PPTX** — archival in an open documented format, plus handoff to people without the app.
- One full-bleed picture shape per slide at the project's resolution setting
- `<p:transition advTm="…">` per slide carrying the §4.6 mapping
- Background audio as a media relationship, with a `<p:timing>` tree that plays across all
  slides. **This is the one genuinely awkward piece of OOXML in the project** — budget for it
  and verify in real PowerPoint, not just by schema validity.
- **Embed the project JSON as a custom XML part**, so the deck round-trips back into an
  editable project. PowerPoint ignores parts it does not recognise. Source paths inside it
  are stored as **bare filenames** (decided 2026-09-26, before release): a deck is meant to be
  shared and any path discloses folder names or the user name. Reopening finds sources beside the
  .pptx only.
- Write original filename and Date Taken into each slide's notes, so the archive is
  self-documenting without our app.

**JSON project** — we own the schema and version it via `FormatVersion`. Store **paths
relative to a common root where one exists, with an absolute fallback**, so moving a photo
folder does not invalidate every saved project. On load, report missing sources per-slide
rather than refusing the whole project.

---

## 5. Performance requirements

Test set: **200 JPGs, ~4 MB each, 4000 × 3000 px** (typical phone camera output), plus one
5-minute MP3.

| Metric | Target |
|---|---|
| Window visible after invoke | < 500 ms |
| All 200 thumbnails rendered | < 8 s |
| Reorder drag | 60 fps, no stutter |
| Play starts after clicking Play | < 1 s |
| Sustained playback at 1080p | 30 fps, no dropped transitions |
| MP4 export | ≥ 2× realtime (a 10-minute show in under 5 minutes) |
| Peak memory | < 800 MB |

Implementation notes:

- Prefer `BitmapDecoder.GetThumbnailAsync` where an embedded thumbnail exists, falling back
  to a scaled decode — the existing `ImageInspector` already does exactly this.
- **RAW is the exception that will break the thumbnail budget.** `MaxConcurrentThumbnailLoads = 4`
  is tuned for JPEG; RAW files are 25–50 MB with no useful embedded thumbnail at 320 px, so
  200 RAWs will feel like a hang. Size concurrency to the format, and show real progress.
- The UI thread must never block on file or GPU work.
- Export must remain cancellable at frame granularity and must clean up partial output.

---

## 6. Build milestones

Build and verify in this order. Each milestone is independently runnable.

**M0 — Feasibility spike (throwaway).**
Win2D `CanvasRenderTarget` → Direct3D11 surface → `MediaStreamSample` → `MediaTranscoder` →
playable MP4, with two images and one crossfade. **Must be done in a packaged WinUI 3 app,
not a console app** — that is where CsWinRT issues have historically surfaced.
*Exit: an MP4 that plays with a visibly smooth crossfade.*
**This is a gate.** If Win2D on WinUI 3 fights back, stop and revise §3.2 to FFmpeg + `xfade`
before writing anything else. Roughly one day; delete the code afterwards.

**M1 — Core render engine (`ClickToSlideshow.Cli`).**
The timeline model, `FrameComposer`, the MP4 renderer, and the audio mux — no UI. CLI takes a
`.list` plus an audio path and emits a finished MP4.
*This is the milestone most likely to disappoint — validate transition smoothness, colour,
aspect handling and export speed against real photo sets before building any UI.* Mirrors
ClickTo: PDF's M1 discipline exactly.

**M2 — WinUI 3 editor shell.**
Port `MainWindow` and the grid half of `MainViewModel` from ClickTo: PDF. Slide tiles with
duration badges, drag-reorder, rotate, remove, undo, sort. Audio strip. Runtime bar and the
`AudioMatch` control. Launched manually with a folder path. Meets the §5 thumbnail targets.

**M3 — Fullscreen playback.**
The player window over the same `FrameComposer`, wall-clock driven, with audio sync,
keyboard control, auto-hiding chrome and monitor selection.

**M4 — Save flows: MP4 and project JSON.**
Options expander wired to the model, save picker, naming rules, progress, cancellation,
success InfoBar. Open and save `.json` projects, including the missing-source path.

**M5 — PPTX writer.**
`DocumentFormat.OpenXml`, the §4.6 transition mapping, background audio across slides, the
embedded project JSON custom part, and slide-notes metadata. Verify in real PowerPoint.
*Most self-contained milestone — can be swapped with M6 if convenient.*

**M6 — Shell extension.**
Port from `ClickToPDF.ShellExtension`: new CLSID, new titles, combined image+audio extension
table. Register per-user (HKCU) via `regsvr32` for dev testing — no admin, fully reversible.
*Expect this to surface under "Show more options" only; main-menu placement requires the
MSIX manifest registration that does not exist until M7.*

**M7 — MSIX packaging.**
Packaging project, manifest with the context-menu extension, assets at all required scales,
self-signed cert for local sideload. **Do not add the Graphics Capture capability.**

**M8 — Store readiness.**
Listing copy, screenshots, privacy policy page, age rating, About-box third-party list
updated with Win2D and OpenXml, final Windows App Certification Kit run.

---

## 7. Acceptance criteria

Walked by hand and all ticked, 2026-10-02 (`docs/store/submission-checklist.md` §8).

- [x] Selecting 1–300 mixed images and audio files in Explorer shows **Create slideshow** in
      the Windows 11 main context menu
- [x] Menu entry is hidden when nothing in the selection qualifies
- [x] 300 selected files launch correctly (command-line length regression guard)
- [x] An audio-only selection opens the editor with the track loaded and an empty grid
- [x] A folder selection expands non-recursively to its images and audio
- [x] Thumbnails are legible enough to distinguish two similar photos
- [x] Drag-reorder updates slide numbers on drop
- [x] Rotation persists into MP4 and PPTX output and never modifies the source file
- [x] EXIF-rotated phone photos appear upright in grid, playback, MP4 and PPTX
- [x] Runtime bar arithmetic matches actual MP4 duration to within one frame
- [x] Each `AudioMatch` mode behaves as specified in §4.2 in both shorter and longer cases
- [x] `FitSlides` is reversible — switching back to `Loop` restores exact user timings
- [x] Every transition in §4.6 renders identically in live playback and in the exported MP4
- [x] PPTX opens in real PowerPoint with correct order, timings, transitions, and audio that
      plays across all slides
- [x] PPTX round-trips: reopening it in ClickTo: Slideshow reconstructs the project
- [x] Project JSON survives moving the whole photo folder (relative-path resolution)
- [x] Missing sources on project load are reported per-slide, not as a whole-project failure
- [x] A missing codec is reported as one actionable banner **before** Save, not discovered
      mid-render
- [x] A corrupt or unreadable image produces a per-tile error state, not a crash
- [x] Source files are byte-identical after every operation
- [x] Export is cancellable and leaves no partial output file
- [x] Meets all §5 performance targets
- [x] Passes Windows App Certification Kit

---

## 8. Deliberately deferred

Candidates for v1.1+, in rough priority order. **None belong in v1** — they are listed so the
architecture does not preclude them.

1. **Ken Burns pan/zoom.** A source-rect transform inside `FrameComposer`; essentially free
   once the pipeline exists. The reason to defer is UI, not engine: per-slide framing controls
   are a real design problem.
2. **Video clips as slides.** Explicitly an edge case, not a main feature. The pipeline makes
   this *easier* than under MediaComposition — a video slide is just a `FrameComposer` source
   that varies with `t` — but it drags HEVC onto the critical path for iPhone footage and needs
   its own audio-ducking design.
3. **Per-slide captions.** Draws cleanly in Win2D and maps to a PPTX text shape.
4. **Shuffle and repeat on playback.** Playback-only; does not touch the model.
5. **Casting to a TV** (DLNA / Miracast). Large surface, and the honest alternative is "save
   an MP4 and open it on the TV."
6. **More transitions** — only if the PPTX fidelity cost in §4.6 is accepted explicitly.
7. **GIF as an animated slide.** Requires frame-accurate GIF decode in the composer.
8. **Batch/CLI automation** — the CLI exists from M1; exposing it publicly is a support
   commitment.

---

## 9. Store submission notes

The `docs/store/` scaffold from ClickTo: PDF ports directly — listing structure, privacy
policy page, submission checklist, screenshot conventions.

- New Partner Center name reservation required; `Identity Name` / `Publisher` stay
  placeholders until it exists
- Screenshots should lead with the **runtime/audio bar**, not the grid — the grid looks like
  every other photo app; the bar is what makes this one legible at a glance
- About-box third-party list gains two entries: Win2D (MIT) and DocumentFormat.OpenXml (MIT)
- No new manifest capabilities beyond ClickTo: PDF's set. Specifically **not** Graphics Capture
- Age rating answers carry over unchanged
- Expect the first submission to bounce on something cosmetic. That is normal.

---

## 10. Constraints and cautions

- **Never modify source files.** Read-only always, inherited non-negotiably from ClickTo: PDF.
- **Explorer stability.** Any unhandled exception in the shell extension degrades the user's
  Explorer. Wrap everything; fail to no-menu-entry rather than throwing.
- **No telemetry, no network calls, no auto-update.** The app must work identically offline,
  forever.
- **Win2D on WinUI 3 is officially "work in progress."** The repo README says so and some
  features are partial. This is the single largest technical risk in the project, which is why
  M0 is a gate rather than a task.
- **Do not let `MediaComposition` creep back into the render path.** It is a muxer here. The
  moment it is asked to sequence slides, transitions become impossible again.
- **Output dimensions must be even.** Some hardware encoders reject odd width or height, and
  the failure is opaque.
- **Codec availability is a runtime condition.** Probe before Save. A render that fails forty
  slides in is a far worse experience than a broken tile in a grid.
- **Temp file hygiene.** Transcoded audio, list files and partial exports all live under
  `%LOCALAPPDATA%\Temp\ClickToSlideshow\`. Prune on launch the way `AppLog.PruneOldLogs` already does.
- **Do not extract a shared `ClickTo.Shell` library yet.** One shipped app is a sample of one.
  Fork for this app; extract when a third app shows what is genuinely common.
