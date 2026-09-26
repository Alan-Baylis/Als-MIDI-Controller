# Al's MIDI Controller (Version 1.0)

**A sample player for Novation Launchpads. Put a sample on a pad and play it — on
screen, on the hardware, and in time with everything else.**

Al's MIDI Controller turns a Launchpad into a live sample player with no setup. The
screen shows the same **8×8 grid of 64 pads** as the hardware. Load a **sample**
(WAV, OGG, AIFF or MP3) onto any pad, and pressing that pad plays it, either straight
away or **quantised** to the beat. Save the whole grid as a **layout** (a kit of 64
pads) and switch between layouts with one click. Your **session** (tempo, volume,
colours and the current grid) is saved automatically when you quit.

It was built for the musician with a folder of random samples and no sample pack, no
DAW and no patience for a setup wizard.

---

## Supported devices

| Device | Colours | Status |
|---|---|---|
| **Novation Launchpad Pro MK3** | Full RGB | ✅ Tested on hardware (reference device) |
| Novation Launchpad X | Full RGB | Supported, not yet tested on hardware |
| Novation Launchpad Mini MK3 | Full RGB | Supported, not yet tested on hardware |
| Novation Launchpad Pro (MK2) | RGB | Supported, not yet tested on hardware |
| Novation Launchpad MK2 | RGB | Supported, not yet tested on hardware |
| Novation Launchpad / Launchpad S / Launchpad Mini (red/green models) | Red & green | Supported, not yet tested on hardware |

**Notes**
- The device is **detected automatically**. You can also choose it yourself on the
  **Settings** page.
- **One Launchpad at a time** in version 1.0.
- **Plug in and unplug whenever you like.** Playback carries on, and the pads repaint
  when the device comes back.
- **Close other MIDI software first** (Ableton Live, Novation Components and so on).
  Windows lets only one program use a MIDI port at a time.
- The older red/green models may need Novation's USB driver on Windows.
- No Launchpad? The on-screen grid works with a mouse.
- Reports from owners of the untested models are very welcome. Please open an issue.

---

## How it's different from Ableton Live

Ableton Live is a complete music production studio, and the Launchpad was designed
alongside it. Al's MIDI Controller doesn't try to replace it. It's for the moments when
you just want to **play your samples**.

- **No project, no setup.** There are no tracks to create, no Session View to arrange
  and no MIDI mapping to do. Open the app and the grid is ready.
- **Any sample just works.**
  - Silence at the start of a file is **trimmed automatically**, so a pad sounds the
    instant you hit it.
  - Loops **restart on the beat**, so samples of any length stay locked to the bar
    without warp markers.
  - The app does **not** time-stretch. A loop plays at its own speed and starts on the
    grid.
- **The hardware simply follows the screen.** Every pad lights up with its state
  (empty, loading, ready, missing or playing), a custom colour if you gave it one, and
  a flash when it's hit. The top row doubles as a metronome.
- **Errors explain themselves.** When a file won't play, you're told *why* (for
  example, "this is an MP3 despite the extension") and what to do about it.
- **Hard to lose work.**
  - The session saves itself.
  - Files are written safely, and the previous version is kept as a backup.
  - Loading a layout or quitting with unsaved changes asks **Save / Don't Save /
    Cancel**.
- **Small and focused.** One window, three pages, no manual.
- **Free and open source** under the MIT licence.

---

## Quick start

**1. Install**
- Download the latest zip from **[Releases](../../releases)** and extract it anywhere
  you can write to (for example, your Documents folder).
- Run the `.exe`. Windows SmartScreen may warn you about an unsigned program; choose
  **More info → Run anyway**.

**2. Add your samples**
- Copy WAV, OGG, AIFF or MP3 files into the **`Samples`** folder next to the `.exe`.
  Subfolders are fine.
- Or use **Folder…** in the samples browser to point the app at a folder you already
  have.

**3. Plug in your Launchpad** — before or after starting, either works.

