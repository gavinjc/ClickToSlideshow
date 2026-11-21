# ClickTo: Slideshow — Decision Record

Status: decisions locked 2026-09-01. Successor app to ClickTo: PDF (repo `Img2PDF`, now `ClickToPDF`).
Same workflow: select files/folder in Explorer → right-click → grid UI → options → Play or Save.

Goal stated: **feature-complete v1**, willing to take longer rather than defer core features to a v2.
Video input is explicitly *not* a main feature — a nice-to-have edge case.

**Implemented by:** `clickto-slideshow/spec.md` — the full technical specification and phased build plan.

---

## Platform (confirmed from the ClickTo: PDF codebase)

.NET 8 / WinUI 3 / C#, `net8.0-windows10.0.19041.0`, WindowsAppSDK 2.3.1 framework-dependent,
MSIX via wapproj, native C++ (WRL) `IExplorerCommand` shell extension.

**Dependencies added for this app:**

| Package | Licence | Purpose |
|---|---|---|
| `Microsoft.Graphics.Win2D` | MIT (Microsoft) | Frame compositing for preview and MP4 render |
| `DocumentFormat.OpenXml` | MIT (Microsoft) | PPTX writer |

No FFmpeg, no LGPL obligation, no native binaries beyond the existing shell extension. Encoding uses
the OS H.264 encoder via `MediaTranscoder` with `HardwareAccelerationEnabled = true`.

---

## Decision 1 — HEIF/HEIC: detect and explain, never bundle

Carry ClickTo: PDF's policy forward in principle. Do **not** ship libheif or any bundled decoder —
the HEVC patent exposure is a bad trade for a low-priced utility.

**Two corrections to the inherited implementation:**

1. **The existing message is half the fix.** `MainViewModel` line ~26 comments that HEIC decode
   fails "until the user installs the free Microsoft Store HEIF Image Extensions." HEIC image data
   is HEVC-coded, so decode requires **both** HEIF Image Extensions (free) **and** an HEVC decoder.
   HEVC Video Extensions is a ~$0.99 Store item; the free "from Device Manufacturer" variant is
   OEM-preinstalled, which is why HEIC works on most retail laptops and fails on clean installs.
   Audit `HeicCodecMissingMessage` in `Strings/en-us/Resources.resw` — it currently names only HEIF
   Image Extensions, so users will install it and still fail.

2. **Probe at load and again before Save — not per-file at error time.** Thumbnail decode
   (`BitmapDecoder`) and render decode are separate code paths. A user can get a perfect-looking
   grid and have the render fail partway through. Block early with an actionable banner.

Prefer `ms-windows-store://assoc/?FileExt=<ext>` over a hardcoded ProductId — documented, generic
across every format the guard covers, and cannot go stale when a Store listing changes.

Generalise the guard beyond HEIF — `WINCODEC_ERR_COMPONENTNOTFOUND` (`0x88982F50`) also applies to
WebP, AVIF and RAW (see Decision 3).

*A separate cleanup task exists for fixing this same workflow in the shipped ClickTo: PDF.*

---

## Decision 2 — Three outputs: MP4, PPTX, JSON

Each covers a property the others don't. None is redundant.

| Output | Self-contained | Editable | Open standard | Opens anywhere |
|---|---|---|---|---|
| MP4 | yes | no | yes | yes |
| PPTX | yes | yes | yes (ISO/IEC 29500) | needs a viewer |
| JSON | **no** — references originals | yes | yes (ours) | no |

**MP4** — rendered by our own frame pipeline (Decision 4), muxed with audio via MediaComposition.

**PPTX** — purpose is *archival in an open, documented format* plus handoff to other people.
- Resolution setting (Original / 4K / 1080p) resolves the file-size concern without a second writer.
- Per-slide `<p:transition advTm="...">` for auto-advance, carrying the real transition.
- Background audio as a media relationship with a `<p:timing>` tree that plays across all slides —
  the one genuinely awkward piece of OOXML in the project.
