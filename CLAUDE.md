# ClickToSlideshow

## Project Overview
A single-purpose Windows 11 utility: select image files (and optionally audio) in File
Explorer, right-click **Create slideshow**, reorder as thumbnails in a grid with per-slide
durations and an audio strip, then **Play** fullscreen or **Save** as MP4, PPTX, or a JSON
project file. Not a video editor — no trimming, no timeline, no titles. See
`docs/clickto-slideshow-spec.md` (authoritative spec) and `docs/clickto-slideshow-decisions.md`
(decision record) for full detail.

**Sibling project, heavy reuse:** `ClickToPDF` (ClickTo: PDF) is the sibling app
this was forked from. Large parts of the shell extension, app shell, and infrastructure
(`AppLog`, `AppSettings`, `UndoStack`, `NaturalSortComparer`, save-progress UI, MSIX/signing
setup) port over with minor changes — see the spec's "Reuse from ClickTo: PDF" table before
writing anything from scratch. Check ClickToPDF's code first; a near-duplicate written from
scratch here is a defect, not a neutral choice.

## Shared code: Bureau.Common (git submodule)
The About dialog, cross-promo panel, rating prompt, title-bar theming, surface palette and
`IconButtonStyle` come from the shared
[Bureau.Common](https://github.com/gavinjc/Bureau.Common) repo, mounted
at `external/Bureau.Common` and pinned to one commit (adopted in 1.0.1).
- Clone with `git clone --recurse-submodules`. After a pull that moves the pin, run
  `git submodule update --init`. Without it, `external/Bureau.Common` is empty and the build
  fails on the missing project.
- Change shared chrome **in Bureau.Common**: commit and push there first, then bump the pin
  here (`git -C external/Bureau.Common pull`, then commit the submodule change). Don't edit
  the files under `external/` as if they were this repo's.
- Read `external/Bureau.Common/CLAUDE.md` before touching it. Its rules include: no network
  calls, its own resource map for strings, and the WindowsAppSDK version kept in step with
  this app's.
- App-specific resources stay in this app's `App.xaml`: `AddSectionButtonStyle` and the
  runtime bar's `AppAudioAccentBrush`.

## The ClickTo family
This app is one of a family. The family context below is loaded from the submodule, as of its
pinned commit. It says where the shared conventions and lessons live; family-wide lessons go
there, not into this repo's notes.

(The shared family-context file is internal and not part of this snapshot.)

**Naming:** repo, solution (`ClickToSlideshow.sln`), all projects
(`ClickToSlideshow.App`/`.Core`/`.Cli`/`.ShellExtension`/`.Package`), and namespaces are
`ClickToSlideshow.*`. The product/store-facing name is **ClickTo: Slideshow**: window title,
MSIX manifest `DisplayName`, and all `docs/store/*` submission copy. The spec's original
proposed codename `Img2Show` has been fully renamed to `ClickToSlideshow` throughout
`docs/clickto-slideshow-spec.md` and `docs/clickto-slideshow-decisions.md` (2026-09-01) — no
remaining self-references to the old codename.

## Build
`ClickToSlideshow.sln` exists; layout mirrors ClickToPDF exactly (spec §3):

```
ClickToSlideshow.sln
├── ClickToSlideshow.Core/             C# / .NET 8 — timeline model, frame composer, renderers
├── ClickToSlideshow.Cli/              C# console — headless render, the M1 proving ground
├── ClickToSlideshow.App/              C# / .NET 8 / WinUI 3 — editor window + fullscreen player
├── ClickToSlideshow.ShellExtension/   C++ (WRL) DLL — IExplorerCommand, launches the app
└── ClickToSlideshow.Package/          Windows Application Packaging Project → MSIX
```

- `.Core` / `.Cli` / `.App` (csproj) — build via `dotnet build` / `dotnet run`. `.App`/`.Core`
  need `-r win-x64` (plain `dotnet build`/`restore` regenerates `.Core`'s restore output
  without the target the wapproj build needs — see the M7 build-order gotcha in
  `docs/CURRENT_APPLICATION.md` if the wapproj then fails `NETSDK1047`).
- `.ShellExtension` (vcxproj, native WRL COM DLL) — needs VS2022's C++ desktop workload;
  build via VS2022's own MSBuild.exe, not `dotnet build`.
- `.Package` (wapproj, MSIX packaging) — needs the "Universal Windows Platform development"
  VS2022 workload; also built via VS2022's MSBuild.exe. Signing needs a local self-signed dev
  cert, same process as ClickToPDF (see that repo's CLAUDE.md and `docs/store/*`); a real one has
  been generated and trusted machine-wide on the dev machine (not committed).

**M0 (Win2D → `MediaStreamSample` → `MediaTranscoder` → MP4) passed 2026-09-01** — see
`docs/clickto-slideshow-decisions.md`'s "Next step" entry. M1 through M7 are all built and
verified; **v1.0.0 is live in the Microsoft Store** (Store ID `9PLKC14MGC3N`, submitted
2026-09-26). See `docs/CURRENT_APPLICATION.md` for what's actually shipped and its Known Issues
for open gaps.

**Lint / duplication:**
```
npx jscpd --min-lines 8 --min-tokens 60 --reporters console,json ./src
lizard -C 12 -L 80 -a 5 --warnings_only .
```
C# analyzer severities live in `.editorconfig`; C++ checks in `.clang-tidy`.

## Tests
`dotnet test` over xunit projects under `tests/` (`ClickToSlideshow.Core.Tests`,
`ClickToSlideshow.App.Tests`), mirroring ClickToPDF's layout — 111 and 87 tests respectively as of
2026-11-02. Build `.App`/`.Core` for `win-x64` first (`dotnet build <project> -r win-x64`) or
`dotnet test` will fail restore for the WinUI-dependent projects.

## Architecture
See `docs/CURRENT_APPLICATION.md` for a living summary of what's actually built (Key Files,
external dependencies, Known Issues) and `docs/clickto-slideshow-spec.md` for full design
detail — read both before starting any implementation work.

Five projects, one MSIX package (see Build above for the layout). Build order follows spec §6
milestones: M0 Win2D feasibility spike (gate, throwaway) → M1 core render engine (CLI, no UI)
→ M2 WinUI 3 editor shell → M3 fullscreen playback → M4 save flows (MP4/JSON) → M5 PPTX writer
→ M6 shell extension → M7 MSIX packaging → M8 Store readiness. All of M0–M8 are done: the app
passed WACK and certification and is live. `docs/store/submission-checklist.md` records the
submission; every item in it is closed, the spec §7 acceptance walk last (2026-10-02). Each milestone was
independently runnable as built.

## Progress

Milestones and tasks are tracked in `docs/PROGRESS.md` (format:
`~/.claude/init-shared/progress-format.md`). Keep it current as you work: mark the task you
start `status: active`, tick finished tasks and add `done:` with today's date, and add tasks
you discover.

## Code standards

General code standards load from user scope (`~/.claude/CLAUDE.md` imports
`~/.claude/init-shared/code-standards.md`). The project-specific additions:

**Search before you write.** Grep for an existing helper, type, constant, or utility that
already does this before adding a new one — including in the sibling `ClickToPDF` repo. Adding a
near-duplicate is a defect, not a neutral choice. If you looked and found nothing, that is
worth one line in your summary.

**Copy-paste duplication.** `npx jscpd .` uses this repo's `.jscpd.json`.

## Notes
- Never modify, move, or delete the user's source image/audio files — read-only access only.
- No telemetry, no network calls, no cloud, no accounts, no auto-updater. The app must work
  identically offline, forever.
- Output dimensions for MP4 render must be even numbers — some hardware encoders reject odd
  width/height and the failure is opaque (spec §4.5, §10).
- `MediaComposition` is a muxer only, never the render engine — it structurally cannot produce
  transitions (spec §4.5). Don't let it creep back into the render path.
- Do not add the Graphics Capture manifest capability — frames are synthesised, not captured
  (spec §4.5, §7).
- Full acceptance criteria: `docs/clickto-slideshow-spec.md` §7.

## GUI tests

Anything that shows a window on the desktop (the app under test, a GUI test run) must be launched through
`powershell -NoProfile -File "run-gui-test.ps1" -Project ClickToSlideshow -Command "<command>"`.
It holds the desktop lock while the command runs, and waits if a hub worker is using the desktop.
