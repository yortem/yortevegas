using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using YorteVegas.Controls;
using YorteVegas.Models;
using YorteVegas.ViewModels;

namespace YorteVegas.Services;

/// <summary>
/// Drives the preview compositor from the edit. Every visual clip under the
/// playhead becomes a layer (bottom track first); opacity and overlap fades are
/// applied as per-layer alpha, so the preview shows the same stacking the
/// timeline describes. The playhead is driven by a wall clock, never by a player.
/// </summary>
public sealed class TimelinePreview
{
    private readonly PreviewCompositor _compositor;
    private readonly DispatcherTimer _timer;

    private EditProject _project;
    private bool _isPlaying;
    private bool _updatingPlayhead;
    private bool _hasMedia;
    private bool _hasVisual;
    private double _baseVolume = 100;

    private double _anchorProject;
    private DateTime _anchorWall = DateTime.UtcNow;

    private readonly Dictionary<Guid, long> _tokens = new();
    private readonly HashSet<Guid> _lastActive = new();
    private long _serial;
    private bool _globalSeek = true;
    private bool _seekQueued;
    private DateTime _seekDue;
    private string _lastError = "";

    public TimelinePreview(PreviewCompositor compositor, EditProject project)
    {
        _compositor = compositor;
        _project = project;

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    public bool IsPlaying => _isPlaying;
    public bool HasMedia => _hasMedia;
    public bool HasVisual => _hasVisual;
    public string CompositorError => _compositor.Error ?? "";

    public void SetBaseVolume(double value)
    {
        _baseVolume = Math.Clamp(value, 0, 100);
        ApplyNow();
    }

    public event EventHandler? PlayStateChanged;
    public event EventHandler? MediaStateChanged;

    public void AttachProject(EditProject project)
    {
        if (ReferenceEquals(_project, project)) return;
        _project = project;
        Pause();
        _tokens.Clear();
        _lastActive.Clear();
        _globalSeek = true;
        ApplyNow();
    }

    public void OnProjectChanged()
    {
        if (!_isPlaying) _globalSeek = true;
        ApplyNow();
    }

    public void OnPlayheadChanged()
    {
        if (_updatingPlayhead) return;

        if (_isPlaying) Pause();
        _globalSeek = true;

        // Dragging the playhead fires this on every mouse move. Seeking every
        // layer on each event stalls the UI, so coalesce scrubs into ~18 fps.
        _seekQueued = true;
        _seekDue = DateTime.UtcNow.AddMilliseconds(55);
    }

    public void Play()
    {
        if (_isPlaying) return;
        if (_project.ContentDuration <= 0) return;

        var time = _project.Playhead;
        if (time >= _project.ContentDuration - 1e-6) time = 0;
        SetPlayhead(time);
        if (_project.GetPreviewClipAt(time) == null &&
            _project.FindNextPreviewClip(time) == null) return;

        _isPlaying = true;
        _anchorProject = time;
        _anchorWall = DateTime.UtcNow;
        _globalSeek = true;
        ApplyNow();
        _compositor.SetPlaying(true);
        PlayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        var wasPlaying = _isPlaying;
        _isPlaying = false;
        _compositor.SetPlaying(false);
        if (wasPlaying) PlayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void TogglePlay()
    {
        if (_isPlaying) Pause();
        else Play();
    }

    public void SeekToStart()
    {
        Pause();
        SetPlayhead(0);
        _globalSeek = true;
        ApplyNow();
    }

    public void SeekToEnd()
    {
        Pause();
        SetPlayhead(_project.ContentDuration);
        _globalSeek = true;
        ApplyNow();
    }

    public void StepFrames(int frames)
    {
        Pause();
        SetPlayhead(_project.Playhead + frames * _project.FrameDuration);
        _globalSeek = true;
        ApplyNow();
    }

    public void Refresh()
    {
        _globalSeek = true;
        ApplyNow();
    }

    public void RefreshClipVolume() => ApplyNow();

    private void OnTick()
    {
        if (_isPlaying)
        {
            var time = _anchorProject + (DateTime.UtcNow - _anchorWall).TotalSeconds;
            if (time >= _project.ContentDuration)
            {
                SetPlayhead(_project.ContentDuration);
                Pause();
                ApplyNow();
                return;
            }

            SetPlayhead(time);
            ApplyNow();
        }
        else if (_globalSeek && _seekQueued && DateTime.UtcNow >= _seekDue)
        {
            _seekQueued = false;
            ApplyNow();
        }
    }

    private void ApplyNow()
    {
        if (_project == null) return;

        var time = _isPlaying
            ? _anchorProject + (DateTime.UtcNow - _anchorWall).TotalSeconds
            : _project.Playhead;

        var specs = BuildLayers(time);
        var pending = BuildPending(time, specs);
        if (pending != null) specs.Add(pending);

        _compositor.FrameAspect = _project.Height > 0
            ? (double)_project.Width / _project.Height
            : 16.0 / 9.0;
        _compositor.SetLayers(specs);
        _seekQueued = false;

        _lastActive.Clear();
        foreach (var spec in specs) _lastActive.Add(spec.Key);
        _globalSeek = false;

        var hasMedia = specs.Count > 0;
        var hasVisual = specs.Any(s => s.HasVisual);
        if (hasMedia != _hasMedia)
        {
            _hasMedia = hasMedia;
            MediaStateChanged?.Invoke(this, EventArgs.Empty);
        }
        if (hasVisual != _hasVisual)
        {
            _hasVisual = hasVisual;
            MediaStateChanged?.Invoke(this, EventArgs.Empty);
        }

        var error = _compositor.Error ?? "";
        if (!string.Equals(error, _lastError))
        {
            _lastError = error;
            MediaStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private List<PreviewLayer> BuildLayers(double time)
    {
        var specs = new List<PreviewLayer>(4);
        var anySolo = _project.Tracks.Any(t => t.IsSolo);

        // Visual layers, bottom track first (Video N ... Video 1).
        foreach (var track in _project.Tracks.Reverse())
        {
            if (track.Kind != TrackKind.Video) continue;

            var active = track.Clips
                .Where(c => time >= c.StartSeconds - Epsilon && time < c.EndSeconds - Epsilon)
                .OrderBy(c => c.StartSeconds)
                .ToList();
            if (active.Count == 0) continue;

            var overlap = _project.GetOverlaps(track)
                .FirstOrDefault(o => time >= o.StartSeconds && time < o.EndSeconds);

            var audible = !track.IsMuted && (!anySolo || track.IsSolo);

            foreach (var clip in active)
            {
                var alpha = clip.Opacity;
                var gain = audible ? clip.Volume : 0;

                if (overlap != null)
                {
                    var progress = Math.Clamp(
                        (time - overlap.StartSeconds) /
                        Math.Max(0.0001, overlap.EndSeconds - overlap.StartSeconds), 0, 1);
                    var eased = Ease(progress, clip.FadeCurve);

                    if (clip.Id == overlap.Incoming.Id)
                    {
                        alpha *= eased;
                        gain *= eased;
                    }
                    else if (clip.Id == overlap.Outgoing.Id)
                    {
                        gain *= 1 - eased;
                    }
                }

                AddLayer(specs, clip, time, alpha, gain, visual: true);
            }
        }

        // Audio-only layers.
        foreach (var track in _project.Tracks)
        {
            if (track.Kind != TrackKind.Audio) continue;
            if (track.IsMuted || (anySolo && !track.IsSolo)) continue;

            var active = track.Clips
                .Where(c => time >= c.StartSeconds - Epsilon && time < c.EndSeconds - Epsilon)
                .OrderBy(c => c.StartSeconds)
                .ToList();
            if (active.Count == 0) continue;

            var overlap = _project.GetOverlaps(track)
                .FirstOrDefault(o => time >= o.StartSeconds && time < o.EndSeconds);

            foreach (var clip in active)
            {
                var gain = clip.Volume;
                if (overlap != null)
                {
                    var progress = Math.Clamp(
                        (time - overlap.StartSeconds) /
                        Math.Max(0.0001, overlap.EndSeconds - overlap.StartSeconds), 0, 1);
                    var eased = Ease(progress, clip.FadeCurve);

                    if (clip.Id == overlap.Incoming.Id) gain *= eased;
                    else if (clip.Id == overlap.Outgoing.Id) gain *= 1 - eased;
                }

                AddLayer(specs, clip, time, alpha: 1, gain, visual: false);
            }
        }

        return specs;
    }

    /// <summary>
    /// Loads the next clip ahead of time so a cut does not wait for mpv to open
    /// the file. The layer stays paused and silent until it becomes active.
    /// </summary>
    private PreviewLayer? BuildPending(double time, List<PreviewLayer> active)
    {
        TimelineClip? next = null;
        foreach (var track in _project.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (active.Any(s => s.Key == clip.Id)) continue;
                if (clip.StartSeconds <= time + 0.05) continue;
                if (next == null || clip.StartSeconds < next.StartSeconds) next = clip;
            }
        }

        if (next == null || next.StartSeconds - time > 3.0) return null;

        var asset = _project.FindAsset(next.AssetId);
        if (asset == null || asset.IsMissing || !File.Exists(asset.FilePath)) return null;

        if (_globalSeek || !_lastActive.Contains(next.Id) ||
            !_tokens.TryGetValue(next.Id, out var token))
        {
            token = ++_serial;
            _tokens[next.Id] = token;
        }

        return new PreviewLayer
        {
            Key = next.Id,
            Path = asset.FilePath,
            SourceSeconds = next.Kind == MediaKind.Image ? 0 : next.InSeconds,
            SeekToken = token,
            Volume = 0,
            Opacity = 0,
            Rate = Math.Max(0.1, next.PlaybackRate),
            LoopA = -1,
            LoopB = -1,
            HasVisual = next.Kind != MediaKind.Audio,
            Aspect = asset.Width > 0 && asset.Height > 0
                ? (double)asset.Width / asset.Height
                : 16.0 / 9.0,
            Still = next.Kind == MediaKind.Image,
            Pending = true
        };
    }

    private void AddLayer(List<PreviewLayer> specs, TimelineClip clip, double time,
        double alpha, double gain, bool visual)
    {
        var asset = _project.FindAsset(clip.AssetId);
        if (asset == null || asset.IsMissing || !File.Exists(asset.FilePath)) return;
        if (specs.Any(s => s.Key == clip.Id)) return;

        var isNew = !_lastActive.Contains(clip.Id);
        if (isNew || _globalSeek || !_tokens.TryGetValue(clip.Id, out var token))
        {
            token = ++_serial;
            _tokens[clip.Id] = token;
        }

        var rate = Math.Max(0.1, clip.PlaybackRate);
        var looped = IsLooped(clip, asset);
        var aspect = asset.Width > 0 && asset.Height > 0
            ? (double)asset.Width / asset.Height
            : 16.0 / 9.0;

        specs.Add(new PreviewLayer
        {
            Key = clip.Id,
            Path = asset.FilePath,
            SourceSeconds = SourceTimeFor(clip, asset, time),
            SeekToken = token,
            Volume = Math.Clamp(gain, 0, 2) * (_baseVolume / 100.0),
            Opacity = Math.Clamp(alpha, 0, 1),
            Rate = rate,
            LoopA = looped ? clip.InSeconds : -1,
            LoopB = looped ? asset.DurationSeconds : -1,
            HasVisual = visual,
            Aspect = aspect,
            Still = clip.Kind == MediaKind.Image
        });
    }

    private static bool IsLooped(TimelineClip clip, MediaAsset asset)
    {
        if (clip.Kind == MediaKind.Image) return false;
        var available = asset.DurationSeconds - clip.InSeconds;
        return available > 0.05 &&
               clip.DurationSeconds * Math.Max(0.1, clip.PlaybackRate) > available + 0.01;
    }

    private static double SourceTimeFor(TimelineClip clip, MediaAsset asset, double projectTime)
    {
        if (clip.Kind == MediaKind.Image) return 0;

        var rate = Math.Max(0.1, clip.PlaybackRate);
        var offset = Math.Max(0, projectTime - clip.StartSeconds) * rate;
        var available = asset.DurationSeconds - clip.InSeconds;
        if (available > 0.05 && IsLooped(clip, asset)) offset %= available;

        return clip.InSeconds + offset;
    }

    private static double Ease(double t, FadeCurveKind kind) => kind switch
    {
        FadeCurveKind.EaseIn => t * t,
        FadeCurveKind.EaseOut => 1 - (1 - t) * (1 - t),
        FadeCurveKind.SCurve => t * t * (3 - 2 * t),
        FadeCurveKind.Exponential => (Math.Exp(4 * t) - 1) / (Math.Exp(4) - 1),
        _ => t
    };

    private void SetPlayhead(double seconds)
    {
        _updatingPlayhead = true;
        try { _project.Playhead = seconds; }
        finally { _updatingPlayhead = false; }
    }

    private const double Epsilon = 0.000001;

    public void Dispose()
    {
        _timer.Stop();
        _isPlaying = false;
    }
}