**4. Choose a page** by clicking the selector switch: **Player**, **Pads** or
**Settings**. Right-click the switch to go back a page.

**5. Put a sample on a pad (Pads page)**
- Click a pad on the grid to select it.
- In the samples browser, **left-click** a file to preview it, and **right-click** it
  to load it onto the selected pad. The pad inspector's **Load** button does the same.
- In the pad inspector you can set the pad's **label**, **volume** and **loop**, and
  its **start beat**:
  - *Next beat* starts on the next beat, whichever it is.
  - *Beat 1* waits for the downbeat.
- **Clear** empties the pad.

**6. Play**
- Hit the pads, on the Launchpad or with the mouse.
- Pressing a playing loop stops it at the end of the bar.
- **Stop All** silences everything at once.

**7. Tempo and layouts (Player page)**
- Set the **BPM**, the master volume and the metronome.
- In the **layouts browser**:
  - **Save** keeps your changes.
  - **Save As** saves under a new name in the folder you're viewing.
  - **New** starts an empty grid.
  - Click a layout to load it. The current layout is marked with **►**, and a
    **\*** means it has unsaved changes.

**8. Settings page** — choose your MIDI device, and turn pad captions and hints on or
off.

**9. Quit** with the Quit button or the window's close button. If the layout has
unsaved changes, you'll be asked first.

**Where things are kept**

| What | Where |
|---|---|
| Samples | `Samples\` next to the `.exe` |
| Layouts | `Layouts\` next to the `.exe` |
| Logs | `Logs\` next to the `.exe` (please attach `latest.log` to bug reports) |
| Settings / session | `%USERPROFILE%\AppData\LocalLow\<Company>\<Product>\session.json` |

---

## More features

- **Per-pad settings:** volume, loop, start beat, label and custom colour.
- **Musical timing:** quantised starts, stop at bar end, and loops synced to the grid,
  all following live tempo changes.
- **Metronome** with an optional click, shown on screen and on the Launchpad's top row.
- **Pad captions** drawn in a colour that stays readable on every pad colour.
- **Colour picker** for the pad palette, previewed live on the screen and on the
  hardware.
- **Built-in file browser**, with drive list, new folder and overwrite check.
- **Notification ticker and message log** with filters and copy, plus a log file on
  disk.
- **Screensaver:** a "digital rain" animation on the pads when idle. It never touches
  your layout, and a pad press wakes it.
- **Moveable:** folders are stored relative to the app, so you can move the whole
  folder, or take it on a USB stick, and nothing breaks.

## Coming in version 2.0
- **Drag and drop:** drag a sample onto a pad, and move, copy or swap pads.
- **Styles:** themes for the whole interface and the pad colours.
- **Lightshows:** animations for the Launchpad, synced to the beat.

---

## Building from source

- **Unity 6000.4.1f1** (Unity 6.4), URP, TextMeshPro
- Open the project folder in Unity Hub, open the scene **MIDI Controller**, then
  **File → Build Profiles → Windows → Build**.
- A post-build step copies `Samples` next to the executable.
- Design notes, architecture and conventions are in **[DESIGN.md](DESIGN.md)**.

## Credits and licence

- Released under the **[MIT License](LICENSE)**.
- MIDI by **[DryWetMIDI](https://github.com/melanchall/drywetmidi)** (MIT).
- Text by TextMeshPro with **Liberation Sans** (SIL Open Font License).
- Novation and Launchpad are trademarks of Focusrite Audio Engineering Ltd. Ableton
  Live is a trademark of Ableton AG. This project is not affiliated with or endorsed
  by either company.

---

**Thanks for trying Al's MIDI Controller.** Grab a folder of samples, plug in your
Launchpad and play. If something breaks, or you'd like a feature, open an issue and
attach `Logs\latest.log`.
*— Al*

"This project has a *spine* to it, and that's because you keep insisting things be explainable rather than just working. Long may it continue."
*— Claude Opus 5*
