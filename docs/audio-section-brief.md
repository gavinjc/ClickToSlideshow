# Audio section and runtime bar — UI refinement brief

**Target:** `ClickToSlideshow.App`
**Mockup:** `docs/audio-section-mockup.png` — open it before starting; it is the intended end state
**Related:** spec §4.3 (main app), §4.2 (`AudioMatch`)

Read this alongside the mockup. Where the two disagree, this document wins — the mockup is an
HTML approximation and does not use real WinUI controls or theme brushes.

---

## 1. Why

Four problems, all visible in a screenshot of the current build with four slides and two tracks.

**1.1 The audio section reads as an afterthought.** It is a `ListView` of thin text rows pinned
to the bottom of the window, while slides get a rich tile grid. Two tracks named `tone.wav`,
both `0:04`, are visually identical — nothing on the row distinguishes them.

**1.2 The `AudioMatch` control is nowhere near the bars it governs.** Spec §4.3 requires it to
sit "directly beside it, so the consequence of each mode is visible rather than described."
In `MainWindow.xaml` the runtime grid puts the bars in column 0 at a fixed `Width="400"` and the
combo box in column 1 at the far right. On a maximised window those are over a thousand pixels
apart, and they are one concept.

**1.3 The runtime bar is undersized for what it is.** Two 400px rectangles and an 11px summary
line in the bottom-left corner. It currently reads as a status bar. Spec §4.3: *"This is the
element that makes the app feel considered — spend design effort here."* It is also the one
piece of UI no competing tool has.

**1.4 Duration badges never show in the common case.** The badge exists
(`MainWindow.xaml` ~388–397) but is bound `Visibility="{Binding DurationMs,
Converter={StaticResource BoolToVisibility}}"`, so it appears only when a slide carries a
per-slide override. A project using the project default — which is most projects — shows no
per-slide durations at all, which is exactly when the user most needs to understand where the
runtime came from.

---

## 2. What already works — do not rebuild

Check these before writing anything:

- **Audio drag-reorder is already implemented.** `AudioTracksListView` has
  `CanReorderItems="True" AllowDrop="True"`. The problem is that a bare text row gives no
  affordance, so nobody discovers it. This work makes it *visible*; it does not add it.
- **Audio ordering already round-trips.** `RenumberAudioTracks()` syncs `AudioTracks` into
  `_project.AudioTracks` and calls `RecomputeRuntime()`.
- **The duration badge, transition glyph and slide-number badge all exist**, with converters
  (`DurationBadgeConverter`, `TransitionGlyphConverter`, `SlideCountConverter`).
- **`RecomputeRuntime()` already produces `RuntimeBarWidth`, `AudioBarWidth` and
  `RuntimeSummaryText`.** The values are pre-computed on the view model deliberately — see the
  comment above the runtime grid in `MainWindow.xaml` — because `x:Bind` function bindings are
  unreliable with this file's `Window` (not `FrameworkElement`) root. **Keep that approach.**

---

## 3. Changes

### 3.1 Section headers

Add a header above the slide grid and above the audio section.

- Left: the section name in small caps, secondary foreground — **`SLIDES`** and **`AUDIO`**.
- Then a count and total: `4 · 0:19 total`, `2 tracks · 0:08 total`.
- Then a hairline rule filling the remaining width.
- Then a round **add** button at the right end — see §3.10.

**Use "Slides", not "Images".** The toolbar already reads `4 slides`; two names for one concept
in a single window reads as unfinished.

### 3.2 Audio rows become wide cards

Replace the `ListView.ItemTemplate` contents. Target height ~62px, full width of the content
area (cap the width so a maximised window does not stretch a card to 3000px — the mockup caps
around 900px).

Left to right:

