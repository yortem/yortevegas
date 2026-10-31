# YorteVegas — agent notes

## Product scope

YorteVegas is the editor core of a desktop video editor. The current milestone is
deliberately narrow:

- Import video / audio / images.
- Arrange clips on video and audio tracks with select, move, trim, split, duplicate,
  delete, undo/redo.
- Overlapping clips create editable video/audio crossfades; clip opacity/volume lines;
  add/remove layers with a warning when clips are present.
- Preview with mpv.
- Save/open `.yveg` projects (JSON, media referenced by path).

**Do not add** export, effects, transitions, titles, AI, photo editing, shape/paint
tools or Stitch features until that milestone is explicitly requested. Those belong to
other Yorte apps and must not be copied from YorteImpact.

## Build and check

```powershell
dotnet build
dotnet run
```

- Target: `net8.0-windows`, x64, WPF.
- The bundled `Nickvision.MPVSharp.Bundled` package supplies `libmpv-2.dll`. Do not add
  a manual FFmpeg dependency.
- Test on Windows with a short MP4 (H.264 + audio), an audio file, and an image.
- Manual smoke checklist:
  1. Launch with no project → empty editor, "No clip at playhead".
  2. Import media; double-click / drag to timeline.
  3. Scrub, play, pause, step frames, adjust volume.
  4. Move, trim, split, duplicate, delete; undo/redo restores selection.
  5. Mute/lock/solo tracks (locked tracks reject edits).
  6. Save, close, reopen; media still resolves (relative path when in the project folder).
  7. Close with unsaved changes → dark Save / Don't save / Cancel dialog.

## Layout conventions

- `YorteVegas.Models` — project data (`MediaAsset`, `TimelineTrack`, `TimelineClip`).
- `YorteVegas.ViewModels.EditProject` — the single mutation point for timeline state.
- `YorteVegas.Services` — persistence, probing, settings, undo, preview engine, timecode.
- `YorteVegas.Controls` — `PreviewCompositor` (HwndHost + OpenGL) and `TimelineView` (custom-drawn).
- `YorteVegas.Dialogs` — new project, settings, message dialog.
- `Themes/Dark.xaml` — palette and reusable control styles. Keep the YorteImpact
  palette: `#080A11`, `#0D131D`, `#111A27`, `#2E9BFF`, `#E6EEF6`, `#8EA0B4`.
- UI is plain WPF code-behind; no MVVM framework, no reactive libraries.

### Adding timeline operations

1. Add the operation to `EditProject` (validation, clamping, selection).
2. Capture undo in the caller with `project.BeginEdit()` **before** mutating, once per
   gesture, then `project.NotifyTimelineChanged()` after.
3. Draw/hit-test in `TimelineView` if it is an interactive gesture; keep mutations out
   of the render pass.
4. Persist any new clip/track fields in `ProjectService` DTOs and `Capture`/`Apply`.

### Persistence and undo

- Undo snapshots are full `ProjectFile` captures. Keep `Capture`/`Apply` lossless.
- Clip fields (`FadeCurve`, `Opacity`, `Volume`) must round-trip through `ClipDto`.
  `Opacity`/`Volume` are nullable in the DTO so old projects default to 1.
- Never store absolute paths when the media lives inside the project folder; use
  `ProjectService.StorePath`.
- `ProjectService.Save` writes a temp file then replaces, so failed saves do not
  destroy the previous project.
- `EditProject.RestoreSelection` must be used after rebuilding tracks (load/undo),
  because the selected clip ID already matches and the normal property setter would
  skip refreshing the new clip instances.

### Overlaps, fades and levels

- Overlaps are **intentional**. `EditProject.ClampMove`/`TrimClip*` must not push clips
  away from each other; only source length and timeline zero constrain them.
- `EditProject.GetOverlaps(track)` defines the crossfade between adjacent clips. The
  later-starting clip carries `FadeCurve`; the timeline draws both sides of the curve
  and opens the curve menu on right-click.
- Insert commands still use `FirstFreeStart`, so double-click/drop insertion never
  creates an accidental overlap.
- Clip levels live on `TimelineClip.Opacity`/`Volume` (0..1). The rubber band sits at
  the top edge of the clip at full level and at the bottom at zero
  (`LevelTop = rect.Y + 4`, `LevelBottom = rect.Bottom - 3`). `TimelineView` draws the
  rubber band and handles the vertical drag. The timeline raises
  `ClipLevelChanged` for live preview updates; call `NotifyTimelineChanged` on
  release for dirty/undo.
- Dragging a clip onto another compatible layer moves it (`EditProject.
  MoveClipToTrack`); dragging below the last layer of its kind shows a dashed
  "new layer" row and creates that track on drop (`UpdateClipDropTarget` /
  `CommitClipLayerDrop`).
- A borderless maximized window would cover the taskbar; `MainWindow.WindowProc`
  handles `WM_GETMINMAXINFO` and clamps the maximize rectangle to the work area of
  the monitor the window is on.
