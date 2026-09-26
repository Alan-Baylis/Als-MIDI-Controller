# Launchpad Studio — DESIGN.md

**Version 1.0** · Unity 6000.4.1f1 · URP · TextMeshPro · DryWetMIDI
Reference hardware: Novation Launchpad Pro MK3. Also driven: Launchpad X, Launchpad
Mini MK3, Launchpad Pro (MK2), Launchpad MK2, and the red/green Launchpad / S / Mini.
Namespace: `LaunchpadStudio` (except `MidiPadInput`, which is global by accident of history)

A sample player for Novation Launchpads. Drop any WAV, OGG, AIFF or MP3 onto a pad
and it plays — on screen, on the hardware, and in time with everything else.

**Who it is for.** The musician who has a folder of random samples and no sample pack,
no DAW and no patience for a setup wizard. Every feature is measured against that
person: if it needs prior knowledge, a correctly-prepared file, or a manual, it is
wrong.

---

## §1 Principles

1. **One source of truth per fact.** BPM lives on `Manager`. Pad content lives on
   `PadModel`. Selection lives on `SelectionService`. The clip for a path lives in
   `ClipCache`. The current layout's name, path and dirty state live on
   `PadLayoutService`. Anything that needs one of these reads it; nothing keeps a copy.
2. **Core never knows about the UI. Views never write audio.** `Core/` and `Audio/`
   raise events; `View/` subscribes. A view writes to the model through
   `PadModel.Set`, or asks `PadController`. No view has ever touched an `AudioSource`.
3. **Every user-facing message goes through `NotificationService` AND is echoed to the
   console** — and from there to `Logs/latest.log`. The ticker is a convenience and
   never the only record. Code with no reference uses the static `NotificationService.Say`.
4. **A failure explains itself.** FMOD says "Unsupported file or audio format" and
   nothing else; `AudioFileProbe` reads the header and says *which*. A message that
   does not tell you what to do next is not finished.
5. **Nothing an animation does may alter a session.** Screensaver and lightshow write
   to `PadOverlay`, never to `PadModel`. Stopping is one `ClearAll`.
6. **Comments say WHY.** The code already says what. A comment that has gone stale is
   a bug: fix it in the same change that made it wrong.
7. **The hardware is a peripheral, never part of the session.** Unplugging the
   Launchpad closes a port and nothing else. Plugging it back in repaints from the model.
8. **Degrade, never disappear.** No clock → play now and say so. No dialog in the
   scene → the same choice twice. No glyph in the font → an ASCII stand-in.
9. **Balance every `Acquire` with a `Release`.** `PadController` owns that balance for
   pads (`_held[]`); `LibraryView` owns it for the preview voice (`_previewKey`).
10. **Poll cheaply rather than keep a flag that will lie.** The unsaved-changes `*` is
    a fingerprint comparison (`PadSerialization.Signature`), not a dirty flag.
11. **One rule per action, however many routes lead to it.** Every layout load goes
    through `LayoutLoadGuard`; every "what should it be called?" through
    `LayoutSavePrompt`; every "save first?" through `LayoutLoadGuard.SaveCurrentThen`;
    every file replacement through `AtomicFile`; every pad↔record conversion through
    `PadSerialization`. Each exists because two copies of the rule once disagreed.
12. **Paths are stored portable.** A folder the user picks inside the app folder is
    stored relative ("Samples"); inside StreamingAssets as "StreamingAssets/…";
    anywhere else absolute. One pair of functions does this
    (`Manager.ToPortablePath` / `AbsoluteFrom`), so moving the app folder breaks nothing.
13. **The words on screen are the musician's.** The user sees *samples*, not clips.
    Code keeps Unity's `AudioClip` vocabulary (`ClipCache`, `pad.clip`), which is
    correct there and invisible to users.

---

## §2 Layers

