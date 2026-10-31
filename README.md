# YorteVegas

A focused desktop video editor for Windows, built with WPF on .NET 8 and powered by mpv.
It is the first editor core of YorteVegas: import media, arrange a timeline, set up
crossfades and clip levels, preview the composited cut, and export a simple MP4.

![YorteVegas editor](screenshot-yortevegas.jpg)

## What works

- **Media library** — import video, audio and still images; filter by kind, search, and
  see durations / dimensions. Larger thumbnails and a length badge make the clip length
  visible before you drag it in. Missing files are flagged.
- **Timeline** — video and audio tracks, add/remove/rename layers, mute / solo / lock
  (icons with hover text), drag clips, trim either edge, split at the playhead or with
  the razor tool, duplicate and delete. Video clips show a filmstrip thumbnail, and the
  track stack scrolls vertically when it grows beyond the view.
- **Crossfades** — drag one clip over another to create an overlap. A fade curve is drawn
  over the overlap for video and audio; right-click it to pick Linear, Ease in, Ease out,
  S-curve or Exponential, or to remove the overlap.
- **Clip levels** — drag the horizontal line near the top of a clip to set opacity (video
  and images) or volume (audio). Hover the line to see the exact percentage.
- **Editing aids** — snapping to clip edges and the playhead, frame-accurate stepping,
  zoom (mouse wheel, pointer-anchored; `+` / `-`), horizontal scrolling (`Shift` + wheel),
  vertical scrolling (`Alt` + wheel or the right scrollbar), ruler scrubbing and a follow
  playhead during playback.
- **Preview** — mpv renders the clip under the playhead with audio, including still-image
  clips. Transport controls, timecode, volume and frame stepping live below the video.
- **Export** — a deliberately basic export page with three choices: **Great**, **Good**
  and **Light for WhatsApp**. No resolution/bitrate controls; just pick one, choose a
  file and render an MP4.
- **Undo / redo** — snapshot based, including selection, tracks and library changes.
- **Projects** — save/open `.yveg` JSON projects; media stays outside the project file
  (relative paths when possible). Unsaved changes are protected on close.
- **Settings** — defaults for new projects, preview volume, still-image duration and
  "reopen last project".

## Requirements

- Windows 10/11, x64.
- .NET 8 SDK (tested with 8.0.416).
- No separate FFmpeg or mpv installation is needed for playback: the
  `Nickvision.MPVSharp.Bundled` package copies `libmpv-2.dll` next to the app.
- **Export requires FFmpeg.** The app looks for `ffmpeg.exe` beside the executable, in
  a `tools` folder, on `PATH`, or in `%LocalAppData%\YorteVegas`. If it can’t find one,
  the first export asks you to locate it once and remembers the path.

## Build and run

```powershell
dotnet build
dotnet run
```

or launch the built app:

```powershell
.\bin\Debug\net8.0-windows\YorteVegas.exe
```

You can also open files from the command line:

```powershell
YorteVegas.exe "D:\edits\holiday.yveg"      # open a project
YorteVegas.exe "D:\media\b-roll.mp4"         # open the last project and import media
```

## Using the editor

1. **Import** media (`Ctrl+I` or the button in the library). You can also drop files
   onto the library or pass them on the command line.
2. **Add to the timeline** by double-clicking a library item, using its context menu,
   or dragging it onto the timeline. Clips are inserted at the playhead when there is
   room and otherwise after the existing clips on the target track.
3. **Edit**: click to select, drag the middle to move, drag either edge to trim. Use
   `S` (or the Razor toggle) to split, `Ctrl+D` to duplicate, `Delete` to remove.
4. **Fade**: drag a clip over the previous clip on the same track to create an overlap,
   then right-click the overlap (its curves/shadows) to choose a curve or remove it.
5. **Levels**: drag the thin horizontal line near the top of a clip down/up. It controls
   opacity for video/images and volume for audio; the tooltip shows the value.
6. **Layers**: right-click a layer header to rename it or remove it. If it contains
   clips, a warning asks for confirmation first; removing a layer is undoable.
7. **Preview** with `Space`; scrub with the ruler. Images use a still duration
   (default 5 s, configurable in Settings).
8. **Export** from the title-bar button, File → Export video… or `Ctrl+E`. Pick
   **Great**, **Good** or **Light for WhatsApp**, choose the file, and press Export.

## Export

The export page intentionally has no quality knobs. It offers three choices:

| Choice | What it is |
| --- | --- |
| **Great** | Best quality at the full project resolution. |
| **Good** | Up to 1080p; a balanced size for everyday sharing. |
| **Light for WhatsApp** | Up to 720p; a smaller file that sends easily. |

Export writes an H.264/AAC MP4 and is optimized with `faststart`. It renders the
timeline the way the monitor shows it: every video layer with its opacity, the
crossfades between overlapping clips, and all audible clips mixed with their
volumes. Overlap fades are exported as linear ramps even if another curve is
selected in the timeline.