- **Embed the project JSON as a custom XML part**, so the .pptx round-trips back into an editable
  project. PowerPoint ignores unrecognised parts.
  *Revised 2026-09-26 (`bf2c3a2`):* the embedded part stores **bare filenames only**, not the
  relative-with-absolute-fallback paths the JSON project file uses. A .pptx is a file people
  share, and the old paths leaked folder names and, across drives, the Windows user name. The
  cost: reopening a .pptx finds its media only when it sits beside the deck.
- Write original filename and Date Taken into each slide's notes, so the archive is
  self-documenting without our app.

**JSON project file** — we own the schema and can version it; preferred over MediaComposition's own
`.cmp`. Store **relative paths where files share a root, with absolute fallback**, so moving a photo
folder doesn't invalidate every saved project.

**Architecture:** one `SlideshowProject` model in Core → three writers.

---

## Decision 3 — Inputs: maximise WIC coverage, add audio transcode

**Key insight:** `ImageInspector` already decodes via WinRT `BitmapDecoder` = WIC, and WIC is
extensible — Store codec extensions register *into* it. Any format the user has a codec for flows
through existing code unchanged. Widening coverage is an array change, not a decoder.

### Images — accept everything WIC can reach

| Tier | Extensions |
|---|---|
| In-box (all Win10/11) | `.jpg .jpeg .jfif .png .bmp .tif .tiff .jxr .wdp` |
| Free Store extension | `.webp .avif` |
| HEIF + HEVC | `.heic .heif` |
| Raw Image Extension | `.arw .cr2 .cr3 .crw .dng .erf .kdc .mrw .nef .nrw .orf .pef .raf .raw .rw2 .sr2 .srf .srw .x3f` |

Excluded as implausible in a photo slideshow despite being WIC-reachable: `.ico`, `.dds`.

**`.gif` is dropped** — deliberate divergence from ClickTo: PDF, which keeps it. For PDF, silently
taking frame 0 is invisible; in a slideshow a user who adds a GIF expects motion.

**Watch item:** `MaxConcurrentThumbnailLoads = 4` is tuned for JPEGs. RAW files are 25–50MB with no
useful embedded thumbnail at 320px, so 200 RAWs will feel like a hang.

### Audio — accept broadly, transcode on import

`BackgroundAudioTrack` natively accepts **MP3, WAV, FLAC only**. The most likely user file is `.m4a`.

- Native, pass through: `.mp3 .wav .flac`
- Transcode via `MediaTranscoder` on import: `.m4a .aac .wma .ogg .opus`

### Video — not a v1 feature

Explicitly deprioritised as a nice-to-have edge case. Note that the Decision 4 pipeline makes adding
video later *easier*, not harder — a video slide is just a `FrameComposer` source that varies with `t`.

---

## Decision 4 — Transitions in v1, via our own frame pipeline

### Why MediaComposition cannot be the render engine

Transitions are non-negotiable for v1, and MediaComposition **structurally cannot produce one**:

- `IBasicVideoEffect` processes frames of **one** clip; a transition needs two overlapping sources.
- `MediaComposition.Clips` is strictly sequential — clips cannot overlap in time.
- `MediaOverlay` can be time-positioned and has an `Opacity`, but it is a **static** value, not
  animatable across the overlay's duration.

**A single crossfade is already enough to require owning the pipeline.** The size of the transition
set does not change this decision; it only changes how much work sits on top.

### The engine

`FrameComposer.Render(t)` — a pure function of the timeline, built on Win2D — feeds both live
playback and a `MediaStreamSource` that `MediaTranscoder` encodes to MP4. Preview and export are the
same code with different clocks, so they cannot drift.

**Audio: two-pass, MediaComposition as muxer.** Win2D renders video-only; MediaComposition then adds
`BackgroundAudioTracks` including the loop-to-fill logic. Far less code than a single-pass
`MediaStreamSource` carrying both descriptors, at the cost of one re-encode. Keep muxing behind an
interface so single-pass can replace it if the generation loss proves visible.