```
Core/     Manager, PadModel, PadState, SelectionService, Metronome, NoteGrid,
          NotificationService, NoticePalette, PadPalette(+Asset), PadColour,
          PadOverlay, PadCategory, LogFileWriter
Audio/    AudioFileLoader, AudioFileProbe, ClipAnalysis, ClipCache, Rack, RackSettings
Control/  PadController                                   ← the only orchestrator
IO/       AtomicFile, LibraryScanner, SessionFile/Service, PadLayoutFile/Service,
          PadSerialization
Midi/     MidiPadInput (the one port owner), PadDeviceProfile + NovationRgbProfile,
          NovationMk2Profile, NovationLegacyProfile, MidiDeviceCatalog, LedWrite
View/     everything on screen, PadLedView + MetronomeLedView (the hardware "screen"),
          the three modals (ConfirmDialogView, FileBrowserView, ColourPickerView),
          and the static helpers LayoutLoadGuard, LayoutSavePrompt, TextGlyphs
Editor/   HierarchyDump, SceneWiringCheck, CopyFoldersOnBuild
```

Dependency direction is strictly downward: `View → Control → Core/Audio/IO/Midi`.
`LayoutLoadGuard` and `LayoutSavePrompt` live in `View/` because they ask the user
questions; the file logic they call is all in `PadLayoutService`.

---

## §3 Ownership

| Fact | Owner | Notes |
|---|---|---|
| BPM, volume, V-Sync, folders, debug, palette, device choice | `Manager` | serialised fields ARE the startup defaults |
| 64 pad states | `PadModel` | only writer is `Set/Clear/Move/Copy/Swap` |
| Which pad is being edited | `SelectionService` | |
| Beat events | `Metronome` | driven by `AudioSettings.dspTime` |
| Decoded clips | `ClipCache` | refcounted, deduplicated, lingering eviction |
| Lead-in (trim) offset | `ClipAnalysis` via `PadController` | per FILE, runtime only |
| 64 voices | `Rack` | generated from `NoteGrid` |
| Clips on pads, triggering, queues | `PadController` | the ONLY class that writes `pad.clip` |
| The MIDI ports + current device profile | `MidiPadInput` | exclusive on Windows |
| What a device's bytes mean | `PadDeviceProfile` subclasses | listed in `MidiDeviceCatalog` |
| Current layout name, path, dirty state | `PadLayoutService` | `CurrentLayoutName`, `CurrentLayoutPath`, `IsDirty` |
| Animation colour layer | `PadOverlay` | one writer at a time |
| Modal questions | `ConfirmDialogView` | the only one; works in a build |
| File/folder picking | `FileBrowserView` | the only one; works in a build |
| Runtime colour choice | `ColourPickerView` | the only one; works in a build |
| The log file | `LogFileWriter` | `latest.log` + `previous.log` |

Single-instance components are enforced by `SceneWiringCheck.RequireSingle<T>()`.

---

## §4 Audio pipeline

```
path → AudioFileLoader.Load (UnityWebRequestMultimedia, file:// via Uri)
     → AudioFileProbe on failure (says WHICH format problem)
     → ClipCache entry (key = normalised absolute path, case-insensitive)
     → ClipAnalysis: lead-in scan, once per file
     → PadController.AssignClipAsync (token per pad, refcount, load state)
     → PadModel.Set(pad.clip)
     → PadController.ApplyToVoice → AudioSource
```

**ClipCache invariants** — one decode per file; concurrent requests share it; a failed
decode is **not** cached (the user may fix the file and retry); on failure no
reference is taken; eviction after `lingerSeconds` (30) unreferenced or over
`maxIdleClips` (32); `Retain(key)` for `CopyPad`.

**Formats.** WAV (PCM / IEEE float / extensible), OGG, AIFF, **MP3** — all through
Unity's own decoder (`AudioType.MPEG` for MP3; NLayer was not needed). Four lists must
agree: `AudioFileLoader.GuessAudioType`, `LibraryScanner.Extensions`, the probe, and
`PadInspectorView`'s editor-only fallback picker.

MP3 caveats: an encoder delay at the start (removed by the trim) and a few ms of
padding at the end — a free-running `AudioSource.loop` pad will gap slightly at the
wrap. Grid-synced loops are unaffected. WAV/OGG remain the choice for seamless loops.

