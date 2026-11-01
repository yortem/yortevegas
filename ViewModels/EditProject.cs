using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using YorteVegas.Models;
using YorteVegas.Services;

namespace YorteVegas.ViewModels;

/// <summary>
/// The open edit: media library, tracks/clips, selection and playhead. All
/// timeline mutations funnel through here so undo snapshots and change
/// notifications stay consistent.
/// </summary>
public sealed class EditProject : ObservableObject
{
    private const double Epsilon = 1e-6;

    private string _name = "Untitled";
    private int _width = 1920;
    private int _height = 1080;
    private double _fps = 30;
    private double _playhead;
    private Guid? _selectedClipId;
    private readonly List<Guid> _selectedClipIds = new();
    private double? _rangeStart;
    private double? _rangeEnd;

    public EditProject()
    {
        Tracks.CollectionChanged += OnTracksChanged;
        Undo.StateChanged += (_, _) => UndoStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public static EditProject Create(ProjectSettings settings)
    {
        var project = new EditProject
        {
            Name = settings.Name,
            Width = settings.Width,
            Height = settings.Height,
            Fps = settings.Fps
        };
        project.Tracks.Add(new TimelineTrack { Name = "Video 1", Kind = TrackKind.Video });
        project.Tracks.Add(new TimelineTrack { Name = "Audio 1", Kind = TrackKind.Audio });
        project.Undo.Clear();
        return project;
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, string.IsNullOrWhiteSpace(value) ? "Untitled" : value.Trim());
    }

    public int Width
    {
        get => _width;
        set
        {
            if (SetProperty(ref _width, value)) OnPropertyChanged(nameof(ResolutionLabel));
        }
    }

    public int Height
    {
        get => _height;
        set
        {
            if (SetProperty(ref _height, value)) OnPropertyChanged(nameof(ResolutionLabel));
        }
    }

    public double Fps
    {
        get => _fps;
        set
        {
            if (SetProperty(ref _fps, value <= 0 ? 30 : value)) OnPropertyChanged(nameof(FpsLabel));
        }
    }

    public ObservableCollection<MediaAsset> Assets { get; } = new();
    public ObservableCollection<TimelineTrack> Tracks { get; } = new();
    public UndoService Undo { get; } = new();

    /// <summary>Default timeline length for still images.</summary>
    public double ImageDurationSeconds { get; set; } = 5;

    public event EventHandler? TimelineChanged;
    public event EventHandler? SelectionChanged;
    public event EventHandler? PlayheadChanged;
    public event EventHandler? RangeChanged;
    public event EventHandler? UndoStateChanged;

    public string ResolutionLabel => $"{Width} × {Height}";
    public string FpsLabel => Fps % 1 == 0 ? $"{Fps:0} fps" : $"{Fps:0.###} fps";
    public double FrameDuration => 1.0 / Math.Max(1, Fps);

    public double Playhead
    {
        get => _playhead;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(10, ContentDuration + 5));
            if (Math.Abs(_playhead - clamped) < 0.0000001) return;
            _playhead = clamped;
            OnPropertyChanged();
            PlayheadChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Primary selection (the clip menus and the info strip act on it). Setting
    /// it replaces any multi-selection with this single clip.
    /// </summary>
    public Guid? SelectedClipId
    {
        get => _selectedClipId;
        set => ApplySelection(value is { } id ? new[] { id } : Array.Empty<Guid>(), value);
    }

    public TimelineClip? SelectedClip => FindClip(_selectedClipId);

    /// <summary>Start of the export range, or null when the whole timeline is exported.</summary>
    public double? RangeStart => _rangeStart;

    public double? RangeEnd => _rangeEnd;

    public bool HasRange =>
        _rangeStart is { } start && _rangeEnd is { } end && end - start > FrameDuration * 0.5;

