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
    private double? _previewTime;
    private (double Start, double End)? _loopRange;
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

    /// <summary>Content peak at the playhead, for the monitor level meter (0..1).</summary>
    public double AudioLevel { get; private set; }
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
        _previewTime = null;
        _loopRange = null;
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
        _previewTime = null;
        _globalSeek = true;
        QueueSeek();
    }

    /// <summary>
    /// Shows a time in the monitor without moving the playhead. Used while
    /// trimming so the edge frame is visible during the drag.
    /// </summary>
    public void PreviewAt(double seconds)
    {
        if (_isPlaying) Pause();
        _previewTime = Math.Max(0, seconds);
        _globalSeek = true;
        QueueSeek();
    }

    /// <summary>Returns the monitor to the playhead after a trim preview.</summary>
    public void ClearPreviewTime()
    {
        if (_previewTime == null) return;
        _previewTime = null;
        Refresh();
    }

    private void QueueSeek()
    {
        // Seeking every layer on each mouse move stalls the UI, so throttle to
        // ~25 fps. Only the first move of a burst schedules the seek: pushing
        // the due time back on every move would starve the preview until the
        // drag ends.
        if (_seekQueued) return;

        _seekQueued = true;
        _seekDue = DateTime.UtcNow.AddMilliseconds(35);
    }

    public void Play()
    {
        if (_isPlaying) return;
        if (_project.ContentDuration <= 0) return;

        var time = _project.Playhead;
        _loopRange = null;

        if (_project.HasRange &&
            _project.RangeStart is { } rangeStart && _project.RangeEnd is { } rangeEnd)
        {
            // Starting at/after the range restarts it; starting inside loops it.
            if (time >= rangeEnd - 1e-6) time = rangeStart;
            if (time >= rangeStart - 1e-6 && time < rangeEnd - 1e-6)
                _loopRange = (rangeStart, rangeEnd);
        }
        else if (time >= _project.ContentDuration - 1e-6)
        {
            time = 0;
        }

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
        _loopRange = null;
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

    /// <summary>Re-applies the effect stacks after a live parameter edit.</summary>
    public void RefreshClipEffects() => ApplyNow();

    private void OnTick()
    {
        if (_isPlaying)
        {
            var time = _anchorProject + (DateTime.UtcNow - _anchorWall).TotalSeconds;

            if (_loopRange is { } loop && time >= loop.End)
            {
                var length = Math.Max(0.001, loop.End - loop.Start);
                time = loop.Start + (time - loop.End) % length;
                _anchorProject = time;
                _anchorWall = DateTime.UtcNow;
                _globalSeek = true;
                SetPlayhead(time);
                ApplyNow();
                return;
            }

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
            : _previewTime ?? _project.Playhead;

        AudioLevel = 0;
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
        var flash = 0.0;

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

                var transitionState = TransitionAt(track, clip, time);
                LayerTransition layerTransition;
                if (transitionState is { } state)
                {
                    layerTransition = BuildLayerTransition(state, out var transitionFlash);
                    flash = Math.Max(flash, transitionFlash);
                }
                else
                {
                    layerTransition = LayerTransition.None;

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
                }

                AddLayer(specs, clip, time, alpha, gain, visual: true, layerTransition);
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

                AddLayer(specs, clip, time, alpha: 1, gain, visual: false, LayerTransition.None);
            }
        }

        _compositor.Flash = flash;
        return specs;
    }

    private readonly record struct TransitionState(
        ClipTransition Transition, double Progress, bool Outgoing, bool OnOverlap);

    /// <summary>
    /// Finds the transition that applies to a clip at this instant. An overlap
    /// wins over the clip's own edges, so a dropped transition always works on
    /// the crossfade it was dropped on.
    /// </summary>
    private TransitionState? TransitionAt(TimelineTrack track, TimelineClip clip, double time)
    {
        var overlap = _project.GetOverlaps(track)
            .FirstOrDefault(o => time >= o.StartSeconds && time < o.EndSeconds);

        if (overlap != null &&
            (clip.Id == overlap.Incoming.Id || clip.Id == overlap.Outgoing.Id))
        {
            var transition = overlap.Incoming.TransitionIn;
            if (transition is not { Enabled: true }) transition = overlap.Outgoing.TransitionOut;
            if (transition is not { Enabled: true }) return null;

            var progress = Math.Clamp(
                (time - overlap.StartSeconds) /
                Math.Max(0.0001, overlap.EndSeconds - overlap.StartSeconds), 0, 1);
            return new TransitionState(transition, progress,
                clip.Id == overlap.Outgoing.Id, true);
        }

        if (clip.TransitionIn is { Enabled: true } transitionIn)
        {
            var duration = Math.Clamp(TransitionCatalog.Duration(transitionIn), 0.05,
                Math.Max(0.05, clip.DurationSeconds));
            if (time >= clip.StartSeconds - Epsilon &&
                time < clip.StartSeconds + duration - Epsilon)
            {
                return new TransitionState(transitionIn,
                    Math.Clamp((time - clip.StartSeconds) / duration, 0, 1), false, false);
            }
        }

        if (clip.TransitionOut is { Enabled: true } transitionOut)
        {
            var duration = Math.Clamp(TransitionCatalog.Duration(transitionOut), 0.05,
                Math.Max(0.05, clip.DurationSeconds));
            var start = clip.EndSeconds - duration;
            if (time >= start - Epsilon && time < clip.EndSeconds - Epsilon)
            {
                return new TransitionState(transitionOut,
                    Math.Clamp((time - start) / duration, 0, 1), true, false);
            }
        }

        return null;
    }

    private readonly record struct LayerTransition(
        double AlphaMul, double GainMul,
        double OffsetX, double OffsetY, double ScaleMul,
        double Reveal, int RevealMode, double Feather)
    {
        public static readonly LayerTransition None =
            new(1, 1, 0, 0, 1, 1, 0, 0);
    }

    private static LayerTransition BuildLayerTransition(TransitionState state, out double flash)
    {
        flash = 0;
        var transition = state.Transition;
        var eased = TransitionCatalog.Ease(state.Progress);
        var direction = TransitionCatalog.Direction(transition);
        var gain = state.Outgoing ? 1 - state.Progress : state.Progress;

        // On an overlap the outgoing picture stays under the incoming one; the
        // incoming transition supplies the visual change. Off an overlap the
        // outgoing clip has to fade/slide out on its own.
        var alpha = 1.0;

        switch (transition.Kind)
        {
            case TransitionKind.Dissolve:
                if (state.Outgoing)
                    alpha = state.OnOverlap ? 1 : 1 - eased;
                else
                    alpha = eased;
                break;

            case TransitionKind.Flash:
                flash = Math.Clamp(transition.Get("strength", 0.9), 0, 1) *
                        Math.Sin(Math.PI * state.Progress);
                if (state.OnOverlap)
                {
                    // Keep the outgoing picture until the flash peaks, then
                    // reveal the incoming one under the white card.
                    var cut = TransitionCatalog.Ease(
                        Math.Clamp((state.Progress - 0.35) / 0.3, 0, 1));
                    alpha = state.Outgoing ? 1 - cut : cut;
                }
                else
                {
                    alpha = state.Outgoing ? 1 - eased : eased;
                }
                break;

            case TransitionKind.Slide:
            {
                var v = TransitionCatalog.DirectionVector(direction);
                if (state.Outgoing)
                {
                    if (!state.OnOverlap)
                        return new LayerTransition(alpha, gain,
                            v.X * eased, v.Y * eased, 1, 1, 0, 0);
                }
                else
                {
                    return new LayerTransition(alpha, gain,
                        -v.X * (1 - eased), -v.Y * (1 - eased), 1, 1, 0, 0);
                }
                break;
            }

            case TransitionKind.Push:
            {
                var v = TransitionCatalog.DirectionVector(direction);
                return state.Outgoing
                    ? new LayerTransition(alpha, gain,
                        v.X * eased, v.Y * eased, 1, 1, 0, 0)
                    : new LayerTransition(alpha, gain,
                        -v.X * (1 - eased), -v.Y * (1 - eased), 1, 1, 0, 0);
            }

            case TransitionKind.Cross:
                return state.Outgoing
                    ? new LayerTransition(alpha, gain,
                        -eased, -eased, 1, 1, 0, 0)
                    : new LayerTransition(alpha, gain,
                        1 - eased, 1 - eased, 1, 1, 0, 0);

            case TransitionKind.Wipe:
            case TransitionKind.Iris:
            {
                var reveal = state.Outgoing
                    ? (state.OnOverlap ? 1 : 1 - eased)
                    : eased;
                return new LayerTransition(alpha, gain, 0, 0, 1, reveal,
                    TransitionCatalog.RevealMode(transition.Kind, direction),
                    Math.Clamp(transition.Get("feather", 0.06), 0, 0.5));
            }
        }

        return new LayerTransition(alpha, gain, 0, 0, 1, 1, 0, 0);
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
            Filters = EffectCatalog.PreviewStack(next.Effects),
            Pending = true
        };
    }

    private void AddLayer(List<PreviewLayer> specs, TimelineClip clip, double time,
        double alpha, double gain, bool visual, LayerTransition transition)
    {
        var asset = _project.FindAsset(clip.AssetId);
        if (asset == null || asset.IsMissing || !File.Exists(asset.FilePath)) return;
        if (specs.Any(s => s.Key == clip.Id)) return;

        var manualFade = ManualFadeFactor(clip, time);
        alpha *= manualFade;
        gain *= manualFade;
        alpha *= transition.AlphaMul;
        gain *= transition.GainMul;

        if (asset.Peaks is { Length: > 0 } peaks && gain > 0.0001)
        {
            var index = (int)Math.Clamp(
                asset.DurationSeconds > 0.01 ? SourceTimeFor(clip, asset, time) / asset.DurationSeconds : 0,
                0, 1) * (peaks.Length - 1);
            var peak = peaks[Math.Clamp(index, 0, peaks.Length - 1)] * Math.Clamp(gain, 0, 1);
            if (peak > AudioLevel) AudioLevel = peak;
        }

        var isNew = !_lastActive.Contains(clip.Id);
        if (isNew || _globalSeek || !_tokens.TryGetValue(clip.Id, out var token))
        {
            token = ++_serial;
            _tokens[clip.Id] = token;
        }

        var rate = Math.Max(0.1, clip.PlaybackRate);
        var looped = IsLooped(clip, asset);
        var sourceAspect = asset.Width > 0 && asset.Height > 0
            ? (double)asset.Width / asset.Height
            : 16.0 / 9.0;
        var aspect = sourceAspect;

        var chroma = clip.Effects.FirstOrDefault(e =>
            e.Enabled && e.Kind == EffectKind.ChromaKey);
        var keyBlue = chroma != null &&
                      (int)Math.Round(chroma.Get("color", 0)) == 1;

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
            Still = clip.Kind == MediaKind.Image,
            CropLeft = clip.CropLeft,
            CropRight = clip.CropRight,
            CropTop = clip.CropTop,
            CropBottom = clip.CropBottom,
            PanX = clip.PanX,
            PanY = clip.PanY,
            Scale = Math.Clamp(clip.Scale * transition.ScaleMul, 0.05, 4),
            PositionX = Math.Clamp(clip.PositionX + transition.OffsetX, -1, 1),
            PositionY = Math.Clamp(clip.PositionY + transition.OffsetY, -1, 1),
            Filters = EffectCatalog.PreviewStack(clip.Effects),
            ChromaKey = chroma != null,
            KeyRed = 0,
            KeyGreen = keyBlue ? 0 : 1,
            KeyBlue = keyBlue ? 1 : 0,
            KeySimilarity = chroma?.Get("similarity", 0.12) ?? 0,
            KeyBlend = chroma?.Get("blend", 0.15) ?? 0,
            Reveal = transition.Reveal,
            RevealMode = transition.RevealMode,
            RevealFeather = transition.Feather
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

    private static double ManualFadeFactor(TimelineClip clip, double time)
    {
        var factor = 1.0;
        if (clip.FadeInSeconds > 0.001 && time < clip.StartSeconds + clip.FadeInSeconds)
            factor *= Math.Clamp((time - clip.StartSeconds) / clip.FadeInSeconds, 0, 1);
        if (clip.FadeOutSeconds > 0.001 && time > clip.EndSeconds - clip.FadeOutSeconds)
            factor *= Math.Clamp((clip.EndSeconds - time) / clip.FadeOutSeconds, 0, 1);
        return factor;
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
        _previewTime = null;
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