**Leading silence (done — was roadmap item 2).** `ClipAnalysis` walks the first
`maxTrimSeconds` (1 s) for the first sample above `silenceThresholdDb` (−48 dB), backs
off `trimBackoffSeconds`, and the pad starts there (`voice.timeSamples`). A trim is a
playback OFFSET, never an edit: the clip is shared by every pad that uses it.

**Memory.** Every clip is decoded to PCM in memory; a 5-minute stereo file is ~50 MB
whatever its format. Fine for samples, heavy for whole songs on 64 pads.

---

## §5 Timing and transport

`Metronome` accumulates **phase**, not absolute next-beat times, so a BPM change
applies on the next frame with no time lost or repeated. Long frames are clamped by
`maxCatchUpSeconds` so an alt-tab does not fire a burst of beats.

Deferred to the clock (all cancellable by pressing again, all falling back to
"immediately" with a throttled, cause-naming warning when there is no clock):

1. **Quantised start** — `startBeat 0` = next beat (freestyle), `1` = downbeat.
2. **Stop at bar end** — pressing a sounding loop; `stopOnDownbeatOnly` waits for beat 1.
3. **Loop re-sync** — quantised loops re-trigger on the grid instead of using
   `AudioSource.loop`, so arbitrary-length samples sit in time.

A second press on a sounding **one-shot** follows `OneShotRepress`: `Restart`
(default, quantised like the first press), `Stop`, or `Ignore`.

`StopAllVoices` is the panic button: clears queues, silences voices, touches neither
model nor selection.

The player keeps running when the window loses focus (`Application.runInBackground`,
set in `Manager.Awake`) — a Launchpad is often played while another app has focus.

---

## §6 Pad colour

Roles live on `PadPaletteAsset` (seven `PadColour` assets). `PadPalette` is the thin
front door `PadView` (screen) and `PadLedView` (hardware) both call.

**Priority, highest first:** overlay → trigger flash → selected (`editing`) → no state
(`empty`) → debug test tone (`hasClip` at half alpha) → `loadState` (`Ready` → pad's
custom colour or `loaded`; `Loading`; `Missing`/`Failed` → `missing`).

On top: **pulse** (the pad's own colour, brighter, on every clip start including loop
wraps). Flash means "pressed"; pulse means "sounding".

Labels use `PadPalette.LabelColourFor` — inverse colour when it contrasts (≥0.35
luminance difference), otherwise black or white. A missing pad keeps the caption of
the file it is looking for.

**Runtime colour picker (done).** `ColourPickerView` — HSV sliders, hex field, live
preview on screen and hardware, Cancel restores. Replaces the editor-only reflection
hack in `PadPaletteView`, and is the only picker that exists in a build.

For the hardware, colours pass through the device profile: full RGB (MK3 family),
6-bit RGB (MK2 family), or 4 levels each of red and green (legacy — blue is folded
half into each so a purple "missing" pad reads as dim amber, not as off).

---

## §7 MIDI and the hardware

**Windows MIDI ports are exclusive, process-wide.** `MidiPadInput` is the only class
that opens one; `PadLedView` and `MetronomeLedView` ask it to send.

**The logical scheme** is the Pro MK3 Programmer layout: grid 11–88, top row 91–98,
right column x9. `NoteGrid`, the model, session files and every LED writer speak it.
A **device profile** translates to and from the device (§7.1). Callers send
`LedWrite(index, Color32)` batches and never know which device is attached.

### §7.1 Device profiles

| Profile | Colour | Handshake | Remap |
|---|---|---|---|
| Launchpad Pro MK3 / X / Mini MK3 (`NovationRgbProfile`) | RGB 0–127 | `0E 01` | none |
| Launchpad Pro (MK2) (`NovationMk2Profile`) | RGB 0–63 | `21 01`, `2C 03` | none |
| Launchpad MK2 (`NovationMk2Profile`) | RGB 0–63 | `22 00` | top row ↔ CC 104–111 |
| Launchpad / S / Mini (`NovationLegacyProfile`) | red/green 4×4 | CC 0 = 0 | X-Y notes from the top; top row ↔ CC 104–111 |

Verified on hardware: Pro MK3. The others are written from Novation's programmer
references; a wrong byte is a one-line fix in `MidiDeviceCatalog`.

