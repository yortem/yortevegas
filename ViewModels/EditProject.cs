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

    public Guid? SelectedClipId
    {
        get => _selectedClipId;
        set
        {
            if (_selectedClipId == value) return;
            var previous = FindClip(_selectedClipId);
            if (previous != null) previous.IsSelected = false;
            _selectedClipId = value;
            var current = FindClip(_selectedClipId);
            if (current != null) current.IsSelected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedClip));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public TimelineClip? SelectedClip => FindClip(_selectedClipId);

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
                if (clip.Id == SelectedClipId) SelectedClipId = null;
                track.Clips.Remove(clip);
            }
        }
        Assets.Remove(asset);
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

    public TimelineTrack EnsureTrack(MediaKind kind)
    {
        var trackKind = kind == MediaKind.Audio ? TrackKind.Audio : TrackKind.Video;
        return Tracks.FirstOrDefault(t => t.Accepts(kind)) ?? AddTrack(trackKind);
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

    public TimelineClip? AddClip(MediaAsset asset, double at, TimelineTrack? track = null)
    {
        track ??= EnsureTrack(asset.Kind);
        if (!track.Accepts(asset.Kind)) track = EnsureTrack(asset.Kind);
        if (track.IsLocked) return null;

        double duration = asset.Kind == MediaKind.Image
            ? Math.Max(0.1, ImageDurationSeconds)
            : asset.DurationSeconds > 0.05 ? asset.DurationSeconds : 5;

        var start = FirstFreeStart(track, at, duration);
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
        NotifyTimelineChanged();
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

        source.Clips.Remove(clip);
        target.Clips.Add(clip);
        SelectedClipId = clip.Id;
        NotifyTimelineChanged();
        return true;
    }

    public void TrimClipLeft(TimelineClip clip, double desiredStart)
    {
        var track = TrackOf(clip);
        if (track == null || track.IsLocked) return;

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
            PanY = clip.PanY
        };

        clip.DurationSeconds = splitAt - clip.StartSeconds;
        clip.FadeOutSeconds = 0;
        clip.FadeInSeconds = Math.Min(clip.FadeInSeconds, clip.DurationSeconds);
        track.Clips.Add(right);
        SelectedClipId = right.Id;
        NotifyTimelineChanged();
        return right;
    }

    public bool SplitAtPlayhead()
    {
        var target = SelectedClip;
        if (target == null || Playhead <= target.StartSeconds || Playhead >= target.EndSeconds)
            target = GetPreviewClipAt(Playhead);
        if (target == null) return false;
        return SplitClip(target, Playhead) != null;
    }

    /// <summary>
    /// Source in-point for the right side of a split. When the left clip is
    /// looping, wrap the source offset so the new clip stays inside the file.
    /// </summary>
    private double SourceInAfterSplit(TimelineClip clip, double splitAt)
    {
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
            PanY = clip.PanY
        };
        track.Clips.Add(copy);
        SelectedClipId = copy.Id;
        NotifyTimelineChanged();
        return copy;
    }

    public bool DeleteClip(TimelineClip clip)
    {
        var track = TrackOf(clip);
        if (track == null || track.IsLocked) return false;
        if (clip.Id == SelectedClipId) SelectedClipId = null;
        track.Clips.Remove(clip);
        NotifyTimelineChanged();
        return true;
    }

    public bool DeleteSelectedClip() => SelectedClip != null && DeleteClip(SelectedClip);

    public bool RemoveTrack(TimelineTrack track)
    {
        if (!Tracks.Contains(track)) return false;
        if (SelectedClip != null && track.Clips.Contains(SelectedClip))
            SelectedClipId = null;
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
    }

    public void RefreshSelectionFlags()
    {
        foreach (var track in Tracks)
            foreach (var clip in track.Clips)
                clip.IsSelected = clip.Id == SelectedClipId;
        OnPropertyChanged(nameof(SelectedClip));
    }

    /// <summary>
    /// Re-point selection after tracks were rebuilt (load / undo). Unlike the
    /// property setter this also refreshes the new clip instances' flags.
    /// </summary>
    public void RestoreSelection(Guid? clipId)
    {
        _selectedClipId = FindClip(clipId)?.Id;
        RefreshSelectionFlags();
        OnPropertyChanged(nameof(SelectedClipId));
    }

    private void OnTracksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Track list changes are structural; callers notify explicitly.
    }
}