### Stretching clips

- **Drag the right edge** to trim. Dragging past the end of the source repeats the
  media; dashed lines mark where it loops back.
- Dragging back towards one full pass **snaps to the regular length** of the media.
- **Ctrl + drag the right edge** to time-stretch: the clip gets longer/shorter while
  the source stays one pass, so playback becomes slow/fast motion. The clip shows a
  speed badge such as `0.5x`; dragging back near the source length snaps to `1x`.
- Looped playback uses mpv's A-B loop and keeps audio pitch corrected.

### Keyboard shortcuts

| Action | Shortcut |
| --- | --- |
| Play / pause | `Space` |
| Step one frame | `←` / `→` (add `Shift` for one second) |
| Start / end | `Home` / `End` |
| Split at playhead | `S` |
| Duplicate clip | `Ctrl+D` |
| Delete clip | `Delete` |
| Undo / redo | `Ctrl+Z` / `Ctrl+Y` |
| Import media | `Ctrl+I` |
| Export video | `Ctrl+E` |
| New / open / save | `Ctrl+N` / `Ctrl+O` / `Ctrl+S` |
| Save as | `Ctrl+Shift+S` |
| Zoom timeline | Mouse wheel (pointer-anchored), or `+` / `-` |
| Fit timeline | `F` |
| Scroll timeline | `Shift` + mouse wheel |
| Scroll layers | `Alt` + mouse wheel, or the right scrollbar |
| Layer menu | Right-click a layer header (rename / remove) |
| Loop / stretch a clip | Drag the right edge; use `Ctrl` + drag for slow/fast motion |
| Change opacity / volume | Drag the line near the top of a clip |
| Razor tool | Razor toggle in the bottom strip |

## Project format

`.yveg` files are readable JSON (version 1). They hold project settings, the media
library, tracks and clips. Media is always referenced by path, never embedded; paths
inside the project folder are stored relatively so a folder can be moved as a unit.

```text
%AppData%\YorteVegas\settings.json   user preferences
%AppData%\YorteVegas\crash.log       written if an unexpected error occurs
<app folder>\mpv.log                 mpv initialization diagnostics
```

## Preview model and known limits

- The preview plays **one source at a time**. The topmost visual clip at the playhead
  wins; if there is no visual clip, an audio clip is played. Overlapping audio tracks
  are not mixed yet, and a video clip's own audio is used.
- **Fades and opacity are timeline-side for now**: the overlap is drawn, editable and
  saved, but the mpv preview cuts between the overlapping clips and does not composite
  two streams or apply clip opacity. Clip volume does affect preview audio.
- Looped and time-stretched playback uses mpv's A-B loop/speed with a wall-clock
  playhead anchor, so it is a preview approximation rather than a rendered result.
- Audio-only clips show an "Audio only" monitor panel while the soundtrack continues.
- Track mute / solo affect preview audio.
- **Basic export only**: the first export pass renders the main video layer and its
  own audio. Crossfades, clip opacity, track volume curves and separate audio layers
  are not yet baked into the MP4. Export requires FFmpeg.
- No effects, titles or color tools in this build.
- Video filmstrips use the Windows thumbnail for the file, repeated along the clip
  (not multiple sampled frames).
- Timecode uses drop-frame notation (`;` before frames) for 29.97/59.94 and non-drop
  for other rates.
- Fade duration is the overlap itself; drag either clip to lengthen or shorten the
  crossfade. The curve shape is editable separately.

## Architecture

```text
MainWindow.xaml / .cs        Shell, workspace, library, monitor, transport, shortcuts
Controls/MpvPreview.cs       HwndHost + Nickvision.MPVSharp player
Controls/TimelineView.cs     Custom-drawn timeline, hit-testing and editing gestures
ViewModels/EditProject.cs    Media, tracks, clips, selection, playhead, edit operations
Services/TimelinePreview.cs  Loads clips, seeks, advances the playhead during playback
Services/MediaProbeService.cs  Duration/size probing and Shell video thumbnails
Services/ExportService.cs    Basic FFmpeg MP4 export (main video layer + its audio)
Services/ProjectService.cs   .yveg DTOs, save/load, undo snapshots
Services/UndoService.cs      Bounded snapshot undo/redo
Dialogs/                     New project, settings, dark message dialog
Themes/Dark.xaml             Shared dark-blue palette and control styles
Models/                      Observable project model
```

The UI is plain WPF code-behind (no MVVM framework), matching the YorteImpact
foundation. Timeline mutations go through `EditProject`, which raises
`TimelineChanged` and supports undo snapshots. The preview is driven by
`TimelinePreview`, which owns the playback state machine and never touches WPF
controls from mpv callbacks.