- Preview volume = monitor base volume × clip volume × fade gain. Opacity and
  crossfades render in the compositor; the export does not bake them yet.

### Clip timing, looping and time-stretch

- `TimelineClip.PlaybackRate` (0.1..10) maps project time to source:
  `source = In + (projectTime - Start) * rate`.
- A clip loops when `Duration * rate` exceeds the remaining source. Normal right-edge
  trimming is allowed to create that; `TimelineView` draws dashed loop boundaries and
  `SnapRegularEnd` snaps back to one full pass of the media.
- Holding Ctrl when starting a right-edge drag selects `DragMode.StretchRight`: the
  current source segment is kept, the new rate is `sourceLength / newDuration`, and the
  clip snaps to `1x` at the regular length. A speed badge is drawn on the clip.
- `TimelinePreview` applies mpv `speed` and `ab-loop-a/b`. Looped clips drive the
  playhead from a wall-clock anchor, with a fallback seek if mpv reaches the source end.
  Always clear `ab-loop-a/b` when switching to a non-looped clip.
- `SplitClip` advances the right clip's `In` by `(split - start) * rate`; left trims
  adjust `In` by `delta * rate`. Keep both rate-aware.

## Export

- The export UI must stay deliberately simple: exactly **Great**, **Good**, and
  **Light for WhatsApp**, plus a destination. Do not add resolution/bitrate/CRF
  controls to the page.
- `ExportService` renders the whole timeline with one FFmpeg filter graph that
  mirrors the compositor: every video layer bottom to top with clip opacity, an
  alpha fade-in for each overlapping incoming clip, and all audible clips delayed,
  volume-adjusted and mixed. Keep it in step with `PreviewCompositor`; do not go
  back to per-clip segments + concat.
- Export fades are linear alpha ramps even when the clip uses another curve; the
  preview shows the chosen curve. If curve-accurate export is needed, extend
  `BuildGraph` rather than changing the preview.
- FFmpeg is external for export only, but it is bundled: `tools\ffmpeg.exe` is copied
  beside the executable by the csproj (`Link="ffmpeg.exe"`). `LocateFfmpeg` finds it
  there, so the dialog normally never asks. The path prompt is only a fallback when
  the file is missing. If you replace the bundled build, keep the licence obligations
  of that build in mind (the current static build includes x264/GPL components; this
  app is used privately).
- The whole graph is passed through `ProcessStartInfo.ArgumentList` (never a shell
  string). Keep captions/overlays and audio trims inside the same graph so one
  encode produces the file.
- A basic export is H.264/AAC, `yuv420p`, faststart. `Light` caps the long edge at
  1280, `Good` at 1920, and `Great` keeps the project size.

## Preview compositor (important)

`Controls/PreviewCompositor.cs` owns the monitor: one `HwndHost` with a WGL
OpenGL context, one libmpv render context per active clip, each rendering into
its own FBO, composited with per-layer alpha in track order. This is what makes
crossfades, clip opacity and stacked tracks visible. It replaced the earlier
single `MpvPreview` window player, which could only show one file.

- **Do not go back to one `vo=gpu` player attached to an HWND (`wid`).** A
  window-bound player cannot blend layers, so opacity and fades would only exist
  on the timeline again.
- **Airspace:** the monitor is an `HwndHost`; never place WPF controls or
  tooltips on top of the video. The transport bar is a separate row.
- `MpvPlayer` (direct libmpv P/Invoke) uses `vo=libmpv`, `gpu-api=opengl`,
  `gpu-context=win`, `keep-open=yes`, `image-display-duration=inf`,
  `input-default-bindings=no` and `input-vo-keyboard=no`. All calls run on the
  UI thread with the GL context current.
- Render parameter arrays passed to `mpv_render_context_create` / `_render`
  must end with `MPV_RENDER_PARAM_INVALID` (type 0); without the terminator
  libmpv reads past the array and crashes. Set
  `MPV_RENDER_PARAM_BLOCK_FOR_TARGET_TIME` (type 12) to 0 — type 13 is
  `SKIP_RENDERING`, not block-for-target-time, and using it does not stop
  mpv from blocking on every layer render.
- Ctrl+drag on a clip body duplicates it and drags the copy (deferred until the
  mouse actually moves, so a Ctrl+click does not duplicate). Ctrl+drag on the
  right edge is still time-stretch.
- Shift+drag the level line on a video clip adjusts its **volume**; a normal drag
  still adjusts opacity. Audio clips always use the line for volume.
- Manual fades live on `TimelineClip.FadeInSeconds`/`FadeOutSeconds`; the clip menu
  toggles 0.5 s fades, the preview multiplies them into alpha/audio gain, and the
  export adds `fade`/`afade` filters. They are persisted in `ClipDto`.