- **Choice:** `Manager.MidiDeviceId` ("" = auto-detect), saved as `midiProfile` in the
  session. `MidiDeviceView` (Settings page) is the dropdown and status line.
- **Auto-detect** walks `MidiDeviceCatalog.All` in order — most specific names first,
  the catch-all legacy profile last. **Order matters.**
- A profile is chosen only on a FRESH connect (both halves closed), never while
  repairing a half-open link. Restoring the device you are already connected to does
  not reconnect.
- **One device at a time** in 1.0.

### §7.2 Connection behaviour (unchanged)

Programmer-mode SysEx is sent a few frames after the port opens and retried. LED
writes are coalesced into one batch per `LateUpdate`. Hot-plug is a state machine fed
by `DevicesWatcher` **and** a backstop rescan. `PumpRepair` retries the half that
failed and raises `ConnectionChanged(true)` so views repaint. Consecutive send
failures count as a yanked cable: one notice, not a flood. **Banned:** `MidiDeviceProbe`.

---

## §8 Files and folders

### §8.1 Where things live

| Folder / file | Default | Editor | Windows build | Created at startup |
|---|---|---|---|---|
| Samples | `Manager.libraryRoot = "Samples"` | `<project>/Samples` | `<exe folder>/Samples` | yes, when relative |
| Layouts | `Manager.layoutsFolder = "Layouts"` | `<project>/Layouts` | `<exe folder>/Layouts` | yes |
| Logs | `LogFileWriter.folderName = "Logs"` | `<project>/Logs` | `<exe folder>/Logs` | yes (falls back to AppData if unwritable) |
| session.json | — | `<project>/session.json` | `%USERPROFILE%/AppData/LocalLow/<Company>/<Product>/` | written on quit |

- **Resolution:** `Manager.AbsoluteFrom` — absolute as given; `StreamingAssets/…` →
  the real StreamingAssets folder; anything else → next to the project (editor) or the
  exe (build).
- **Creation:** `Manager.EnsureStartupFolders()` in `Awake` and again after the session
  restores paths. An ABSOLUTE library root that is missing is deliberately not created:
  an empty stand-in would hide the unplugged drive that "not found" exists to report.
- **Shipping:** `CopyFoldersOnBuild` (editor, post-build) copies `Samples` from the
  project root next to the exe. It copies over, never deletes. Add `"Layouts"` to its
  list to ship starter kits.
- **StreamingAssets is not for user content** (§9.13). Nothing is ever written there.
- The session's location follows Player Settings' **Company Name** and **Product
  Name**. Changing either after release orphans every user's session.

### §8.2 Formats

All JSON via `JsonUtility`: no properties, no dictionaries, no nullables; colours as
`RRGGBBAA` hex. Every field has a sane default, so an old file loads in a new build.

| File | Version | Contains |
|---|---|---|
| `session.json` | 2 | settings (incl. `layoutsFolder`, `midiProfile`, `lastLayoutName`, `lastLayoutPath`), notice colours, palette roles, pads |
| `*.layout.json` | 1 | the 64 pads and nothing else — no BPM, palette or display settings |

`SessionSettings.lightshowsFolder` and `stylesFolder` are reserved for v2 and are not
yet read or written. Note that `Manager` calls the first one `animationsFolder` — see §14.

Layout paths are stored **relative to `libraryRoot`** when inside it (explicit
`relative` flag) and resolved against the *current* root on load. `missing` and
`unresolved` are reported separately; the path is always kept.

### §8.3 Writing safely

`AtomicFile` is the one way a file is replaced: `File.Replace` swaps in a single
filesystem operation and keeps the old contents as `.bak`. `SessionService` recovers
from the `.bak` when the main file is corrupt (`recoverFromBackup`), keeps the corrupt
file for inspection (`keepCorruptFiles`), and adopts an interrupted `.tmp`
(`adoptInterruptedSaves`).

### §8.4 Layout commands

- **Save** writes over `CurrentLayoutPath` with no questions. A never-saved grid asks
  for a name (`LayoutSavePrompt`).
- **Save As** (Layouts browser) asks for a name and saves into the folder the browser
  is SHOWING, subfolders included. An existing different file → "Replace?" (old one
  kept as `.bak`). An empty name keeps the dialog open with the reason.