| Element | Notes |
|---|---|
| Drag handle | Six-dot glyph, secondary foreground. Pure affordance — reorder already works |
| Track number | Small circular badge. **New** — see 3.5 |
| Play / pause | Circular button, ~32px. **New** — see 3.4 |
| File name | Fixed width, `TextTrimming="CharacterEllipsis"` |
| Waveform | Fills remaining width. **New** — see 3.3 |
| Duration | `FormattedDuration`, tabular figures |
| Remove | Existing `RemoveAudioTrackButton` |

**Cards, not square tiles.** A square tile earns its area because a thumbnail carries
information; audio has nothing to show in that shape and would render as a row of identical
music-note glyphs. The waveform is audio's thumbnail, and it needs width, not height.

### 3.3 Waveform

New work in `ClickToSlideshow.Core`: decode a track to a fixed-length peaks array (200–400
buckets is ample at this size) and expose it on `AudioTrack` or alongside it.

- **Compute off the UI thread, after the track is added.** A 4-second WAV is instant; a 60 MB
  FLAC is not. The card must render immediately with a flat placeholder and fill in when ready.
- Render as a `Path`/`Polygon` or an `ItemsControl` of thin rectangles — whichever profiles
  better with a dozen cards on screen.
- A track that fails to decode keeps the placeholder. It is decoration; never let it fail a save.

### 3.4 Per-track play / pause

Audio rows currently have no preview at all — only Remove. This is new, not a replacement.

- Plays that track alone, from the start, through `MediaPlayer`.
- Glyph toggles to pause while playing.
- **Only one track plays at a time**, and starting the slideshow (`Play`) stops any track preview.
- Stop playback on window close and on removal of the playing track.

### 3.5 `AudioTrackItem` must become observable

Currently a plain class, with a comment explaining the choice: `AudioTrack` is immutable, so
nothing changes after construction. **That reasoning no longer holds** — three new properties
change at runtime:

- `TrackNumber` — changes on reorder (`RenumberAudioTracks` must now set it, as it does not today)
- `IsPlaying` — changes on preview
- `Peaks` / `HasPeaks` — arrives asynchronously

Convert it to `ObservableObject` and update that comment to say why, rather than deleting it.

### 3.6 Runtime bar becomes a footer

Full width of the window, above the bottom edge, visually separated by a top border and a
slightly raised background.

```
Slideshow  ████████████████████████████████████████  0:19
Audio      ████████████████                          0:08
           Audio is 11 seconds short — it will repeat to fill
           the slideshow, then fade out.
```

- Both bars share one axis scaled to `max(runtime, audioLength)`, so the shorter one is visibly
  short. This is the entire point of the control.
- Row labels on the left, times on the right in tabular figures.
- Distinct fills: accent for the slideshow, a second colour for audio. See §4 on colour.
- The summary line spells out the *consequence* of the current `AudioMatch` mode in plain
  English, not just the two numbers. Extend `RuntimeSummaryText` in `RecomputeRuntime()`.

**Implementation note — this is the fiddly part.** The bars are currently fixed at
`Width="400"` with widths pre-computed on the view model. A full-width bar has to respond to
window size while keeping that pre-computed approach (do not switch to `x:Bind` function
bindings — see §2). Feed the available width into the view model from a `SizeChanged` handler on
the footer grid and have `RecomputeRuntime()` take it into account. Debounce it; resizing fires
continuously.

### 3.7 Move `AudioMatch` next to the bars

Into the same footer, immediately right of the bars, keeping its "If lengths differ" label.
This is spec §4.3's stated intent and it has drifted.

### 3.8 Duration badge always visible

Change the badge's `Visibility` so it shows the *effective* duration — the per-slide override
when one exists, otherwise the project default. Keep a visual distinction between the two
(a subtle accent outline on overridden slides is enough) so "this one is different" is still
readable at a glance.

### 3.9 Audio flows under slides, not pinned to the bottom

Both sections top-align and stack, with empty space falling below both. Pinning audio to the
window bottom means a four-slide project has a large void between two things the user is
actively relating to each other.

The runtime footer stays pinned to the bottom — that one *should* always be visible.

### 3.10 Add buttons — one per section