- The export audio mix ends with `alimiter=limit=0.95` so loud sums do not clip.
  The monitor meter is driven by `TimelinePreview.AudioLevel`, which reads the
  waveform peak at the playhead (a content meter, not a live output meter).
- Crop/pan lives on `TimelineClip.CropLeft/Right/Top/Bottom` (source fractions) and
  `PanX/PanY` (source fractions, clamped into the crop window). The preview maps the
  crop window through UV uniforms in the compositor; the export applies an FFmpeg
  `crop` filter before scale/pad. The clip menu opens the visual `CropPanDialog`:
  it captures the layer FBO with `PreviewCompositor.TryCaptureLayer` and lets the
  user drag the visible box (`CropEditor`). Crop edges go up to 0.95 each and are
  clamped together on apply.
- Copy/paste style (`ClipStyleClipboard`) carries opacity, volume, fades and
  crop/pan between clips from the clip context menu; it deliberately excludes
  timing (start, in-point, duration, rate).
- `TimelinePreview` builds one `PreviewLayer` per active clip, bottom track
  first, alpha = clip opacity x fade-curve progress. Video clips keep their own
  audio; audio-track clips are visual-less layers. The next clip is preloaded up
  to 3 s ahead (paused and silent) so cuts do not wait for `loadfile`.
- Preview quality is deliberately 540p per layer (FBO height 540, width capped
  at 1200) to keep playback smooth. Do not raise it without measuring.
- The playhead is wall-clock driven; never derive it from `time-pos`.
- Gaps play black; playback that starts in a gap stays there rather than jumping
  to the next clip.
- Stills do not expose a duration: seek them 250 ms after load (`Still`), never
  wait on `duration` alone.
- If the compositor cannot initialise, `CompositorError` is shown in the monitor
  placeholder instead of a silent black frame.
- `MediaProbeService` creates short-lived probe clients with `vo=null`, `ao=null`,
  `pause=yes`. Probes are serialized with a semaphore and disposed on the same
  background thread. Removing `audio=no` was required for audio-only files to expose
  `duration`.
- **Do not create extra mpv image-output clients for thumbnails.** An earlier
  `vo=image` frame extractor caused an intermittent CoreCLR access violation when it
  ran alongside the preview. Video filmstrips now use the Windows Shell thumbnail
  (`IShellItemImageFactory`) on a dedicated STA thread; keep it that way.
- Track header controls are custom-drawn icons (lock, mute, solo) with timeline-drawn
  hover tooltips. The layer context menu raises `RemoveTrackRequested` and
  `RenameTrackRequested`; MainWindow owns the dialogs and calls into `EditProject`.
- The timeline scrolls vertically with a custom right-hand scrollbar and `Alt` + wheel.
  `TrackTop` already includes `-_scrollY`; keep drawing and hit testing on `TrackTop`
  and `TryGetTrackRow` so the header and lanes stay aligned.
- `Timecode.Format` uses drop-frame numbering (`HH:MM:SS;FF`) for 29.97/59.94 and
  non-drop (`:`) for other rates. Keep the ruler labels separate (short wall-clock).

## Appearance

- Custom `WindowChrome` title bar; mark interactive elements with
  `WindowChrome.IsHitTestVisibleInChrome="True"` and keep `IsInsideButton` checks in
  the drag handler.
- Use `Themes/Dark.xaml` resource styles (`BaseButton`, `GhostButton`, `AccentButton`,
  `IconButton`, `StripButton`, `ChipToggle`, `ThinSlider`, etc.).
- The implicit `TextBlock` style deliberately does **not** set `Foreground`; text
  inherits from the window/button so light buttons (e.g. Export) keep dark labels.
- Timeline-drawn interactions must keep hit testing in sync with rendering: use
  `HeaderButtonRect`, `ClipRect`, `LevelTop`/`LevelBottom`, and `TryGetFadeAt` for both
  drawing and input.
- Prefer the custom `MessageDialog` over `MessageBox` for prompts so dialogs stay dark.

## Files worth knowing

| File | Responsibility |
| --- | --- |
| `MainWindow.xaml` / `.cs` | Shell, library, monitor, transport, menu, shortcuts |
| `Controls/TimelineView.cs` | Ruler/layers/clips/scrollbar drawing and gestures |
| `Controls/MpvPreview.cs` | HwndHost + libmpv, threading, lifecycle |
| `Services/TimelinePreview.cs` | Clip loading, seeking, playback state machine |
| `Services/ProjectService.cs` | `.yveg` schema, save/load, undo capture/apply |
| `ViewModels/EditProject.cs` | Timeline operations, selection, playhead, snapping |
| `Themes/Dark.xaml` | Palette and control templates |
| `README.md` | User-facing features, limits, build instructions |

Settings live at `%AppData%\YorteVegas\settings.json`; crash logs at
`%AppData%\YorteVegas\crash.log`; mpv diagnostics at `mpv.log` beside the executable.