- **Load** (row click or Load button) goes through `LayoutLoadGuard`: a dirty grid asks
  Save / Don't Save / Cancel. Save runs first and the load only happens if it succeeded.
- **Quit** (button, window X, Alt+F4) goes through `AppExit`: a dirty, non-empty grid
  asks the same three questions via `SaveCurrentThen`. The session itself is always
  saved in `OnApplicationQuit`.
- A file renamed in Explorer loads under its NEW name — the file is the layout.
- The current row in the browser is marked by PATH (`currentMarker`, default `►`).

---

## §9 Unity traps (each one has cost us a session)

1. **A component on a hidden object stops receiving events.** Views that show windows
   sit on always-active objects and *point at* the window. They log an error if
   `window == gameObject`.
2. **Always-active components** (`SceneWiringCheck.RequireAlwaysActive`): `Metronome`,
   `ClipCache`, `PadController`, `PadModel`, `NotificationService`, `PadOverlay`,
   `SessionService`, `LogFileWriter`, `ManagerView`, `LayoutControls`,
   `ConfirmDialogView`, `ColourPickerView`.
3. **UnityEvent → deleted method** shows `<Missing>` and fails silently.
4. **Buttons are wired in `Awake`; leave OnClick lists EMPTY.**
5. **`SetValueWithoutNotify` / `SetIsOnWithoutNotify` / `SetTextWithoutNotify`** for
   model → widget, always.
6. **`Selectable` ColorTint multiplies `Image.color`.**
7. **An `AudioSource` is silenced when its GameObject deactivates.**
8. **Statics survive play-mode exit** (`AudioListener.volume`,
   `QualitySettings.vSyncCount`, and our own statics — reset them with
   `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]`).
9. **TMP lays out lazily** — `ForceMeshUpdate()` before measuring.
10. **Destroying a child of a layout group** — `SetParent(null, false)` first.
11. **`FindAnyObjectByType` skips inactive objects** — use `FindObjectsInactive.Include`.
12. **`refreshRate` (int) rounds inconsistently** — use `refreshRateRatio`.
13. **Nothing inside `StreamingAssets` is imported.** A PNG there is never a Sprite,
    a TTF never a font, a WAV never an AudioClip. Inspector-referenced files belong in
    ordinary folders (`Assets/Files`); StreamingAssets is only for files opened by path.
    Move assets in Unity's Project window, never in Explorer, so `.meta` files travel.
14. **TMP draws a box for any character its font lacks** — silently. Code-written text
    goes through `TextGlyphs.Fit`. Text typed into the Inspector is not checked: keep
    it ASCII, or check it in the font asset.
15. **A script's file name must match its class name** (`BpmLabel.cs`, not
    `BmpLabel.cs`). Rename in the Project window so the `.meta` GUID is kept.
16. **Renaming a serialised field loses its value** unless it carries
    `[FormerlySerializedAs("oldName")]` (used when Refresh became Save As).
17. **`Application.wantsToQuit` is not raised when you stop Play in the editor.** Test
    the quit question in a build.
18. **Focusing an input field in the frame it opens** loses the focus to the click, or
    feeds the previous dialog's Return into it — `ConfirmDialogView` focuses a frame late.
19. **An unfocused standalone player pauses** unless `Application.runInBackground`.

---

## §10 Editor tooling

**`Tools/Hierarchy Dump/`** — the project as readable text. The combined dump has four
parts separated by a line reading exactly `***** Break *****`: a note, all source
(sorted by path), the full scene, and the wiring-only view. Identical sibling runs are
summarised after the second.

**`Tools/Scene Wiring Check/`** — empty slots, broken references, missing scripts,
UnityEvents aimed at deleted methods, banned components, duplicate/orphaned
`PadView`s, bulb counts vs `beatsPerBar`, single-instance and always-active rules.
Optional slots go in `KnownOptional` or carry "Optional" in their `[Tooltip]`.
**Keep it clean**: a check that cries wolf gets ignored.

**`CopyFoldersOnBuild`** — post-build step that ships project-root folders (`Samples`)
next to the executable.