    public void SetRange(double start, double end)
    {
        var from = SnapToFrame(Math.Max(0, Math.Min(start, end)));
        var to = SnapToFrame(Math.Max(0, Math.Max(start, end)));
        if (to - from < FrameDuration * 0.5)
        {
            ClearRange();
            return;
        }

        if (_rangeStart is { } currentStart && _rangeEnd is { } currentEnd &&
            Math.Abs(currentStart - from) < 0.000001 &&
            Math.Abs(currentEnd - to) < 0.000001)
        {
            return;
        }

        _rangeStart = from;
        _rangeEnd = to;
        OnPropertyChanged(nameof(RangeStart));
        OnPropertyChanged(nameof(RangeEnd));
        OnPropertyChanged(nameof(HasRange));
        RangeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearRange()
    {
        if (_rangeStart == null && _rangeEnd == null) return;

        _rangeStart = null;
        _rangeEnd = null;
        OnPropertyChanged(nameof(RangeStart));
        OnPropertyChanged(nameof(RangeEnd));
        OnPropertyChanged(nameof(HasRange));
        RangeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Every selected clip id; the primary is <see cref="SelectedClipId"/>.</summary>
    public IReadOnlyList<Guid> SelectedClipIds => _selectedClipIds;

    public int SelectionCount => _selectedClipIds.Count;

    public bool IsClipSelected(Guid id) => _selectedClipIds.Contains(id);

    public IReadOnlyList<TimelineClip> SelectedClips() =>
        _selectedClipIds.Select(id => FindClip(id)).Where(c => c != null)
            .Cast<TimelineClip>().ToList();

    public void SetSelection(IEnumerable<Guid> ids, Guid? primary = null) =>
        ApplySelection(ids, primary);

    /// <summary>Ctrl+click: add or remove one clip without disturbing the rest.</summary>
    public void ToggleClipSelection(TimelineClip clip)
    {
        var ids = new List<Guid>(_selectedClipIds);
        var added = !ids.Remove(clip.Id);
        if (added) ids.Add(clip.Id);
        ApplySelection(ids, added ? clip.Id : ids.Count > 0 ? ids[^1] : null);
    }

    /// <summary>Shift+click: select the run of clips between the anchor and the target.</summary>
    public void SelectRange(TimelineTrack track, TimelineClip anchor, TimelineClip target)
    {
        var clips = track.Clips
            .OrderBy(c => c.StartSeconds)
            .ThenBy(c => c.Id)
            .ToList();
        var from = clips.IndexOf(anchor);
        var to = clips.IndexOf(target);
        if (from < 0 || to < 0) return;
        if (from > to) (from, to) = (to, from);
        ApplySelection(clips.Skip(from).Take(to - from + 1).Select(c => c.Id), target.Id);
    }

    /// <summary>Selects every clip on every layer.</summary>
    public int SelectAllClips()
    {
        var ids = Tracks
            .SelectMany(t => t.Clips.OrderBy(c => c.StartSeconds))
            .Select(c => c.Id)
            .ToList();
        ApplySelection(ids, ids.Count > 0 ? ids[^1] : null);
        return ids.Count;
    }

    public int SelectTrackClips(TimelineTrack track)
    {
        var ids = track.Clips.OrderBy(c => c.StartSeconds).Select(c => c.Id).ToList();
        ApplySelection(ids, ids.Count > 0 ? ids[^1] : null);
        return ids.Count;
    }

    /// <summary>True when the clipboard holds clips whose media still exists here.</summary>
    public bool CanPasteClips =>
        ClipClipboard.HasClips && ClipClipboard.Items.Any(i =>
            i.Kind == MediaKind.Text || FindAsset(i.AssetId) != null);

    /// <summary>Copies the selection into the in-app clip clipboard.</summary>
    public int CopySelectedClips()
    {
        var clips = SelectedClips().OrderBy(c => c.StartSeconds).ToList();
        if (clips.Count == 0) return 0;

        ClipClipboard.Set(clips.Select(c =>
            ClipClipboard.Capture(c, TrackOf(c)?.Id ?? Guid.Empty)));
        return clips.Count;
    }

    /// <summary>
    /// Pastes the clipboard at a project time, keeping the relative offsets of
    /// the copied clips and preferring their original layers.
    /// </summary>
    public int PasteClips(double at)
    {
        if (!CanPasteClips) return 0;

        var items = ClipClipboard.Items;
        var baseStart = items.Min(i => i.Start);
        var pasted = new List<TimelineClip>();

        foreach (var item in items)
        {
            if (item.Kind != MediaKind.Text && FindAsset(item.AssetId) == null) continue;

            var track = Tracks.FirstOrDefault(t =>
                    t.Id == item.TrackId && !t.IsLocked && t.Accepts(item.Kind))
                ?? EnsureTrack(item.Kind);
            if (track.IsLocked) continue;

            var clip = new TimelineClip
            {
                AssetId = item.AssetId,
                Name = item.Name,
                Kind = item.Kind,
                StartSeconds = Math.Max(0, SnapToFrame(at + item.Start - baseStart)),
                DurationSeconds = item.Duration,
                InSeconds = item.In,
                FadeCurve = item.FadeCurve,
                Opacity = item.Opacity,
                Volume = item.Volume,
                PlaybackRate = item.PlaybackRate,
                FadeInSeconds = item.FadeIn,
                FadeOutSeconds = item.FadeOut,
                CropLeft = item.CropLeft,
                CropRight = item.CropRight,
                CropTop = item.CropTop,
                CropBottom = item.CropBottom,
                PanX = item.PanX,
                PanY = item.PanY,
                Scale = item.Scale,
                PositionX = item.PositionX,
                PositionY = item.PositionY,
                Effects = item.Effects.Select(e => e.Clone()).ToList(),
                TransitionIn = item.TransitionIn?.Clone(),
                TransitionOut = item.TransitionOut?.Clone(),
                Text = item.Text?.Clone()
            };
            track.Clips.Add(clip);
            pasted.Add(clip);
        }

        if (pasted.Count == 0) return 0;

        ApplySelection(pasted.Select(c => c.Id), pasted[^1].Id);
        NotifyTimelineChanged();
        return pasted.Count;
    }

    private void ApplySelection(IEnumerable<Guid> ids, Guid? primary)
    {
        var list = new List<Guid>();
        foreach (var id in ids)
        {
            if (!list.Contains(id) && FindClip(id) != null) list.Add(id);
        }

        _selectedClipId = primary is { } wanted && list.Contains(wanted)
            ? wanted
            : list.Count > 0 ? list[^1] : (Guid?)null;

        _selectedClipIds.Clear();
        _selectedClipIds.AddRange(list);

        foreach (var track in Tracks)
            foreach (var clip in track.Clips)
                clip.IsSelected = _selectedClipIds.Contains(clip.Id);

        OnPropertyChanged(nameof(SelectedClipId));
        OnPropertyChanged(nameof(SelectedClip));
        OnPropertyChanged(nameof(SelectionCount));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>End of the last clip (0 when the timeline is empty).</summary>
    public double ContentDuration
    {
        get
        {
            double end = 0;
            foreach (var track in Tracks)
                foreach (var clip in track.Clips)
                    end = Math.Max(end, clip.EndSeconds);
            return end;
        }
    }

    public double SnapToFrame(double seconds)
    {
        var snapped = Math.Round(seconds * Fps) / Fps;
        return Math.Max(0, snapped);
    }

    public MediaAsset? FindAsset(Guid id) => Assets.FirstOrDefault(a => a.Id == id);

    public TimelineClip? FindClip(Guid? id)
    {
        if (id == null) return null;
        foreach (var track in Tracks)
            foreach (var clip in track.Clips)
                if (clip.Id == id.Value)
                    return clip;
        return null;
    }

    public TimelineTrack? TrackOf(TimelineClip clip) => Tracks.FirstOrDefault(t => t.Clips.Contains(clip));

    // ---------------------------------------------------------------- assets

    public MediaAsset AddAsset(string path, MediaKind kind, double duration, int width, int height, bool hasAudio)
    {
        var asset = new MediaAsset
        {
            Name = Path.GetFileName(path),
            FilePath = path,
            Kind = kind,
            DurationSeconds = duration,
            Width = width,
            Height = height,
            HasAudio = hasAudio,
            IsMissing = !File.Exists(path)
        };
        Assets.Add(asset);
        return asset;
    }

    public void RemoveAsset(MediaAsset asset)
    {
        foreach (var track in Tracks)
        {
            var clips = track.Clips.Where(c => c.AssetId == asset.Id).ToList();
            foreach (var clip in clips)
            {
                _selectedClipIds.Remove(clip.Id);
                track.Clips.Remove(clip);
            }
        }
        Assets.Remove(asset);
        if (_selectedClipId is { } primary && !_selectedClipIds.Contains(primary))
            _selectedClipId = _selectedClipIds.Count > 0 ? _selectedClipIds[^1] : null;
        RefreshSelectionFlags();
        NotifyTimelineChanged();
    }

    // ---------------------------------------------------------------- tracks

    public TimelineTrack AddTrack(TrackKind kind, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            int n = 1;
            while (Tracks.Any(t => t.Kind == kind && t.Name == $"{kind} {n}")) n++;
            name = $"{kind} {n}";
        }

        var track = new TimelineTrack { Name = name, Kind = kind };
        if (kind == TrackKind.Video)
        {
            int index = Tracks.IndexOf(Tracks.FirstOrDefault(t => t.Kind == TrackKind.Audio)!);
            if (index < 0) Tracks.Add(track);
            else Tracks.Insert(index, track);
        }
        else
        {
            Tracks.Add(track);
        }

        NotifyTimelineChanged();
        return track;
    }

    /// <summary>
    /// First layer that accepts this media and is not locked. Falls back to a
    /// locked layer (so the caller can report it) and only then creates one.
    /// </summary>
    public TimelineTrack EnsureTrack(MediaKind kind)
    {
        var trackKind = kind == MediaKind.Audio ? TrackKind.Audio : TrackKind.Video;

        // Media should not land on a text layer when a picture layer is free.
        if (kind != MediaKind.Text)
        {
            var mediaTrack = Tracks.FirstOrDefault(t =>
                t.Accepts(kind) && !t.IsLocked &&
                !t.Clips.Any(c => c.Kind == MediaKind.Text));
            if (mediaTrack != null) return mediaTrack;
        }

        return Tracks.FirstOrDefault(t => t.Accepts(kind) && !t.IsLocked)
            ?? Tracks.FirstOrDefault(t => t.Accepts(kind))
            ?? AddTrack(trackKind);
    }

    /// <summary>First clock position at or after <paramref name="desired"/> where the clip fits.</summary>
    public double FirstFreeStart(TimelineTrack track, double desired, double duration)
    {
        var candidate = Math.Max(0, SnapToFrame(desired));
        foreach (var other in track.Clips.OrderBy(c => c.StartSeconds))
        {
            if (candidate + duration <= other.StartSeconds + Epsilon) break;
            candidate = Math.Max(candidate, other.EndSeconds);
        }
        return SnapToFrame(candidate);
    }

    // ----------------------------------------------------------------- clips

    public TimelineClip? AddClip(MediaAsset asset, double at, TimelineTrack? track = null,
        bool notify = true, bool exact = false)
    {
        track ??= EnsureTrack(asset.Kind);
        if (!track.Accepts(asset.Kind)) track = EnsureTrack(asset.Kind);
        if (track.IsLocked) return null;

        double duration = asset.Kind == MediaKind.Image
            ? Math.Max(0.1, ImageDurationSeconds)
            : asset.DurationSeconds > 0.05 ? asset.DurationSeconds : 5;

        var start = exact
            ? SnapToFrame(Math.Max(0, at))
            : FirstFreeStart(track, at, duration);
        var clip = new TimelineClip
        {
            AssetId = asset.Id,
            Name = asset.Name,
            Kind = asset.Kind,
            StartSeconds = start,
            DurationSeconds = duration,
            InSeconds = 0
        };
        track.Clips.Add(clip);
        SelectedClipId = clip.Id;
        if (notify) NotifyTimelineChanged();
        return clip;
    }

    public TimelineClip? AddClipAtPlayhead(MediaAsset asset)
    {
        var clip = AddClip(asset, Playhead);
        if (clip == null) return null;
        // For a video track, respect the current selection so "insert" feels
        // intentional: if a video track is selected nothing extra happens.
        return clip;
    }

    /// <summary>
    /// Adds a text event at a project time. Text always gets its own free video
    /// layer (a new top layer when every existing one is busy), so it overlays
    /// the picture instead of becoming an accidental crossfade.
    /// </summary>
    public TimelineClip AddTextClip(TextOverlay text, double start, double duration = 4,
        TimelineTrack? preferredTrack = null)
    {
        start = SnapToFrame(Math.Max(0, start));
        duration = Math.Max(FrameDuration, SnapToFrame(duration));
        var end = start + duration;

        static bool Occupied(TimelineTrack track, double from, double to) =>
            track.Clips.Any(c => from < c.EndSeconds - Epsilon && to > c.StartSeconds + Epsilon);

        var track = preferredTrack;
        if (track == null || track.Kind != TrackKind.Video || track.IsLocked ||
            Occupied(track, start, end))
        {
            track = Tracks.FirstOrDefault(t =>
                t.Kind == TrackKind.Video && !t.IsLocked && !Occupied(t, start, end));
        }

        if (track == null)
        {
            int number = 1;
            while (Tracks.Any(t => t.Name == $"Text {number}")) number++;
            track = new TimelineTrack { Name = $"Text {number}", Kind = TrackKind.Video };
            Tracks.Insert(0, track);
        }

        var clip = new TimelineClip
        {
            AssetId = Guid.Empty,
            Name = text.DisplayName,
            Kind = MediaKind.Text,
            StartSeconds = start,
            DurationSeconds = duration,
            InSeconds = 0,
            Text = text
        };

        track.Clips.Add(clip);
        SelectedClipId = clip.Id;
        NotifyTimelineChanged();
        return clip;
    }

    /// <summary>Marks the project dirty after a live text edit.</summary>
    public void NotifyTextChanged() => NotifyTimelineChanged();

    public TimelineClip? GetClipAt(double seconds, TrackKind kind)
    {
        var soloed = kind == TrackKind.Audio && Tracks.Any(t => t.Kind == TrackKind.Audio && t.IsSolo);
        foreach (var track in Tracks)
        {
            if (track.Kind != kind) continue;
            if (kind == TrackKind.Audio && track.IsMuted) continue;
            if (soloed && !track.IsSolo) continue;
            foreach (var clip in track.Clips.OrderBy(c => c.StartSeconds))
                if (seconds >= clip.StartSeconds - Epsilon && seconds < clip.EndSeconds - Epsilon)
                    return clip;
        }
        return null;
    }

    /// <summary>Topmost visual clip at a project time (video track order wins).</summary>
    public TimelineClip? GetVisualClipAt(double seconds) => GetClipAt(seconds, TrackKind.Video);

    public TimelineClip? GetAudioClipAt(double seconds) => GetClipAt(seconds, TrackKind.Audio);

    /// <summary>The clip that should be previewed at a given project time.</summary>
    public TimelineClip? GetPreviewClipAt(double seconds) =>
        GetVisualClipAt(seconds) ?? GetAudioClipAt(seconds);

    public double ClampMove(TimelineClip clip, double desiredStart)
    {
        // Overlaps are intentional: they become crossfades. Only keep the
        // clip from moving before the start of the timeline.
        return SnapToFrame(Math.Max(0, desiredStart));
    }

    public void MoveClip(TimelineClip clip, double desiredStart)
    {
        var track = TrackOf(clip);
        if (track == null || track.IsLocked) return;
        clip.StartSeconds = ClampMove(clip, desiredStart);
    }

    /// <summary>Moves a clip to another compatible layer, keeping its time position.</summary>
    public bool MoveClipToTrack(TimelineClip clip, TimelineTrack target)
    {
        var source = TrackOf(clip);
        if (source == null || ReferenceEquals(source, target)) return false;
        if (source.IsLocked || target.IsLocked) return false;
        if (!target.Accepts(clip.Kind)) return false;

        // Text and picture may share a layer only at different times; an
        // overlap would turn the text into a crossfade instead of an overlay.
        if (clip.Kind == MediaKind.Text)
        {
            if (target.Clips.Any(c => c.Kind != MediaKind.Text && Overlaps(c, clip)))
                return false;
        }
        else if (target.Clips.Any(c => c.Kind == MediaKind.Text && Overlaps(c, clip)))
        {
            return false;
        }

        source.Clips.Remove(clip);
        target.Clips.Add(clip);
        SelectedClipId = clip.Id;
        NotifyTimelineChanged();
        return true;
    }

    private static bool Overlaps(TimelineClip left, TimelineClip right) =>
        left.StartSeconds < right.EndSeconds - Epsilon &&
        left.EndSeconds > right.StartSeconds + Epsilon;

    public void TrimClipLeft(TimelineClip clip, double desiredStart)
    {
        var track = TrackOf(clip);
        if (track == null || track.IsLocked) return;

        if (clip.Kind == MediaKind.Text)
        {
            // Text has no source in-point; moving the left edge just changes
            // how long the text event lasts.
            var textMinStart = 0.0;
            var textMaxStart = clip.EndSeconds - FrameDuration;
            var textStart = SnapToFrame(Math.Clamp(desiredStart, textMinStart,
                Math.Max(textMinStart, textMaxStart)));
            var textDelta = textStart - clip.StartSeconds;
            clip.StartSeconds = textStart;
            clip.DurationSeconds = Math.Max(FrameDuration, clip.DurationSeconds - textDelta);
            return;
        }

        var rate = Math.Max(0.1, clip.PlaybackRate);
        // Limits come from the source media only; neighbours may be overlapped.
        var minStart = Math.Max(0, clip.StartSeconds - clip.InSeconds / rate);
        var maxStart = clip.EndSeconds - FrameDuration;
        var newStart = SnapToFrame(Math.Clamp(desiredStart, minStart, Math.Max(minStart, maxStart)));
        var delta = newStart - clip.StartSeconds;

        var asset = FindAsset(clip.AssetId);
        var available = asset != null ? Math.Max(0, asset.DurationSeconds - clip.InSeconds) : 0;
        var newIn = clip.InSeconds + delta * rate;
        if (available > 0.05 &&
            clip.DurationSeconds * rate > available + 0.01 &&
            newIn > clip.InSeconds + available)
        {
            // Trimming into a repeated pass: wrap back inside the file.
            newIn = clip.InSeconds + ((newIn - clip.InSeconds) % available);
        }

        clip.StartSeconds = newStart;
        clip.InSeconds = Math.Max(0, newIn);
        clip.DurationSeconds = Math.Max(FrameDuration, clip.DurationSeconds - delta);
    }

    public void TrimClipRight(TimelineClip clip, double desiredEnd)
    {
        var track = TrackOf(clip);
        if (track == null || track.IsLocked) return;

        // Dragging past the end of the source is allowed: the timeline repeats
        // the media (see TimelinePreview). Ctrl-drag uses StretchRight instead.
        var minEnd = clip.StartSeconds + FrameDuration;
        var newEnd = SnapToFrame(Math.Max(minEnd, desiredEnd));
        clip.DurationSeconds = Math.Max(FrameDuration, newEnd - clip.StartSeconds);
    }

    public TimelineClip? SplitClip(TimelineClip clip, double at)
    {
        var track = TrackOf(clip);
        if (track == null || track.IsLocked) return null;

        var splitAt = SnapToFrame(at);
        if (splitAt <= clip.StartSeconds + FrameDuration * 0.5) return null;
        if (splitAt >= clip.EndSeconds - FrameDuration * 0.5) return null;

        var right = new TimelineClip
        {
            AssetId = clip.AssetId,
            Name = clip.Name,
            Kind = clip.Kind,
            StartSeconds = splitAt,
            DurationSeconds = clip.EndSeconds - splitAt,
            InSeconds = SourceInAfterSplit(clip, splitAt),
            FadeCurve = FadeCurveKind.Linear,
            Opacity = clip.Opacity,
            Volume = clip.Volume,
            PlaybackRate = clip.PlaybackRate,
            FadeInSeconds = 0,
            FadeOutSeconds = Math.Min(clip.FadeOutSeconds, clip.EndSeconds - splitAt),
            CropLeft = clip.CropLeft,
            CropRight = clip.CropRight,
            CropTop = clip.CropTop,
            CropBottom = clip.CropBottom,
            PanX = clip.PanX,
            PanY = clip.PanY,
            Scale = clip.Scale,
            PositionX = clip.PositionX,
            PositionY = clip.PositionY,
            Effects = clip.Effects.Select(e => e.Clone()).ToList(),
            TransitionOut = clip.TransitionOut?.Clone(),
            Text = clip.Text?.Clone()
        };

        clip.DurationSeconds = splitAt - clip.StartSeconds;
        clip.FadeOutSeconds = 0;
        clip.TransitionOut = null;
        clip.FadeInSeconds = Math.Min(clip.FadeInSeconds, clip.DurationSeconds);
        track.Clips.Add(right);
        SelectedClipId = right.Id;
        NotifyTimelineChanged();
        return right;
    }

    /// <summary>True when at least one unlocked clip straddles the playhead.</summary>
    public bool HasClipAtPlayhead() =>
        Tracks.Any(t => !t.IsLocked && t.Clips.Any(c =>
            Playhead > c.StartSeconds + FrameDuration * 0.5 &&
            Playhead < c.EndSeconds - FrameDuration * 0.5));

    /// <summary>
    /// Splits every clip under the playhead on every unlocked layer, so a cut
    /// lands on the picture and its audio together. Returns how many clips split.
    /// </summary>
    public int SplitAllAtPlayhead()
    {
        var split = 0;
        foreach (var track in Tracks.ToList())
        {
            if (track.IsLocked) continue;

            foreach (var clip in track.Clips.OrderBy(c => c.StartSeconds).ToList())
            {
                if (Playhead <= clip.StartSeconds + FrameDuration * 0.5) continue;
                if (Playhead >= clip.EndSeconds - FrameDuration * 0.5) continue;
                if (SplitClip(clip, Playhead) != null) split++;
            }
        }

        return split;
    }

    /// <summary>
    /// Source in-point for the right side of a split. When the left clip is
    /// looping, wrap the source offset so the new clip stays inside the file.
    /// </summary>
    private double SourceInAfterSplit(TimelineClip clip, double splitAt)
    {
        if (clip.Kind == MediaKind.Text) return 0;

        var rate = Math.Max(0.1, clip.PlaybackRate);
        var rawOffset = (splitAt - clip.StartSeconds) * rate;
        var asset = FindAsset(clip.AssetId);
        var available = asset != null ? Math.Max(0, asset.DurationSeconds - clip.InSeconds) : 0;

        if (available > 0.05 && clip.DurationSeconds * rate > available + 0.01)
            rawOffset %= available;

        return clip.InSeconds + rawOffset;
    }

    public TimelineClip? DuplicateClip(TimelineClip clip)
    {
        var track = TrackOf(clip);
        if (track == null || track.IsLocked) return null;

        var start = FirstFreeStart(track, clip.EndSeconds, clip.DurationSeconds);
        var copy = new TimelineClip
        {
            AssetId = clip.AssetId,
            Name = clip.Name,
            Kind = clip.Kind,
            StartSeconds = start,
            DurationSeconds = clip.DurationSeconds,
            InSeconds = clip.InSeconds,
            FadeCurve = clip.FadeCurve,
            Opacity = clip.Opacity,
            Volume = clip.Volume,
            PlaybackRate = clip.PlaybackRate,
            FadeInSeconds = clip.FadeInSeconds,
            FadeOutSeconds = clip.FadeOutSeconds,
            CropLeft = clip.CropLeft,
            CropRight = clip.CropRight,
            CropTop = clip.CropTop,
            CropBottom = clip.CropBottom,
            PanX = clip.PanX,
            PanY = clip.PanY,
            Scale = clip.Scale,
            PositionX = clip.PositionX,
            PositionY = clip.PositionY,
            Effects = clip.Effects.Select(e => e.Clone()).ToList(),
            TransitionIn = clip.TransitionIn?.Clone(),
            TransitionOut = clip.TransitionOut?.Clone(),
            Text = clip.Text?.Clone()
        };
        track.Clips.Add(copy);
        SelectedClipId = copy.Id;
        NotifyTimelineChanged();
        return copy;
    }

    // ---------------------------------------------------------------- effects

    /// <summary>Appends an effect to a clip with the catalog defaults.</summary>
    public ClipEffect AddEffect(TimelineClip clip, EffectKind kind)
    {
        var effect = EffectCatalog.Create(kind);
        clip.Effects.Add(effect);
        NotifyTimelineChanged();
        return effect;
    }

    public bool RemoveEffect(TimelineClip clip, ClipEffect effect)
    {
        if (!clip.Effects.Remove(effect)) return false;
        NotifyTimelineChanged();
        return true;
    }

    /// <summary>Moves an effect up (-1) or down (+1) in the stack.</summary>
    public bool MoveEffect(TimelineClip clip, ClipEffect effect, int delta)
    {
        var index = clip.Effects.IndexOf(effect);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= clip.Effects.Count) return false;

        clip.Effects.RemoveAt(index);
        clip.Effects.Insert(target, effect);
        NotifyTimelineChanged();
        return true;
    }

    /// <summary>Marks the project dirty after live effect parameter edits.</summary>
    public void NotifyEffectChanged() => NotifyTimelineChanged();

    // ------------------------------------------------------------ transitions

    /// <summary>
    /// Replaces the transition on one clip edge. <paramref name="outgoing"/>
    /// selects the end of the clip; otherwise the start.
    /// </summary>
    public ClipTransition SetTransition(TimelineClip clip, bool outgoing,
        TransitionKind kind, IReadOnlyDictionary<string, double>? values = null)
    {
        var transition = TransitionCatalog.Create(kind);
        if (values != null)
        {
            foreach (var pair in values)
                transition.Set(pair.Key, pair.Value);
        }

        if (outgoing) clip.TransitionOut = transition;
        else clip.TransitionIn = transition;

        NotifyTimelineChanged();
        return transition;
    }

    public bool RemoveTransition(TimelineClip clip, bool outgoing)
    {
        var had = outgoing ? clip.TransitionOut != null : clip.TransitionIn != null;
        if (!had) return false;

        if (outgoing) clip.TransitionOut = null;
        else clip.TransitionIn = null;

        NotifyTimelineChanged();
        return true;
    }

    /// <summary>Marks the project dirty after a live transition parameter edit.</summary>
    public void NotifyTransitionChanged() => NotifyTimelineChanged();

    public bool DeleteClip(TimelineClip clip)
    {
        var track = TrackOf(clip);
        if (track == null || track.IsLocked) return false;

        _selectedClipIds.Remove(clip.Id);
        if (_selectedClipId == clip.Id)
            _selectedClipId = _selectedClipIds.Count > 0 ? _selectedClipIds[^1] : null;
        track.Clips.Remove(clip);
        RefreshSelectionFlags();
        NotifyTimelineChanged();
        return true;
    }

    public bool DeleteSelectedClip() => SelectedClip != null && DeleteClip(SelectedClip);

    /// <summary>Deletes every selected clip on an unlocked layer. Returns how many went.</summary>
    public int DeleteSelectedClips()
    {
        var clips = SelectedClips()
            .Where(c => TrackOf(c) is { IsLocked: false })
            .ToList();
        if (clips.Count == 0) return 0;

        ApplySelection(Array.Empty<Guid>(), null);
        foreach (var clip in clips) TrackOf(clip)?.Clips.Remove(clip);
        NotifyTimelineChanged();
        return clips.Count;
    }

    public int DeleteSelectedClipsAndCloseGaps()
    {
        var tracks = SelectedClips()
            .Select(TrackOf)
            .Where(t => t is { IsLocked: false })
            .Cast<TimelineTrack>()
            .Distinct()
            .ToList();
        if (tracks.Count == 0) return 0;

        var deleted = DeleteSelectedClips();
        foreach (var track in tracks) CloseGaps(track);
        return deleted;
    }

    /// <summary>
    /// Moves every selected clip by the same offset. The offset is limited so
    /// the first clip cannot cross zero, which would desync the group.
    /// </summary>
    public int NudgeSelectedClips(double delta)
    {
        var clips = SelectedClips()
            .Where(c => TrackOf(c) is { IsLocked: false })
            .ToList();
        if (clips.Count == 0) return 0;

        var minStart = clips.Min(c => c.StartSeconds);
        if (minStart + delta < 0) delta = -minStart;
        if (Math.Abs(delta) < Epsilon) return 0;

        foreach (var clip in clips)
            clip.StartSeconds = ClampMove(clip, clip.StartSeconds + delta);

        NotifyTimelineChanged();
        return clips.Count;
    }

    /// <summary>
    /// Deletes a clip and pulls the rest of its layer left by the clip length,
    /// so the cut closes instead of leaving a hole.
    /// </summary>
    public bool DeleteClipAndCloseGap(TimelineClip clip)
    {
        var track = TrackOf(clip);
        if (track == null || track.IsLocked) return false;

        var start = clip.StartSeconds;
        var duration = clip.DurationSeconds;
        _selectedClipIds.Remove(clip.Id);
        if (_selectedClipId == clip.Id)
            _selectedClipId = _selectedClipIds.Count > 0 ? _selectedClipIds[^1] : null;
        track.Clips.Remove(clip);
        RefreshSelectionFlags();

        foreach (var other in track.Clips.Where(c => c.StartSeconds >= start - Epsilon))
            other.StartSeconds = Math.Max(0, other.StartSeconds - duration);

        NotifyTimelineChanged();
        return true;
    }

    /// <summary>Moves the next clip (and everything after it) back against this clip.</summary>
    public bool CloseGapAfter(TimelineClip clip)
    {
        var track = TrackOf(clip);
        if (track == null || track.IsLocked) return false;

        var next = track.Clips
            .Where(c => !ReferenceEquals(c, clip) && c.StartSeconds >= clip.EndSeconds - Epsilon)
            .OrderBy(c => c.StartSeconds)
            .FirstOrDefault();
        if (next == null) return false;

        var shift = next.StartSeconds - clip.EndSeconds;
        if (shift <= FrameDuration * 0.5) return false;

        foreach (var other in track.Clips.Where(c => c.StartSeconds >= next.StartSeconds - Epsilon))
            other.StartSeconds = Math.Max(0, other.StartSeconds - shift);

        NotifyTimelineChanged();
        return true;
    }

    /// <summary>True when any clip on the layer starts after the previous one ends.</summary>
    public bool HasGaps(TimelineTrack track)
    {
        var clips = track.Clips.OrderBy(c => c.StartSeconds).ToList();
        if (clips.Count < 2) return false;

        var cursor = clips[0].EndSeconds;
        for (var i = 1; i < clips.Count; i++)
        {
            if (clips[i].StartSeconds > cursor + FrameDuration * 0.5) return true;
            cursor = Math.Max(cursor, clips[i].EndSeconds);
        }
        return false;
    }

    /// <summary>
    /// Pulls every clip on a layer back against the previous one, closing gaps
    /// without touching intentional overlaps (crossfades). Returns how many gaps
    /// were closed.
    /// </summary>
    public int CloseGaps(TimelineTrack track)
    {
        if (track.IsLocked || !HasGaps(track)) return 0;

        var clips = track.Clips
            .OrderBy(c => c.StartSeconds)
            .ThenBy(c => c.Id)
            .ToList();
        if (clips.Count < 2) return 0;

        var closed = 0;
        var cursor = clips[0].EndSeconds;
        for (var i = 1; i < clips.Count; i++)
        {
            var clip = clips[i];
            if (clip.StartSeconds > cursor + FrameDuration * 0.5)
            {
                clip.StartSeconds = SnapToFrame(cursor);
                closed++;
            }
            cursor = Math.Max(cursor, clip.EndSeconds);
        }

        if (closed > 0) NotifyTimelineChanged();
        return closed;
    }

    /// <summary>
    /// Nearest clip edge (or timeline start/end) in a direction, used for
    /// jumping the playhead between cuts. <paramref name="direction"/> is -1 or 1.
    /// </summary>
    public double? FindEditPoint(double from, int direction)
    {
        if (direction == 0) return null;

        var best = direction > 0 ? double.MaxValue : double.MinValue;
        var found = false;
        var limit = FrameDuration * 0.25;

        void Consider(double time)
        {
            if (direction > 0)
            {
                if (time <= from + limit || time >= best) return;
            }
            else
            {
                if (time >= from - limit || time <= best) return;
            }

            best = time;
            found = true;
        }

        Consider(0);
        Consider(ContentDuration);
        foreach (var track in Tracks)
            foreach (var clip in track.Clips)
            {
                Consider(clip.StartSeconds);
                Consider(clip.EndSeconds);
            }

        return found ? best : null;
    }

    public bool RemoveTrack(TimelineTrack track)
    {
        if (!Tracks.Contains(track)) return false;

        foreach (var clip in track.Clips) _selectedClipIds.Remove(clip.Id);
        if (_selectedClipId is { } primary && !_selectedClipIds.Contains(primary))
            _selectedClipId = _selectedClipIds.Count > 0 ? _selectedClipIds[^1] : null;
        RefreshSelectionFlags();

        Tracks.Remove(track);
        NotifyTimelineChanged();
        return true;
    }

    /// <summary>Overlap between adjacent clips on a track — the crossfade region.</summary>
    public sealed record ClipOverlap(
        TimelineClip Outgoing, TimelineClip Incoming, double StartSeconds, double EndSeconds);

    public IEnumerable<ClipOverlap> GetOverlaps(TimelineTrack track)
    {
        var clips = track.Clips
            .OrderBy(c => c.StartSeconds)
            .ThenBy(c => c.Id)
            .ToList();

        for (var i = 0; i < clips.Count - 1; i++)
        {
            var outgoing = clips[i];
            var incoming = clips[i + 1];
            var start = Math.Max(outgoing.StartSeconds, incoming.StartSeconds);
            var end = Math.Min(outgoing.EndSeconds, incoming.EndSeconds);
            if (end - start > FrameDuration * 0.25)
                yield return new ClipOverlap(outgoing, incoming, start, end);
        }
    }

    /// <summary>The clip that should be previewed next when playback leaves the current clip.</summary>
    public TimelineClip? FindNextPreviewClip(double afterSeconds)
    {
        // An overlapping incoming clip may already contain the boundary. Keep
        // playing it instead of skipping to a later clip.
        var containing = GetPreviewClipAt(afterSeconds + Epsilon);
        if (containing != null) return containing;

        TimelineClip? best = null;
        foreach (var track in Tracks)
        {
            if (track.Kind == TrackKind.Audio && track.IsMuted) continue;
            foreach (var clip in track.Clips)
            {
                if (clip.StartSeconds < afterSeconds - Epsilon) continue;
                if (best == null || clip.StartSeconds < best.StartSeconds)
                    best = clip;
            }
        }
        return best;
    }

    // ------------------------------------------------------------- lifecycle

    public void BeginEdit() => Undo.Capture(this);

    /// <summary>Pushes a snapshot captured before a live-editing dialog opened.</summary>
    public void BeginEdit(ProjectFile snapshot) => Undo.CaptureState(snapshot);

    public void NotifyTimelineChanged()
    {
        OnPropertyChanged(nameof(ContentDuration));
        TimelineChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyReloaded()
    {
        RefreshSelectionFlags();
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
        OnPropertyChanged(nameof(Fps));
        OnPropertyChanged(nameof(ResolutionLabel));
        OnPropertyChanged(nameof(FpsLabel));
        OnPropertyChanged(nameof(ContentDuration));
        OnPropertyChanged(nameof(Playhead));
        TimelineChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        PlayheadChanged?.Invoke(this, EventArgs.Empty);
        RangeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshSelectionFlags()
    {
        foreach (var track in Tracks)
            foreach (var clip in track.Clips)
                clip.IsSelected = _selectedClipIds.Contains(clip.Id);
        OnPropertyChanged(nameof(SelectedClip));
    }

    /// <summary>
    /// Re-point selection after tracks were rebuilt (load / undo). Unlike the
    /// property setter this also refreshes the new clip instances' flags.
    /// </summary>
    public void RestoreSelection(Guid? clipId) =>
        RestoreSelection(clipId is { } id ? new[] { id } : Array.Empty<Guid>(), clipId);

    public void RestoreSelection(IReadOnlyList<Guid> clipIds, Guid? primary)
    {
        _selectedClipIds.Clear();
        foreach (var id in clipIds)
            if (FindClip(id) != null) _selectedClipIds.Add(id);

        _selectedClipId = primary is { } wanted && _selectedClipIds.Contains(wanted)
            ? wanted
            : _selectedClipIds.Count > 0 ? _selectedClipIds[^1] : (Guid?)null;

        RefreshSelectionFlags();
        OnPropertyChanged(nameof(SelectedClipId));
        OnPropertyChanged(nameof(SelectionCount));
    }

    private void OnTracksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Track list changes are structural; callers notify explicitly.
    }
}