### The transition set — deliberately small, and losslessly archivable

| ClickTo transition | Win2D implementation | PPTX element |
|---|---|---|
| Cut | none — just advance | *(omitted)* |
| Crossfade / dissolve | two `DrawImage`, alpha ramp | `<p:fade thruBlk="0"/>` |
| Fade through black | alpha ramp via background colour | `<p:fade thruBlk="1"/>` |
| Push (L/R/U/D) | offset transform | `<p:push dir="l"/>` |

Plus a global fade-in at the start and fade-out at the end.

Two reasons to keep it here rather than growing it:

1. **Every one maps exactly onto an OOXML element**, so transitions survive into the PPTX archive
   with no degradation. A larger set would force exotic transitions to collapse to `fade` on export,
   quietly making the archive unfaithful to the project.
2. **None needs Win2D's effect graph, masks, or geometry clipping** — a crossfade is two `DrawImage`
   calls with an alpha value. The shallowest possible use of Win2D, which materially reduces
   exposure to the work-in-progress risk below.

### The risk, and the spike that retires it

**Win2D on WinUI 3 is officially "work in progress."** This is the single largest technical risk in
the project. Spike M0 is a **gate**, not a task: Win2D → `MediaStreamSample` → `MediaTranscoder` →
playable MP4 with one crossfade, in a *packaged WinUI 3 app*. If it fails, fall back to FFmpeg with
`xfade` and accept the LGPL compliance work, because feature-completeness is the stated goal.

---

## Decision 5 — Two flat context menu items, not a ClickTo submenu

Each app registers its own top-level verb. **No `ClickTo ▸` parent menu.**

### The constraint that settles it

**Cross-package menu grouping does not exist.** Two separately installed, separately updated MSIX
packages cannot contribute items to one shared parent. Windows 11 auto-cascades multiple verbs from
*one* handler into a submenu, but there is no manifest element or API for two packages to negotiate
a shared root.

Faking it would mean each extension detecting the other's package family name, electing a host,
suppressing the non-host's top-level entry, and having the host proxy-launch its sibling by AUMID.
That breaks when the host is uninstalled, when versions mismatch, and when a future app ships that
the host has never heard of. Too much fragility for one hover.

### Why flat is also the better design

- **Vendor grouping is the wrong axis.** Users right-click thinking "I want a PDF" or "I want a
  slideshow," never "I want to do a ClickTo thing." A parent menu optimises for the brand at the
  cost of the verb, and burying the verb undercuts the product's whole premise — one right-click.
- **The overlap is smaller than it looks.** PDF shows for images; Slideshow shows for images *and*
  audio. Audio-only and mixed selections show only Slideshow. The both-visible case is pure-image
  selections, where two clear verbs is not pollution.
- **Limits are not close.** The practical cap is ~16 items per handler.

### Get the family feel without nesting

Give the two apps **icons that share a visual family** — same silhouette or frame, different accent
colour. Adjacency plus iconography reads as a family with no extra hover and no hidden verb.

Do **not** rename the menu entries to `ClickTo: PDF` / `ClickTo: Slideshow` to force alphabetical
adjacency — Explorer does not guarantee third-party ordering, so that degrades both verbs and gains
nothing reliable. Keep `Combine to PDF` / `Convert to PDF` and `Create slideshow`.

### When to revisit

At **four or more** ClickTo apps. The move then is *not* cross-package negotiation but a **suite
package**: one MSIX, one `IExplorerCommand` handler, N verbs, which Windows cascades into a
"ClickTo" submenu for free.

The catch is commercial, not technical: a suite means one Store listing, and ClickTo: PDF already
has its own listing accumulating reviews and ranking. Decide this before shipping app #3 — that is
where the path forks.