**Before every build:** run the Wiring Check, then test the build with no
`session.json` in AppData (a true first run).

---

## §11 Status — version 1.0

Working:

- 64-pad grid, mouse and MIDI, velocity curve with floor; one-shot re-press modes.
- Six Launchpad models through device profiles; auto-detect; hot-plug with repaint;
  device choice saved with the session.
- Refcounted clip cache; no leaks on assign / clear / move / copy / swap / teardown.
- WAV, OGG, AIFF and MP3; header probe that explains failures; automatic
  leading-silence trim.
- Quantised starts, stop-at-bar-end, grid-synced loops.
- Per-pad loop, volume, pitch, label, category guess, custom colour.
- Pad captions in inverse colour; runtime colour picker for palette roles.
- Hardware LED mirror: state colours, flash, pulse, metronome row.
- Notification ticker + log window (filters, copy, log folder) + log file on disk.
- Session save/load with atomic writes, backup recovery and portable paths.
- Layouts: browser with instant load, Save, Save As, New, Load, unsaved `*`, current
  row marker, unsaved-changes guard on load and on quit.
- In-app file browser (open / save / pick folder with dimmed file preview), confirm
  dialog with optional text entry, colour picker — all working in a build.
- Samples / Layouts / Logs next to the exe, created on first run.
- Screensaver on the overlay layer, held off by playback, queues, previews and dialogs.
- Feature Notes (post-it hints) and a startup greeting.

**Known limits in 1.0:** one device at a time; Windows-first (on macOS the app
folders resolve inside the `.app` bundle); no drag-and-drop; no undo; the top row and
side buttons are mapped but do nothing yet; non-Latin file names need a fallback font.

---

## §12 Roadmap — version 2.0

In this order: drag-and-drop first (smallest, most asked-for), then styles (touches
every view, so better done before more views exist), then lightshows (the biggest,
and it benefits from both).

### §12.1 Drag-and-drop

Everything underneath exists: `LibraryItemView.Entry`, `PadController.AssignClipAsync
/ MovePad / CopyPad / SwapPads`, all refcount-balanced.

- **Library row → pad:** `LibraryItemView` implements `IBeginDragHandler`,
  `IDragHandler`, `IEndDragHandler`. A `DragGhost` on a top-level overlay canvas shows
  the sample name (through `TextGlyphs`). `PadView` implements `IDropHandler` and calls
  `AssignClipAsync`. Right-click assign stays as it is.
- **Pad → pad:** plain = move, **Ctrl** = copy, **Alt** = swap. `PadView` becomes a
  drag source too.
- **Hover feedback** is local to `PadView` (an outline or a brighten), NOT
  `PadOverlay` — the overlay belongs to animations (§1.5).
- **Hardware shortcut:** holding a pad on the Launchpad while clicking a sample row
  assigns it to that pad. `MidiPadInput` already knows which pads are down.
- Every drop marks the layout dirty through the normal fingerprint; no new flag.
- **Out of scope:** dragging files in from Explorer. Unity standalone has no OS
  drag-and-drop; it needs a Win32 hook plugin. Revisit with the cross-platform work.
- **Undo** (one level: "Undo last change to pad N") is a natural companion — a pad's
  `PadSerialization` record before the change is all it needs.

### §12.2 Styles (themes)

A **style** is a `.style.json` in `stylesFolder` ("Styles"): the look, never the
content. Loading a style must not touch pads, BPM or folders — the same rule that
keeps BPM out of layouts.

- **Contents:** the seven palette roles (already serialised as hex in the session),
  the four notice colours, and UI roles — `panel`, `panelText`, `accent`,
  `buttonNormal`, `buttonText`, `highlight`, `ticker` — plus a font choice by name.
- **Applying:** a `StyleService` (Core, always-active) holds the current style and
  raises `StyleChanged`. A small `StyledGraphic` component on each Image/TMP_Text names
  its role, so there is no hierarchy walking and no name matching. Palette roles go
  through `PadPaletteAsset`, so screen and hardware follow together.
- **Fonts:** from a list of TMP font assets shipped in the build (e.g. Liberation Sans,
  Noto Sans). `TextGlyphs` already makes a font that lacks `►` or `—` degrade cleanly.
  Loading arbitrary TTFs from disk is out of scope.