Adopt ClickTo: PDF's add affordance: a filled accent circle with a `+`. One in the **Slides**
header, one in the **Audio** header, at the right end of each section's rule.

**There is currently no way to add images from inside the app at all.** The toolbar's `Open…`
is `LoadProjectAsync` — it picks a `.json` or `.pptx` project, per its own tooltip ("Open a
saved project"). So the Slides `+` is a missing feature, not a restyled button. Spec §4.3 calls
for adding files from inside the app; it was never built.

- **Slides `+`** → `FileOpenPicker`, `PickMultipleFilesAsync`, filtered to the supported image
  extensions, appended through the same path the shell-extension launch uses. Must respect the
  existing skipped-file reporting and per-tile error handling.
- **Audio `+`** → the existing `AddAudioButton_Click` body, unchanged. Only the button's
  presentation moves. Note its picker currently filters `.mp3 .wav .flac` only, while spec §4.4
  lists `.m4a .aac .wma .ogg .opus` as transcode-on-import formats — out of scope here, but
  worth a separate issue.

**Why two anchored buttons rather than one floating FAB.** ClickTo: PDF's single floating
button works because there is exactly one thing to add. Here there are two, so a floating button
cannot say which it means without a menu — and the bottom-right corner it would occupy now holds
the runtime footer and the `AudioMatch` combo, which it would overlap. Anchoring each button to
its own section removes the ambiguity and the collision.

Size ~30px so it sits level with the header text without dominating it; ClickTo: PDF's is larger
because it floats over empty space.

**Accessibility:** a bare `+` has no accessible name. Both buttons need
`AutomationProperties.Name` and a `ToolTipService.ToolTip` — "Add photos" and "Add audio track"
— in `Resources.resw`. The existing `AddAudioButton` entries already have the automation name;
keep it and drop the `.Content` string, which is no longer displayed.

---

## 4. Decide before building

- **Audio accent colour.** The mockup uses amber against the slides' accent blue. That reads
  well, but the app already has the teal/amber brand from the icon, so a third colour needs
  checking in the real theme, in both light and dark. Using the existing `TextFillColorSecondary`
  for the audio bar is the conservative alternative — less legible, no new colour.
- **Card width cap.** 900px in the mockup. Check against a maximised 4K window.
- **Waveform density.** Bucket count is a legibility/performance tradeoff; tune with a dozen
  cards on screen, not one.

---

## 5. Acceptance criteria

- [ ] Section headers read `SLIDES` and `AUDIO` with live counts and totals
- [ ] Audio cards show drag handle, number, play/pause, name, waveform, duration, remove
- [ ] Dragging an audio card reorders it, and `TrackNumber` updates on drop
- [ ] Waveform appears without blocking the UI when a large file is added
- [ ] A track that fails to decode shows a placeholder and never blocks a save
- [ ] Play previews one track; a second play stops the first; `Play` (slideshow) stops previews
- [ ] Runtime footer spans the window and both bars share one axis
- [ ] Resizing the window rescales the bars without flicker or lag
- [ ] The summary line states the consequence of the current `AudioMatch` mode in plain English
- [ ] `AudioMatch` sits adjacent to the bars
- [ ] Every slide shows a duration badge; overridden slides remain visually distinct
- [ ] With four slides there is no large void between the slides and the audio section
- [ ] Each section header has an accent `+` button that adds only that section's file type
- [ ] The Slides `+` adds images, reports skipped files, and does not disturb existing order
- [ ] Neither `+` overlaps the runtime footer at any window size
- [ ] Both `+` buttons have an accessible name and a tooltip
- [ ] `Open…` still opens a saved project and is not confused with adding images
- [ ] Existing project files still load and save with audio order preserved

---

## 6. Out of scope

- No waveform scrubbing or in-place trimming — this is not a video editor (spec §2 non-goals)
- No per-track volume or fade controls
- No change to `AudioMatch` semantics; this is presentation only
- No change to the slide tile layout beyond §3.8