**Nothing to do now.** `ExplorerCommand.cpp` already stubs `EnumSubCommands` → `E_NOTIMPL` and
`GetFlags` → `ECF_DEFAULT`; leave those rather than deleting them as dead code, and the suite option
stays open. The `--list` launch contract is already the right shape for a future suite handler to
invoke either app.

---

## Reuse from ClickTo: PDF

| Piece | Reuse |
|---|---|
| `Img2PDF.ShellExtension` (now `ClickToPDF.ShellExtension`; whole vcxproj) | ~95% — new CLSID, exe name, titles, extension list |
| `App.xaml.cs` activation + `--list`/`--skipped` arg parsing | 100% |
| `AppLog`, `AppSettings`, `UndoStack`, `NaturalSortComparer`, `SaveFileNaming`, converters | 100% |
| `PageItem` → `SlideItem` | ~85% — add `DurationMs`, `TransitionIn` |
| `MainViewModel` grid half (load/sort/reorder/rotate/remove/undo/thumbnails) | ~70% |
| `MainWindow.xaml` shell | ~70% |
| Save progress + cancel UI | 100% — and now genuinely needed (36k frames for a 300-photo show) |
| MSIX/wapproj/signing + `docs/store/*` scaffold | ~90% |
| `Img2PDF.Core/Pdf/*` (now `ClickToPDF.Core/Pdf/*`) | 0% — replaced by the frame pipeline |

The `.list` temp-file IPC carries over unchanged and matters more here — selections of 300+ photos
are normal.

**Do not extract a shared `ClickTo.Shell` library yet.** One shipped app is a sample of one;
extracting now is speculative structure. Fork for app #2, extract when #3 shows what is actually common.

---

## Resolved since these decisions were taken

Now specified in `spec.md` rather than open:

- **Runtime arithmetic** — transition time is *additive*: `Σ(durations) + (n−1) × transition`
  (spec §4.2). Matches user expectation and PPTX `advTm` semantics. Reversible if it tests badly.
- **Audio reconciliation** — `AudioMatch` with three modes: Loop (default), Trim, FitSlides, where
  FitSlides scales durations for display only so switching back restores exact user timings (§4.2).