- **Artwork:** the bulbs, selector switch and background are sprites. In 2.0 a style
  TINTS them where that reads well; replacing artwork is a later step.
- **UI:** a Style dropdown on the Settings page filled from the Styles folder (the
  folder browser already filters by extension). The session stores the style NAME;
  layouts never do. A missing style file falls back to the built-in default with a notice.
- **Authoring:** "Save current look as style" writes the current colours to a new
  `.style.json`, so nobody has to hand-write one. The colour picker is already runtime.

### §12.3 Lightshows

A **lightshow** is a `.lightshow.json` in the lightshows folder: timed frames painted
onto `PadOverlay`. The overlay contract — one writer, `Apply` = one event = one
batched LED message — was built for this. `ScreensaverMatrix` already refuses to start
while the overlay is owned.

- **Format** (JsonUtility-friendly — no dictionaries):
  `{ version, name, bpmSynced, lengthBeats, loop, frames: [ { at, rows[8], top } ] }`.
  `at` is in beats when `bpmSynced`, otherwise seconds. `rows` are 8 strings of 8
  space-separated `RRGGBB` values, top row first so the file reads like the grid; `-`
  means transparent (the pad shows its normal colour). `top` is the optional 91–98 row.
- **Player:** `LightshowPlayer` (View, always-active) acquires the overlay, schedules
  frames from `Metronome` (beat-synced) or `AudioSettings.dspTime` (free), and releases
  with one `ClearAll`. Beat-synced shows follow BPM changes for free, because the
  metronome accumulates phase.
- **Triggering:** a `LightshowBrowserView` (a Layouts-browser twin — `LibraryScanner`
  already takes an extension filter); optionally a per-pad `lightshow` field so a pad
  fires a show with its sample; optionally the top-row buttons, which the device
  profiles already map to 91–98.
- **Coexistence:** a show owns the overlay outright. Pressing pads while a show runs
  still triggers audio; whether the pad flash cuts through is a style choice
  (`hideUnderShow`).
- **Authoring (v2.0 minimum):** a "record" mode that captures what the grid displays
  per beat into frames, plus hand-editing — the row format is designed for it.
- **Tidy first:** decide one name — `Manager.animationsFolder` ("Animations") vs
  `SessionSettings.lightshowsFolder` ("Lightshows"). Use "Lightshows" and rename the
  Manager field with `[FormerlySerializedAs("animationsFolder")]`.

### §12.4 Later

Multiple simultaneous devices (`MidiPadInput` owning a list of connections, one
profile each); top-row and side buttons as functions (banks, stop all, show select);
cross-platform folder locations (macOS: `~/Music/Launchpad Studio`); recording;
more devices (APC Mini, Midi Fighter 64) under `PadDeviceProfile`.

---

## §13 Open questions

- Should a layout optionally carry BPM? (Currently no, and that has been right.)
- Should pad categories drive a default colour? (The enum and guesser exist; nothing
  reads them.) Styles are the natural place: a style could map category → colour.
- Should a lightshow be allowed to sit inside a layout, or always be its own file?

---

## §14 Conventions

- Private serialised fields: `[SerializeField] private T name;` with a `[Tooltip]`.
  The tooltip is user documentation *and* is read by the wiring check ("Optional").
- Private runtime fields: `_camelCase`. Public API: `PascalCase` properties.
- British spelling in our identifiers (`colour`, `normalise`), Unity's in Unity's API.
- Events are past tense (`PadChanged`, `LayoutSaved`) or imperative nouns (`Beat`).
- `ContextMenu` diagnostics on anything with hidden state (`Log … Wiring`,
  `DescribeState()`, `Log MIDI Ports`).
- Comments explain the bug or the trade-off, not the syntax.
- One class, one file, named for the class. `Control/` holds exactly one thing.
- **On screen:** "sample", not "clip". Plain ASCII in Inspector-typed text; anything
  else from code goes through `TextGlyphs.Fit`.
- **Renaming** a file, folder or serialised field: in the Project window, with
  `FormerlySerializedAs` for fields. Then run the Wiring Check.