- **Defaults** — 4000 ms slide, 1000 ms transition, Crossfade, 1080p, Fit, black background (§4.2).
- **Push stays in the set** — four transitions, all with exact OOXML equivalents (§4.6).
- **Original resolution (2026-10-31, fixed in 1.0.2)** — 1.0.0 offered Original in Options, but
  saving with it threw `NotSupportedException`. Gavin chose to fix it rather than remove it. The
  output frame is the largest slide by pixel area, measured after its rotation (EXIF plus the
  user's), and every slide fits or fills that frame as for any other resolution. The size is
  floored to even numbers and kept to what the H.264 encoder takes, which was probed on the dev
  machine with synthetic photos, video-only and with audio:
  - The Media Foundation H.264 encoder page states no size limit; it defers to the H.264 levels,
    and its highest is 5.2. Level 5.1/5.2 allows 36,864 macroblocks a frame (4096×2304 at 16:9),
    and that is the cap: a larger frame is scaled down, keeping its shape, until it fits.
  - The hardware encoder refused any side over 4096 (4608×2592 failed) and frames of 64×48 or
    less, but took 4096×4096 and 80×60. The software encoder (tried with hardware acceleration
    off) took all of these. `MediaTranscoder` doesn't fall back to software when the hardware
    encoder refuses a size, so the cap is the stricter, standard one, plus at most 4096 a side,
    plus at least 128 a side (a smaller photo gets bands).
  - Bitrate: Windows' preset for the nearest standard size (1080p up to its area, 2160p above),
    scaled by pixel area, so 1080p and 2160p themselves are unchanged. A 2304×4096 portrait came
    out at about 36 Mbit/s rather than the ~21 the rule sets; the encoder or muxer raised it, and
    it was left alone.
  - PowerPoint takes its slide size from the same frame. With no readable slide the frame falls
    back to 1080p, and the render then reports the unreadable photo as before.
  The rule is `Core/Video/OriginalFrameSize.cs`; `Mp4Profiles` applies it for the video pass and
  the PPTX writer, and the audio mux pass takes its size from the video pass's own output.
- **Shared message bar and outside drop (2026-11-02, 1.0.2)** — the InfoBars and the red error
  text are replaced by Bureau.Common's `MessageBar`, kept in the content row so it never covers
  the runtime footer. Kinds get their own rows (`MessageKeys`): an unsupported file, a missing
  track and an unreadable track are three different causes, so they no longer share one banner
  joined by line breaks. The section headers keep their small + buttons rather than the shared
  round add button, by Gavin's choice. Drops from File Explorer reuse the shell extension's
  rules (`ExpandedSelection.Of` mirrors `ExplorerCommand.cpp`'s `Invoke`/`ExpandFolder`) so the
  two routes can't disagree; a dragged item with no path on disk is listed as
  skipped by name rather than added as a slide that could never load. Choose folder… and the
  dev folder launch still leave unsupported files out without listing them, as before.
- **PPTX save progress (2026-11-02, 1.0.2)** — the PPTX writer takes the same
  `IProgress<RenderProgress>` as the MP4 render and shares its row and Cancel. Its phases run
  the other way round (Audio, then Frames) because slide 1 carries the soundtrack, so the
  soundtrack has to exist before any slide is written; reordering that wasn't worth the risk to
  a timing tree copied byte for byte from PowerPoint.

## Next step

~~Run the M0 Win2D → `MediaStreamSource` → MP4 spike. Everything else waits on that result.~~

**M0 passed (2026-09-01).** Win2D `CanvasRenderTarget` → `MediaStreamSample` →
`MediaTranscoder` → MP4 works, confirmed two ways: unpackaged (`dotnet build` + direct exe run)
and via real MSIX activation (`shell:AppsFolder` launch of a locally-registered test package,
Developer Mode, no signing needed for a loose/unpackaged-registration deploy). Output was a
valid 1280×720 H.264 MP4, 5.0s / 150 frames, and the crossfade was visually confirmed by
extracting frames at t=1.0s/2.5s/4.0s — solid A, a genuine 50/50 alpha blend of A and B at the
midpoint, solid B.

Implementation notes for M1's real `FrameComposer`:

- `(IDirect3DSurface)canvasRenderTarget` — a plain C# cast — is sufficient to bridge Win2D's
  `CanvasRenderTarget` into `MediaStreamSample.CreateFromDirect3D11Surface`. No native D3D11
  interop helper was needed.
- `Colors`/`FontWeight`/`FontWeights` live under `Microsoft.UI.*` / `Windows.UI.Text.FontWeight`
  in this WinUI 3 + WindowsAppSDK 2.3.1 combination, not `Windows.UI.Colors` — a couple of
  namespaces drifted from older UWP samples found online.
- `MediaTranscoder.PrepareMediaStreamSourceTranscodeAsync` takes an `IRandomAccessStream`, not a
  `StorageFile` directly — open the file first (`StorageFile.OpenAsync(FileAccessMode.ReadWrite)`).
- `MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p)` defaults to exactly 1280×720,
  which is worth matching in the render target size to avoid a `CanTranscode=false` from a
  profile/descriptor size mismatch that has nothing to do with Win2D itself.
- The `WindowsAppSDKSelfContained=false` (csproj) + `WindowsAppSdkBootstrapInitialize=false`
  (publish profile) pairing ported from Img2PDF worked as documented — no packaged-activation
  issues, no CsWinRT marshalling failures.

Per spec §6, the spike code itself (`spike/M0-Win2DFeasibility/`) is throwaway and has been
deleted. M1 (`ClickToSlideshow.Cli`, the real render engine) is next.
