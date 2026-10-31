using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YorteVegas.Models;
using YorteVegas.Services;
using YorteVegas.ViewModels;

namespace YorteVegas.Controls;

/// <summary>
/// Custom timeline: ruler, fixed layer headers, clips, playhead and a slim
/// horizontal scrollbar, all drawn in one surface so columns stay aligned.
/// Supports select / move / trim / split (razor), scrubbing and zooming.
/// </summary>
public sealed class TimelineView : FrameworkElement
{
    // ------------------------------------------------------------- palette
    private static readonly Brush BackgroundBrush = Frozen("#07080A");
    private static readonly Brush HeaderBrush = Frozen("#0A0B0E");
    private static readonly Brush RulerBrush = Frozen("#0A0D13");
    private static readonly Brush LaneBrushA = Frozen("#080A0E");
    private static readonly Brush LaneBrushB = Frozen("#090B10");
    private static readonly Brush LaneLineBrush = Frozen("#121923");
    private static readonly Brush GridBrush = FrozenArgb(9, 255, 255, 255);
    private static readonly Brush RulerTickBrush = Frozen("#3A4A5E");
    private static readonly Brush RulerTextBrush = Frozen("#6E8095");
    private static readonly Brush TextBrush = Frozen("#DCE8F4");
    private static readonly Brush MutedBrush = Frozen("#8EA0B4");
    private static readonly Brush DimBrush = Frozen("#5C6B7E");
    private static readonly Brush AccentBrush = Frozen("#2E9BFF");
    private static readonly Brush AmberBrush = Frozen("#F2B23A");
    private static readonly Brush DangerBrush = Frozen("#F87171");
    private static readonly Brush VideoClipBrush = Frozen("#16222F");
    private static readonly Brush ImageClipBrush = Frozen("#1B2436");
    private static readonly Brush AudioClipBrush = Frozen("#1A2D42");
    private static readonly Brush ClipBorderBrush = Frozen("#263A4F");
    private static readonly Brush HoverBorderBrush = Frozen("#3E5F82");
    private static readonly Brush VideoStripeBrush = Frozen("#2E9BFF");
    private static readonly Brush ImageStripeBrush = Frozen("#9A5CFF");
    private static readonly Brush AudioStripeBrush = Frozen("#4E9BD6");
    private static readonly Brush WaveBrush = FrozenArgb(95, 160, 205, 235);
    private static readonly Brush VideoWaveBrush = FrozenArgb(120, 150, 205, 240);
    private static readonly Brush ScrollTrackBrush = Frozen("#111A27");
    private static readonly Brush ScrollThumbBrush = Frozen("#2C3E54");
    private static readonly Brush ScrollThumbHoverBrush = Frozen("#3C5674");
    private static readonly Brush DropFillBrush = FrozenArgb(58, 46, 155, 255);
    private static readonly Pen DropPen = CreateDropPen();
    private static readonly Brush FadeShadeBrush = FrozenArgb(24, 127, 196, 255);
    private static readonly Brush FadeShadeHoverBrush = FrozenArgb(48, 127, 196, 255);
    private static readonly Brush FadeInBrush = Frozen("#7FC4FF");
    private static readonly Brush FadeOutBrush = FrozenArgb(120, 160, 190, 220);
    private static readonly Pen FadeInPen = CreateFadePen(FadeInBrush, 1.4);
    private static readonly Pen FadeOutPen = CreateFadePen(FadeOutBrush, 1.0);
    private static readonly Brush TooltipBrush = FrozenArgb(246, 15, 23, 36);
    private static readonly Pen TooltipBorderPen = CreateFadePen(Frozen("#2A3B52"), 1);
    private static readonly Brush LevelLineBrush = FrozenArgb(150, 220, 235, 250);
    private static readonly Brush LevelActiveBrush = Frozen("#F2B23A");
    private static readonly Brush AudioLevelActiveBrush = Frozen("#7FC4FF");
    private static readonly Pen LoopBoundaryPen = CreateLoopPen();

    private static Pen CreateLoopPen()
    {
        var pen = new Pen(FrozenArgb(95, 220, 235, 250), 1)
        {
            DashStyle = new DashStyle(new double[] { 2, 3 }, 0),
            DashCap = PenLineCap.Round
        };
        pen.Freeze();
        return pen;
    }

    private static Pen CreateDropPen()
    {
        var pen = new Pen(AccentBrush, 1.5) { DashStyle = DashStyles.Dash };
        pen.Freeze();
        return pen;
    }

    private static Pen CreateFadePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    private static readonly Typeface RegularTypeface = new(new FontFamily("Segoe UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface SemiBoldTypeface = new(new FontFamily("Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface MonoTypeface = new(new FontFamily("Consolas"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    // -------------------------------------------------------------- layout
    public const double HeaderWidth = 176;
    public const double RulerHeight = 28;
    private const double LanePadding = 8;
    private const double BottomBarHeight = 14;
    private const double MinPixelsPerSecond = 6;
    private const double MaxPixelsPerSecond = 500;

    private EditProject? _project;
    private double _pixelsPerSecond = 48;
    private double _scrollPixels;
    private double _scrollY;
    private bool _snapEnabled = true;
    private bool _razorMode;
    private bool _scrollThumbHover;
    private bool _vScrollThumbHover;

    // ------------------------------------------------------------- dragging
    private enum DragMode { None, Scrub, Move, TrimLeft, TrimRight, StretchRight, Opacity, Volume, Scrollbar, VScrollbar }
    private DragMode _drag = DragMode.None;
    private Point _dragStartPoint;
    private TimelineClip? _dragClip;
    private double _dragClipStart;
    private double _dragClipDuration;
    private double _dragLevelStart;
    private double _dragSourceLength;
    private double _dragScrollStart;
    private double _scrollGrabOffset;
    private double _vScrollGrabOffset;
    private bool _editCaptured;
    private bool _moved;
    private bool _ctrlDuplicate;
    private TimelineTrack? _dropTrack;
    private bool _dropCreatesTrack;
    private double _dropNewTrackTop;
    private TrackKind _dropNewTrackKind;

    private Guid? _hoverClipId;
    private bool _hoverLeft;
    private bool _hoverRight;
    private (TimelineTrack? Track, int Button)? _hoverHeaderButton;
    private readonly Dictionary<ImageSource, ImageBrush> _imageBrushes = new();
    private DropPreview? _dropPreview;
    private (TimelineTrack Track, EditProject.ClipOverlap Overlap)? _hoverFade;
    private (TimelineTrack Track, Guid ClipId)? _hoverLevel;

    private sealed record DropPreview(double Start, double Duration, string Name, TimelineTrack Track);

    public TimelineView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
        SizeChanged += (_, _) => { ClampScroll(); InvalidateVisual(); };
    }

    // ------------------------------------------------------------ properties

    public EditProject? Project
    {
        get => _project;
        set
        {
            if (ReferenceEquals(_project, value)) return;
            if (_project != null)
            {
                _project.TimelineChanged -= OnProjectChanged;
                _project.PlayheadChanged -= OnProjectChanged;
                _project.SelectionChanged -= OnProjectChanged;
            }
            _project = value;
            if (_project != null)
            {
                _project.TimelineChanged += OnProjectChanged;
                _project.PlayheadChanged += OnProjectChanged;
                _project.SelectionChanged += OnProjectChanged;
            }
            InvalidateVisual();
        }
    }

    public double PixelsPerSecond => _pixelsPerSecond;

    public bool SnapEnabled
    {
        get => _snapEnabled;
        set { _snapEnabled = value; }
    }

    public bool RazorMode
    {
        get => _razorMode;
        set
        {
            _razorMode = value;
            Cursor = value ? Cursors.Cross : Cursors.Arrow;
            InvalidateVisual();
        }
    }

    public event EventHandler? ZoomChanged;

    /// <summary>Raised when the user chooses "Remove layer" for a track header.</summary>
    public event EventHandler<TimelineTrack>? RemoveTrackRequested;

    /// <summary>Raised when the user wants to rename a track header.</summary>
    public event EventHandler<TimelineTrack>? RenameTrackRequested;

    /// <summary>Raised continuously while an opacity / volume line is dragged.</summary>
    public event EventHandler? ClipLevelChanged;

    /// <summary>Position where a dropped item would start, snapped to a frame.</summary>
    public double DropTimeAtPoint(Point point) =>
        _project?.SnapToFrame(Math.Max(0, TimeAtPoint(point))) ?? 0;

    /// <summary>
    /// Finds the track that should receive a dropped item: the lane under the
    /// pointer when it can accept the kind, otherwise the first unlocked track
    /// of the right type.
    /// </summary>
    public TimelineTrack? FindDropTrack(Point point, MediaKind kind)
    {
        if (_project == null) return null;

        if (TryGetTrackRow(point.Y, out var row, out _) && row != null &&
            row.Accepts(kind) && !row.IsLocked)
        {
            return row;
        }

        return _project.Tracks.FirstOrDefault(t => t.Accepts(kind) && !t.IsLocked);
    }

    /// <summary>Shows the translucent clip-shaped hint used while dragging media.</summary>
    public void ShowDropPlaceholder(Point point, string name, MediaKind kind, double durationSeconds)
    {
        if (_project == null)
        {
            ClearDropPlaceholder();
            return;
        }

        var track = FindDropTrack(point, kind);
        if (track == null)
        {
            ClearDropPlaceholder();
            return;
        }

        var duration = kind == MediaKind.Image
            ? Math.Max(0.5, _project.ImageDurationSeconds)
            : durationSeconds > 0.05 ? durationSeconds : 4;
        var start = DropTimeAtPoint(point);

        _dropPreview = new DropPreview(start, duration, name, track);
        InvalidateVisual();
    }

    public void ClearDropPlaceholder()
    {
        if (_dropPreview == null) return;
        _dropPreview = null;
        InvalidateVisual();
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        ClearDropPlaceholder();
    }

    // --------------------------------------------------------------- helpers

    private double ViewportWidth => Math.Max(1, ActualWidth - HeaderWidth - LanePadding);

    private double ContentSeconds
    {
        get
        {
            var content = _project?.ContentDuration ?? 0;
            return Math.Max(Math.Max(content + 10, 24), ViewportWidth / _pixelsPerSecond);
        }
    }

    private double MaxScrollPixels => Math.Max(0, ContentSeconds * _pixelsPerSecond - ViewportWidth);

    private double LaneBottom => Math.Max(RulerHeight, ActualHeight - BottomBarHeight);

    private double TrackAreaHeight => Math.Max(1, LaneBottom - RulerHeight);

    private double TotalTracksHeight => _project?.Tracks.Sum(t => t.Height) ?? 0;

    private double MaxScrollY => Math.Max(0, TotalTracksHeight - TrackAreaHeight);

    private double TimeToX(double seconds) =>
        HeaderWidth + LanePadding - _scrollPixels + seconds * _pixelsPerSecond;

    public double TimeAtPoint(Point point) =>
        Math.Max(0, (point.X - HeaderWidth - LanePadding + _scrollPixels) / _pixelsPerSecond);

    private void ClampScroll()
    {
        _scrollPixels = Math.Clamp(_scrollPixels, 0, MaxScrollPixels);
        _scrollY = Math.Clamp(_scrollY, 0, MaxScrollY);
    }

    private void OnProjectChanged(object? sender, EventArgs e)
    {
        ClampScroll();
        FollowPlayhead();
        InvalidateVisual();
    }

    /// <summary>Page the view along when playback runs past the right edge.</summary>
    private void FollowPlayhead()
    {
        if (_project == null || ActualWidth <= 0 || _drag != DragMode.None) return;

        var x = TimeToX(_project.Playhead);
        var rightEdge = ActualWidth - 34;
        if (x <= rightEdge) return;

        var targetX = HeaderWidth + LanePadding + ViewportWidth * 0.82;
        _scrollPixels += x - targetX;
        ClampScroll();
    }

    public void ScrollToPlayhead()
    {
        if (_project == null) return;
        var x = TimeToX(_project.Playhead);
        var left = HeaderWidth + LanePadding;
        var right = ActualWidth - LanePadding;
        if (x < left || x > right)
        {
            _scrollPixels = Math.Max(0,
                _project.Playhead * _pixelsPerSecond - ViewportWidth * 0.35);
            ClampScroll();
            InvalidateVisual();
        }
    }

    public void ZoomIn() => ZoomAt(_pixelsPerSecond * 1.25, _project?.Playhead ?? 0, HeaderWidth + LanePadding + 40);

    public void ZoomOut() => ZoomAt(_pixelsPerSecond / 1.25, _project?.Playhead ?? 0, HeaderWidth + LanePadding + 40);

    public void FitToView()
    {
        var content = Math.Max(4, _project?.ContentDuration ?? 0) + 2;
        var pps = Math.Clamp(ViewportWidth / content, MinPixelsPerSecond, MaxPixelsPerSecond);
        _pixelsPerSecond = pps;
        _scrollPixels = 0;
        _scrollY = 0;
        ClampScroll();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Back to the comfortable default zoom with the playhead at the left.</summary>
    public void ResetZoom()
    {
        _pixelsPerSecond = 48;
        _scrollPixels = 0;
        _scrollY = 0;
        ClampScroll();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void ZoomAt(double newPixelsPerSecond, double anchorTime, double anchorX)
    {
        newPixelsPerSecond = Math.Clamp(newPixelsPerSecond, MinPixelsPerSecond, MaxPixelsPerSecond);
        if (Math.Abs(newPixelsPerSecond - _pixelsPerSecond) < 0.001) return;

        var timeAtAnchor = Math.Max(0,
            (anchorX - HeaderWidth - LanePadding + _scrollPixels) / _pixelsPerSecond);
        _pixelsPerSecond = newPixelsPerSecond;
        _scrollPixels = timeAtAnchor * _pixelsPerSecond - (anchorX - HeaderWidth - LanePadding);
        ClampScroll();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static Brush FrozenArgb(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, double size,
        Brush brush, Typeface? typeface = null, double maxWidth = double.PositiveInfinity)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface ?? RegularTypeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        if (!double.IsInfinity(maxWidth) && !double.IsNaN(maxWidth))
        {
            formatted.MaxTextWidth = Math.Max(1, maxWidth);
            formatted.MaxLineCount = 1;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
        }

        dc.DrawText(formatted, new Point(x, y));
    }

    // ---------------------------------------------------------------- render

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));
        dc.DrawRectangle(HeaderBrush, null, new Rect(0, 0, HeaderWidth, height));
        dc.DrawRectangle(RulerBrush, null, new Rect(HeaderWidth, 0, Math.Max(0, width - HeaderWidth), RulerHeight));

        var tracks = _project?.Tracks ?? (IEnumerable<TimelineTrack>)Array.Empty<TimelineTrack>();

        // lane backgrounds
        dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, RulerHeight,
            Math.Max(0, width - HeaderWidth), Math.Max(0, LaneBottom - RulerHeight))));
        double y = RulerHeight - _scrollY;
        int index = 0;
        foreach (var track in tracks)
        {
            if (y >= LaneBottom) break;
            if (y + track.Height > RulerHeight)
            {
                var top = Math.Max(y, RulerHeight);
                var bottom = Math.Min(y + track.Height, LaneBottom);
                dc.DrawRectangle(index % 2 == 0 ? LaneBrushA : LaneBrushB, null,
                    new Rect(HeaderWidth, top, Math.Max(0, width - HeaderWidth), Math.Max(0, bottom - top)));
                if (y + track.Height <= LaneBottom)
                    dc.DrawRectangle(LaneLineBrush, null, new Rect(0, y + track.Height - 1, width, 1));
            }
            y += track.Height;
            index++;
        }
        dc.Pop();

        DrawGrid(dc, width, height);
        DrawTracks(dc, width, tracks);
        DrawFades(dc, width, tracks);
        DrawDropPreview(dc, width);
        DrawClipDropTarget(dc, width);
        DrawRuler(dc, width);
        DrawHeaders(dc, tracks);
        DrawHeaderTooltip(dc, width);
        DrawPlayhead(dc, height);
        DrawScrollbar(dc, width, height);
        DrawVerticalScrollbar(dc, width, height);
        DrawEmptyHint(dc, width, tracks);
    }

    private void DrawGrid(DrawingContext dc, double width, double height)
    {
        var step = ChooseTickStep(out _);
        if (step <= 0) return;

        dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, 0,
            Math.Max(0, width - HeaderWidth), Math.Max(0, height - BottomBarHeight))));

        var first = Math.Floor((_scrollPixels / _pixelsPerSecond) / step) * step;
        for (var t = first; ; t += step)
        {
            var x = TimeToX(t);
            if (x > width) break;
            if (x >= HeaderWidth - 1)
                dc.DrawRectangle(GridBrush, null, new Rect(Math.Round(x), RulerHeight, 1, LaneBottom - RulerHeight));
        }
        dc.Pop();
    }

    /// <summary>Picks a readable ruler step (seconds) for the current zoom.</summary>
    private double ChooseTickStep(out double minor)
    {
        // Ruler labels show whole wall-clock seconds, so never label below 1s.
        var steps = new[] { 1d, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };
        foreach (var step in steps)
        {
            if (step * _pixelsPerSecond >= 70)
            {
                minor = step / (step >= 10 ? 5 : 4);
                return step;
            }
        }
        minor = 300;
        return 3600;
    }

    private void DrawRuler(DrawingContext dc, double width)
    {
        var step = ChooseTickStep(out var minor);
        dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, 0, Math.Max(0, width - HeaderWidth), RulerHeight)));

        var firstMinor = Math.Floor((_scrollPixels / _pixelsPerSecond) / minor) * minor;
        for (var t = firstMinor; ; t += minor)
        {
            var x = TimeToX(t);
            if (x > width + 1) break;
            if (x < HeaderWidth - 1) continue;
            dc.DrawRectangle(RulerTickBrush, null, new Rect(Math.Round(x), RulerHeight - 5, 1, 5));
        }

        var first = Math.Floor((_scrollPixels / _pixelsPerSecond) / step) * step;
        for (var t = first; ; t += step)
        {
            var x = TimeToX(t);
            if (x > width + 1) break;
            if (x < HeaderWidth - 1) continue;
            dc.DrawRectangle(RulerTickBrush, null, new Rect(Math.Round(x), RulerHeight - 10, 1, 10));
            DrawText(dc, Timecode.Ruler(t, _project?.Fps ?? 30), x + 5, 6, 9.5,
                RulerTextBrush, MonoTypeface, 64);
        }
        dc.Pop();
    }

    private void DrawHeaders(DrawingContext dc, IEnumerable<TimelineTrack> tracks)
    {
        DrawText(dc, "LAYERS", 14, 8, 9.5, DimBrush, SemiBoldTypeface);

        dc.PushClip(new RectangleGeometry(new Rect(0, RulerHeight, HeaderWidth,
            Math.Max(0, LaneBottom - RulerHeight))));

        double y = RulerHeight - _scrollY;
        foreach (var track in tracks)
        {
            if (y >= LaneBottom) break;
            if (y + track.Height <= RulerHeight)
            {
                y += track.Height;
                continue;
            }

            if (_hoverHeaderButton is { } hover && ReferenceEquals(hover.Track, track))
                dc.DrawRoundedRectangle(FrozenArgb(20, 255, 255, 255), null,
                    HeaderButtonRect(track, hover.Button), 4, 4);

            var dot = track.Kind == TrackKind.Video ? AccentBrush : AudioStripeBrush;
            dc.DrawEllipse(dot, null, new Point(13, y + track.Height / 2), 2.2, 2.2);
            DrawText(dc, track.Name, 22, y + (track.Height - 15) / 2, 11,
                track.Kind == TrackKind.Video ? TextBrush : MutedBrush, SemiBoldTypeface,
                HeaderWidth - 102);

            DrawTrackToggles(dc, track);
            y += track.Height;
        }
        dc.Pop();
    }

    private Rect HeaderButtonRect(TimelineTrack track, int button)
    {
        var top = TrackTop(track);
        var cy = top + (track.Height - 18) / 2;
        var x = button switch
        {
            0 => HeaderWidth - 74,
            1 => HeaderWidth - 50,
            _ => HeaderWidth - 26
        };
        return new Rect(x, cy, 18, 18);
    }

    private void DrawTrackToggles(DrawingContext dc, TimelineTrack track)
    {
        var lockRect = HeaderButtonRect(track, 0);
        var muteRect = HeaderButtonRect(track, 1);
        var soloRect = HeaderButtonRect(track, 2);

        if (track.IsLocked)
            dc.DrawRoundedRectangle(FrozenArgb(42, 242, 178, 58), null, lockRect, 3, 3);
        if (track.IsMuted)
            dc.DrawRoundedRectangle(FrozenArgb(48, 248, 113, 113), null, muteRect, 3, 3);
        if (track.IsSolo)
            dc.DrawRoundedRectangle(FrozenArgb(48, 46, 155, 255), null, soloRect, 3, 3);

        DrawLockIcon(dc, lockRect, track.IsLocked ? AmberBrush : DimBrush);
        DrawMuteIcon(dc, muteRect, track.IsMuted);
        DrawSoloIcon(dc, soloRect, track.IsSolo);
    }

    private static void DrawLockIcon(DrawingContext dc, Rect box, Brush brush)
    {
        var pen = new Pen(brush, 1.1);
        var x = box.X + 5;
        var y = box.Y + 4;
        dc.DrawRoundedRectangle(null, pen, new Rect(x, y + 5, 8, 6), 1.2, 1.2);

        var arc = new StreamGeometry();
        using (var ctx = arc.Open())
        {
            ctx.BeginFigure(new Point(x + 1, y + 5), false, false);
            ctx.ArcTo(new Point(x + 7, y + 5), new Size(3, 3), 0, false,
                SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, pen, arc);
    }

    /// <summary>Speaker icon; muted adds a slash.</summary>
    private static void DrawMuteIcon(DrawingContext dc, Rect box, bool muted)
    {
        var brush = muted ? DangerBrush : DimBrush;
        var x = box.X + 4;
        var y = box.Y + 5;

        var speaker = new StreamGeometry();
        using (var ctx = speaker.Open())
        {
            ctx.BeginFigure(new Point(x, y + 3), true, true);
            ctx.LineTo(new Point(x + 2.5, y + 3), true, false);
            ctx.LineTo(new Point(x + 5, y), true, false);
            ctx.LineTo(new Point(x + 5, y + 10), true, false);
            ctx.LineTo(new Point(x + 2.5, y + 7), true, false);
            ctx.LineTo(new Point(x, y + 7), true, false);
        }
        speaker.Freeze();
        dc.DrawGeometry(brush, null, speaker);

        if (muted)
        {
            dc.DrawLine(new Pen(DangerBrush, 1.5),
                new Point(x + 6.5, y), new Point(x + 11, y + 10));
            return;
        }

        var wave = new StreamGeometry();
        using (var ctx = wave.Open())
        {
            ctx.BeginFigure(new Point(x + 7, y + 2.5), false, false);
            ctx.ArcTo(new Point(x + 7, y + 9.5), new Size(3.4, 3.4), 0, false,
                SweepDirection.Clockwise, true, false);
        }
        wave.Freeze();
        dc.DrawGeometry(null, new Pen(DimBrush, 1.1), wave);
    }

    /// <summary>Headphone icon for solo.</summary>
    private static void DrawSoloIcon(DrawingContext dc, Rect box, bool solo)
    {
        var brush = solo ? AccentBrush : DimBrush;
        var pen = new Pen(brush, 1.2);
        var x = box.X + 3.5;
        var y = box.Y + 4;

        var arch = new StreamGeometry();
        using (var ctx = arch.Open())
        {
            ctx.BeginFigure(new Point(x, y + 8), false, false);
            ctx.BezierTo(new Point(x, y), new Point(x + 11, y), new Point(x + 11, y + 8),
                true, false);
        }
        arch.Freeze();
        dc.DrawGeometry(null, pen, arch);

        dc.DrawRoundedRectangle(brush, null, new Rect(x, y + 6.5, 2.4, 5), 1.2, 1.2);
        dc.DrawRoundedRectangle(brush, null, new Rect(x + 8.6, y + 6.5, 2.4, 5), 1.2, 1.2);
    }

    private void DrawTracks(DrawingContext dc, double width, IEnumerable<TimelineTrack> tracks)
    {
        if (_project == null) return;

        dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, RulerHeight,
            Math.Max(0, width - HeaderWidth), Math.Max(0, LaneBottom - RulerHeight))));

        double y = RulerHeight - _scrollY;
        foreach (var track in tracks)
        {
            if (y >= LaneBottom) break;
            if (y + track.Height <= RulerHeight)
            {
                y += track.Height;
                continue;
            }

            foreach (var clip in track.Clips.OrderBy(c => c.StartSeconds))
            {
                var x1 = TimeToX(clip.StartSeconds);
                var x2 = TimeToX(clip.EndSeconds);
                if (x2 < HeaderWidth - 1) continue;
                if (x1 > width + 1) continue;

                var clipHeight = track.Kind == TrackKind.Video
                    ? Math.Max(18, track.Height - 8)
                    : Math.Max(16, track.Height - 12);
                var top = y + (track.Height - clipHeight) / 2;
                var rect = new Rect(Math.Max(HeaderWidth + 1, x1), top,
                    Math.Max(3, x2 - Math.Max(HeaderWidth + 1, x1)), clipHeight);

                DrawClip(dc, clip, track, rect);
            }
            y += track.Height;
        }

        dc.Pop();
    }

    private void DrawClip(DrawingContext dc, TimelineClip clip, TimelineTrack track, Rect rect)
    {
        var isSelected = clip.Id == _project?.SelectedClipId;
        var isHovered = clip.Id == _hoverClipId;
        var asset = _project?.FindAsset(clip.AssetId);
        var missing = asset?.IsMissing == true;

        var fill = clip.Kind switch
        {
            MediaKind.Audio => AudioClipBrush,
            MediaKind.Image => ImageClipBrush,
            _ => VideoClipBrush
        };

        var radius = 4.8;
        dc.DrawRoundedRectangle(fill, null, rect, radius, radius);

        // image thumbnail / video filmstrip wash
        if (clip.Kind == MediaKind.Video && asset?.Filmstrip.Count > 0 &&
            rect.Width > 30 && rect.Height > 22)
        {
            DrawVideoFilmstrip(dc, rect, asset.Filmstrip);
        }
        else if (clip.Kind == MediaKind.Image && asset?.Thumbnail is { } thumb && rect.Width > 26)
        {
            var imageRect = new Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2);
            dc.PushClip(new RectangleGeometry(imageRect, 4, 4));
            dc.DrawRectangle(GetImageBrush(thumb), null, imageRect);
            dc.DrawRectangle(FrozenArgb(158, 10, 14, 20), null, imageRect);
            dc.Pop();
        }

        // Opacity is visible on the block itself, so the timeline reflects it.
        if (clip.Kind != MediaKind.Audio && clip.Opacity < 0.995)
        {
            var alpha = (byte)Math.Clamp((1 - clip.Opacity) * 150, 0, 150);
            dc.PushClip(new RectangleGeometry(
                new Rect(rect.X + 1, rect.Y + 1, Math.Max(1, rect.Width - 2), Math.Max(1, rect.Height - 2)),
                radius, radius));
            dc.DrawRectangle(FrozenArgb(alpha, 6, 10, 16), null, rect);
            dc.Pop();
        }

        // kind stripe
        var stripe = clip.Kind switch
        {
            MediaKind.Audio => AudioStripeBrush,
            MediaKind.Image => ImageStripeBrush,
            _ => VideoStripeBrush
        };        dc.PushClip(new RectangleGeometry(new Rect(rect.X, rect.Y, Math.Min(3.5, rect.Width), rect.Height), radius, radius));
        dc.DrawRectangle(stripe, null, new Rect(rect.X, rect.Y, 3.5, rect.Height));
        dc.Pop();

        if (isSelected)
            dc.DrawRectangle(FrozenArgb(22, 242, 178, 58), null,
                new Rect(rect.X + 3.5, rect.Y, Math.Max(0, rect.Width - 3.5), Math.Min(16, rect.Height)));

        // audio waveform / video filmstrip ticks
        if (clip.Kind == MediaKind.Audio && rect.Width > 34)
            DrawWaveform(dc, clip, asset, rect);
        else if (clip.Kind == MediaKind.Video && rect.Width > 60 &&
                 (asset?.Filmstrip.Count ?? 0) == 0)
            DrawFilmTicks(dc, clip, rect);

        if (clip.Kind == MediaKind.Video && rect.Width > 24)
            DrawVideoWaveform(dc, clip, asset, rect);

        // Dashed lines mark where stretched media loops back to its in-point.
        if (asset != null && clip.Kind != MediaKind.Image)
        {
            var available = asset.DurationSeconds - clip.InSeconds;
            var rate = Math.Max(0.1, clip.PlaybackRate);
            if (available > 0.05)
            {
                var loopDuration = available / rate;
                if (loopDuration > 0.05 && clip.DurationSeconds > loopDuration + 0.02)
                {
                    var guard = 0;
                    for (var boundary = clip.StartSeconds + loopDuration;
                         boundary < clip.EndSeconds - 0.001 && guard < 200;
                         boundary += loopDuration, guard++)
                    {
                        var x = TimeToX(boundary);
                        if (x > rect.X + 3 && x < rect.Right - 3)
                            dc.DrawLine(LoopBoundaryPen,
                                new Point(x, rect.Y + 3), new Point(x, rect.Bottom - 3));
                    }
                }
            }
        }

        var textBrush = missing ? DangerBrush : isSelected ? TextBrush : Frozen("#CFE0F0");
        var labelY = rect.Y + (isSelected ? 5 : 4);
        var speedBadge = Math.Abs(clip.PlaybackRate - 1) > 0.005 && rect.Width > 100;
        var labelWidth = rect.Width - 18 - (speedBadge ? 38 : 0);
        if (!string.IsNullOrEmpty(clip.Name) && labelWidth > 8 && rect.Height > 16)
        {
            var label = missing ? $"{clip.Name}  (missing)" : clip.Name;
            DrawText(dc, label, rect.X + 10, labelY, 10.2, textBrush, SemiBoldTypeface, labelWidth);
        }

        if (speedBadge)
        {
            var text = $"{clip.PlaybackRate:0.##}x";
            var width = MeasureMono(text, 8.5) + 10;
            var badge = new Rect(rect.Right - width - 5, rect.Y + 3, width, 13);
            dc.DrawRoundedRectangle(FrozenArgb(180, 8, 12, 18), null, badge, 3, 3);
            DrawText(dc, text, badge.X + 5, badge.Y + 2, 8.5, AmberBrush, MonoTypeface, width);
        }

        if (rect.Width > 100 && rect.Height >= 34)
        {
            var duration = MediaAsset.FormatDuration(clip.DurationSeconds);
            var tw = MeasureMono(duration, 8.5) + 2;
            DrawText(dc, duration, rect.Right - tw - 7, rect.Bottom - 13, 8.5,
                isSelected ? AmberBrush : DimBrush, MonoTypeface, tw);
        }

        // Opacity (visual clips) / volume (audio) rubber band.
        if (rect.Width > 26 && rect.Height > 18)
        {
            var isAudio = clip.Kind == MediaKind.Audio;
            var level = isAudio ? clip.Volume : clip.Opacity;
            var levelTop = LevelTop(rect, clip);
            var levelBottom = LevelBottom(rect);
            var levelHeight = Math.Max(1, levelBottom - levelTop);
            var lineY = levelBottom - level * levelHeight;

            var active = (_dragClip?.Id == clip.Id &&
                          (_drag == DragMode.Opacity || _drag == DragMode.Volume)) ||
                         (_hoverLevel is { } hover && hover.ClipId == clip.Id);

            var levelBrush = active
                ? (isAudio ? AudioLevelActiveBrush : LevelActiveBrush)
                : LevelLineBrush;
            var levelPen = new Pen(levelBrush, active ? 1.8 : 1.1);
            dc.DrawLine(levelPen,
                new Point(rect.X + 9, lineY), new Point(rect.Right - 6, lineY));
            dc.DrawEllipse(levelBrush, null, new Point(rect.X + 9, lineY),
                active ? 3.2 : 2.4, active ? 3.2 : 2.4);

            if (active && rect.Width > 54)
            {
                var percent = $"{level * 100:0}%";
                var width = MeasureMono(percent, 8.5) + 2;
                DrawText(dc, percent, rect.Right - width - 8,
                    Math.Max(rect.Y + 2, lineY - 12), 8.5, levelBrush, MonoTypeface, width);
            }
        }

        var borderBrush = isSelected ? AmberBrush : isHovered ? HoverBorderBrush : ClipBorderBrush;
        var pen = new Pen(borderBrush, isSelected ? 1.5 : 1);
        dc.DrawRoundedRectangle(null, pen, rect, radius, radius);

        if ((isSelected || isHovered) && rect.Width > 20)
        {
            var handleBrush = isSelected ? AmberBrush : Frozen("#7FA6C8");
            var left = _hoverClipId == clip.Id && _hoverLeft;
            var right = _hoverClipId == clip.Id && _hoverRight;
            if (left)
                dc.DrawRoundedRectangle(handleBrush, null,
                    new Rect(rect.X + 1, rect.Y + rect.Height / 2 - 8, 3, 16), 1.5, 1.5);
            if (right)
                dc.DrawRoundedRectangle(handleBrush, null,
                    new Rect(rect.Right - 4, rect.Y + rect.Height / 2 - 8, 3, 16), 1.5, 1.5);
        }
    }

    private ImageBrush GetImageBrush(ImageSource source)
    {
        if (_imageBrushes.TryGetValue(source, out var brush)) return brush;
        brush = new ImageBrush(source) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        _imageBrushes[source] = brush;
        return brush;
    }

    private static double LevelTop(Rect rect, TimelineClip clip) => rect.Y + 4;

    private static double LevelBottom(Rect rect) =>
        Math.Max(rect.Y + 12, rect.Bottom - 3);

    /// <summary>Source seconds still available from the clip's in-point.</summary>
    private double GetAvailableSource(TimelineClip clip)
    {
        if (_project == null) return 0;
        var asset = _project.FindAsset(clip.AssetId);
        if (asset == null || clip.Kind == MediaKind.Image) return 0;
        return Math.Max(0, asset.DurationSeconds - clip.InSeconds);
    }

    /// <summary>The source segment the clip currently consumes (capped to the file).</summary>
    private double GetClipSourceLength(TimelineClip clip)
    {
        var available = GetAvailableSource(clip);
        if (available <= 0.01) return 0;
        var consumed = clip.DurationSeconds * Math.Max(0.1, clip.PlaybackRate);
        return Math.Min(consumed, available);
    }

    /// <summary>Snap a normal right-edge drag back to one full pass of the source.</summary>
    private double SnapRegularEnd(TimelineClip clip, double desiredEnd)
    {
        var available = GetAvailableSource(clip);
        if (available <= 0.01) return desiredEnd;

        var rate = Math.Max(0.1, clip.PlaybackRate);
        var regularEnd = clip.StartSeconds + available / rate;
        var threshold = 8 / _pixelsPerSecond;
        return Math.Abs(desiredEnd - regularEnd) <= threshold ? regularEnd : desiredEnd;
    }

    private bool TryGetLevelHit(TimelineTrack track, TimelineClip clip, Point p, out double level)
    {
        level = clip.Kind == MediaKind.Audio ? clip.Volume : clip.Opacity;
        var rect = ClipRect(track, clip);
        if (rect.Width < 30 || rect.Height < 18) return false;

        var levelTop = LevelTop(rect, clip);
        var levelBottom = LevelBottom(rect);
        var lineY = levelBottom - level * Math.Max(1, levelBottom - levelTop);

        if (p.X < rect.X + 8 || p.X > rect.Right - 4) return false;
        return Math.Abs(p.Y - lineY) <= 6;
    }

    private void DrawVideoFilmstrip(DrawingContext dc, Rect rect, IReadOnlyList<ImageSource> frames)
    {
        var inner = new Rect(rect.X + 1, rect.Y + 1,
            Math.Max(1, rect.Width - 2), Math.Max(1, rect.Height - 2));

        dc.PushClip(new RectangleGeometry(inner, 4, 4));

        var segmentWidth = Math.Max(46, Math.Min(110, inner.Height * 2.0));
        var count = Math.Clamp((int)Math.Ceiling(inner.Width / segmentWidth), 1, 24);
        var width = inner.Width / count;

        for (var i = 0; i < count; i++)
        {
            var frame = frames[i % frames.Count];
            var segment = new Rect(inner.X + i * width, inner.Y, width + 0.5, inner.Height);
            dc.DrawRectangle(GetImageBrush(frame), null, segment);

            if (i > 0)
                dc.DrawRectangle(FrozenArgb(120, 6, 10, 16), null,
                    new Rect(segment.X, segment.Y, 1, segment.Height));
        }

        // Keep the clip name readable over the frames.
        dc.DrawRectangle(FrozenArgb(102, 8, 12, 18), null, inner);
        dc.Pop();
    }

    private void DrawWaveform(DrawingContext dc, TimelineClip clip, MediaAsset? asset, Rect rect)
    {
        if (asset?.Peaks is not { Length: > 0 } || !asset.HasAudio) return;
        if (rect.Width < 10) return;

        var mid = rect.Y + rect.Height * 0.5;
        var half = Math.Max(1.5, rect.Height * 0.36);
        DrawPeaks(dc, asset, clip, rect, mid, half, WaveBrush);
    }

    /// <summary>Waveform band over a video clip so cuts can follow the audio.</summary>
    private void DrawVideoWaveform(DrawingContext dc, TimelineClip clip, MediaAsset? asset, Rect rect)
    {
        if (asset?.Peaks is not { Length: > 0 } || !asset.HasAudio) return;
        if (rect.Height < 26 || rect.Width < 18) return;

        var top = rect.Y + rect.Height * 0.56;
        var bottom = rect.Bottom - 5;
        if (bottom - top < 5) return;

        var mid = (top + bottom) / 2;
        var half = Math.Max(1, (bottom - top) / 2 - 1);
        DrawPeaks(dc, asset, clip, rect, mid, half, VideoWaveBrush);
    }

    private static void DrawPeaks(DrawingContext dc, MediaAsset asset, TimelineClip clip,
        Rect rect, double mid, double half, Brush brush)
    {
        var peaks = asset.Peaks;
        if (peaks == null || peaks.Length == 0) return;

        var startX = rect.X + 7;
        var endX = rect.Right - 5;
        var width = endX - startX;
        if (width < 4) return;

        var duration = Math.Max(0.001, clip.DurationSeconds);
        var rate = Math.Max(0.1, clip.PlaybackRate);
        var available = asset.DurationSeconds - clip.InSeconds;
        var looped = available > 0.05 && clip.DurationSeconds * rate > available + 0.01;

        var step = 2.0;
        for (var x = startX; x < endX; x += step)
        {
            var t = (x - startX) / width * duration;
            var offset = t * rate;
            if (looped) offset %= Math.Max(0.01, available);

            var source = clip.InSeconds + Math.Max(0, offset);
            var fraction = asset.DurationSeconds > 0.01
                ? source / asset.DurationSeconds
                : 0;
            var index = Math.Clamp((int)(fraction * (peaks.Length - 1)), 0, peaks.Length - 1);
            var amplitude = peaks[index] * half;
            if (amplitude < 0.6) amplitude = 0.6;

            dc.DrawRectangle(brush, null,
                new Rect(x, mid - amplitude, 1.4, amplitude * 2));
        }
    }

    private static void DrawFilmTicks(DrawingContext dc, TimelineClip clip, Rect rect)
    {
        var brush = FrozenArgb(18, 255, 255, 255);
        var gap = 34.0;
        for (var x = rect.X + gap; x < rect.Right - 6; x += gap)
            dc.DrawRectangle(brush, null, new Rect(Math.Round(x), rect.Y + rect.Height * 0.55, 1, rect.Height * 0.35));
    }

    private double MeasureMono(string text, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            MonoTypeface, size, DimBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private void DrawDropPreview(DrawingContext dc, double width)
    {
        if (_dropPreview is not { } preview || _project == null) return;
        if (!_project.Tracks.Contains(preview.Track)) return;

        var top = TrackTop(preview.Track);
        if (top >= LaneBottom) return;

        var clipHeight = preview.Track.Kind == TrackKind.Video
            ? Math.Max(18, preview.Track.Height - 8)
            : Math.Max(16, preview.Track.Height - 12);
        var rectTop = top + (preview.Track.Height - clipHeight) / 2;

        var x1 = TimeToX(preview.Start);
        var x2 = TimeToX(preview.Start + preview.Duration);
        var left = Math.Max(HeaderWidth + 1, x1);
        var rect = new Rect(left, rectTop, Math.Max(14, x2 - left), clipHeight);

        dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, RulerHeight,
            Math.Max(0, width - HeaderWidth), Math.Max(0, LaneBottom - RulerHeight))));

        dc.DrawRoundedRectangle(DropFillBrush, DropPen, rect, 4.8, 4.8);
        DrawText(dc, preview.Name, rect.X + 9, rect.Y + 5, 10.2, TextBrush,
            SemiBoldTypeface, Math.Max(8, rect.Width - 18));
        if (rect.Height >= 30)
        {
            DrawText(dc, "Drop to insert", rect.X + 9, rect.Y + rect.Height - 14, 8.5,
                AccentBrush, MonoTypeface, Math.Max(8, rect.Width - 18));
        }

        dc.Pop();
    }

    /// <summary>Highlights the layer a dragged clip will move to, or the row a new layer would take.</summary>
    private void DrawClipDropTarget(DrawingContext dc, double width)
    {
        if (_drag != DragMode.Move || _dragClip == null || _project == null) return;

        dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, RulerHeight,
            Math.Max(0, width - HeaderWidth), Math.Max(0, LaneBottom - RulerHeight))));

        if (_dropTrack != null && !ReferenceEquals(_dropTrack, _project.TrackOf(_dragClip)))
        {
            var top = TrackTop(_dropTrack);
            var rect = new Rect(HeaderWidth + 5, top + 2,
                Math.Max(0, width - HeaderWidth - 10), Math.Max(1, _dropTrack.Height - 4));
            dc.DrawRectangle(DropFillBrush, new Pen(AccentBrush, 1), rect);
            DrawText(dc, _dropTrack.Name, rect.X + 10, rect.Y + (rect.Height - 13) / 2,
                10.5, TextBrush, SemiBoldTypeface, Math.Max(8, rect.Width - 20));
        }
        else if (_dropCreatesTrack)
        {
            var height = _dropNewTrackKind == TrackKind.Video ? 54 : 40;
            var top = Math.Min(_dropNewTrackTop + 3, LaneBottom - height - 1);
            var rect = new Rect(HeaderWidth + 5, top,
                Math.Max(0, width - HeaderWidth - 10), Math.Max(1, height - 6));
            var pen = new Pen(AccentBrush, 1.2) { DashStyle = DashStyles.Dash };
            dc.DrawRectangle(DropFillBrush, pen, rect);

            var label = _dropNewTrackKind == TrackKind.Video
                ? "Drop to create a new video layer"
                : "Drop to create a new audio layer";
            DrawText(dc, label, rect.X + 12, rect.Y + (rect.Height - 13) / 2,
                10.5, TextBrush, SemiBoldTypeface, Math.Max(8, rect.Width - 24));
        }

        dc.Pop();
    }

    private void DrawFades(DrawingContext dc, double width, IEnumerable<TimelineTrack> tracks)
    {
        if (_project == null) return;

        dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, RulerHeight,
            Math.Max(0, width - HeaderWidth), Math.Max(0, LaneBottom - RulerHeight))));

        foreach (var track in tracks)
        {
            var laneTop = TrackTop(track);
            if (laneTop >= LaneBottom) break;
            var laneHeight = Math.Min(track.Height, LaneBottom - laneTop);

            foreach (var overlap in _project.GetOverlaps(track))
            {
                var x1 = Math.Max(HeaderWidth + 1, TimeToX(overlap.StartSeconds));
                var x2 = Math.Min(width, TimeToX(overlap.EndSeconds));
                if (x2 - x1 < 2) continue;

                var hovered = _hoverFade is { } hover &&
                              ReferenceEquals(hover.Track, track) &&
                              hover.Overlap.Incoming.Id == overlap.Incoming.Id;

                dc.DrawRectangle(hovered ? FadeShadeHoverBrush : FadeShadeBrush, null,
                    new Rect(x1, laneTop, x2 - x1, laneHeight));

                var top = laneTop + 5;
                var bottom = laneTop + laneHeight - 5;
                var height = bottom - top;
                if (height <= 2) continue;

                DrawFadeCurve(dc, overlap, x1, x2, top, bottom, FadeOutPen, outgoing: true);
                DrawFadeCurve(dc, overlap, x1, x2, top, bottom, FadeInPen, outgoing: false);
            }
        }

        dc.Pop();
    }

    private static void DrawFadeCurve(DrawingContext dc, EditProject.ClipOverlap overlap,
        double x1, double x2, double top, double bottom, Pen pen, bool outgoing)
    {
        var height = bottom - top;
        var steps = Math.Clamp((int)((x2 - x1) / 3), 8, 80);
        var geometry = new StreamGeometry();

        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x1, outgoing ? top : bottom), false, false);
            for (var i = 1; i <= steps; i++)
            {
                var t = (double)i / steps;
                var eased = Ease(t, overlap.Incoming.FadeCurve);
                var y = outgoing
                    ? top + eased * height
                    : bottom - eased * height;
                ctx.LineTo(new Point(x1 + (x2 - x1) * t, y), true, false);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static double Ease(double t, FadeCurveKind kind) => kind switch
    {
        FadeCurveKind.EaseIn => t * t,
        FadeCurveKind.EaseOut => 1 - (1 - t) * (1 - t),
        FadeCurveKind.SCurve => t * t * (3 - 2 * t),
        FadeCurveKind.Exponential => (Math.Exp(4 * t) - 1) / (Math.Exp(4) - 1),
        _ => t
    };

    private void DrawHeaderTooltip(DrawingContext dc, double width)
    {
        if (_project == null) return;

        if (_hoverLevel is { } hoverLevel)
        {
            var clip = _project.FindClip(hoverLevel.ClipId);
            if (clip != null && hoverLevel.Track.Clips.Contains(clip))
            {
                var rect = ClipRect(hoverLevel.Track, clip);
                var isAudio = clip.Kind == MediaKind.Audio;
                var level = isAudio ? clip.Volume : clip.Opacity;
                var top = LevelTop(rect, clip);
                var bottom = LevelBottom(rect);
                var lineY = bottom - level * Math.Max(1, bottom - top);
                var label = isAudio
                    ? $"Volume {level * 100:0}% ֲ· drag up/down"
                    : $"Opacity {level * 100:0}% ֲ· drag up/down";
                DrawTooltipBox(dc, label, rect.X + 8, lineY - 25, width);
                return;
            }
        }

        if (_hoverFade is { } fade)
        {
            var x = Math.Min(TimeToX(fade.Overlap.StartSeconds) + 10, width - 300);
            var y = TrackTop(fade.Track) + 4;
            var label = $"Crossfade ֲ· {fade.Overlap.Incoming.FadeCurve.ToLabel()} ֲ· right-click to change";
            DrawTooltipBox(dc, label, x, y - 24, width);
        }
        else if (_hoverHeaderButton is { } hover && hover.Track != null)
        {
            var track = hover.Track;
            var text = hover.Button switch
            {
                0 => track.IsLocked ? "Unlock track" : "Lock track",
                1 => track.IsMuted ? "Unmute track" : "Mute track",
                _ => track.IsSolo ? "Unsolo track" : "Solo track"
            };
            var anchor = HeaderButtonRect(track, hover.Button);
            DrawTooltipBox(dc, text, anchor.Right + 8, anchor.Y - 2, width);
        }
    }

    private void DrawTooltipBox(DrawingContext dc, string text, double x, double y, double width)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            RegularTypeface, 10, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var boxWidth = Math.Min(formatted.Width + 16, Math.Max(60, width - 8));
        const double boxHeight = 21;
        x = Math.Clamp(x, 4, Math.Max(4, width - boxWidth - 4));
        y = Math.Clamp(y, 4, Math.Max(4, LaneBottom - boxHeight - 4));

        dc.DrawRoundedRectangle(TooltipBrush, TooltipBorderPen,
            new Rect(x, y, boxWidth, boxHeight), 4, 4);

        formatted.MaxTextWidth = Math.Max(8, boxWidth - 16);
        formatted.MaxLineCount = 1;
        formatted.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(formatted, new Point(x + 8, y + 4));
    }

    private void DrawPlayhead(DrawingContext dc, double height)
    {
        if (_project == null) return;
        var x = TimeToX(_project.Playhead);
        if (x < HeaderWidth || x > ActualWidth) return;

        var lineBottom = Math.Min(LaneBottom, height);
        dc.DrawRectangle(AmberBrush, null, new Rect(Math.Round(x), 0, 1.4, lineBottom));

        var marker = new StreamGeometry();
        using (var ctx = marker.Open())
        {
            ctx.BeginFigure(new Point(x - 5, 0), true, true);
            ctx.LineTo(new Point(x + 5, 0), true, false);
            ctx.LineTo(new Point(x, 9), true, false);
        }
        marker.Freeze();
        dc.DrawGeometry(AmberBrush, null, marker);
    }

    private void DrawScrollbar(DrawingContext dc, double width, double height)
    {
        if (width <= HeaderWidth + 40) return;

        var track = ScrollbarTrackRect(width, height);
        dc.DrawRoundedRectangle(ScrollTrackBrush, null, track, 3, 3);

        if (MaxScrollPixels <= 0.5) return;
        var thumb = ScrollbarThumbRect(track);
        dc.DrawRoundedRectangle(_scrollThumbHover ? ScrollThumbHoverBrush : ScrollThumbBrush, null, thumb, 3, 3);
    }

    private Rect ScrollbarTrackRect(double width, double height) =>
        new(HeaderWidth + LanePadding, Math.Max(RulerHeight, height - BottomBarHeight + 2),
            Math.Max(20, width - HeaderWidth - LanePadding - 10), 7);

    private Rect ScrollbarThumbRect(Rect track)
    {
        var viewport = Math.Max(1, ViewportWidth);
        var content = Math.Max(viewport, ContentSeconds * _pixelsPerSecond);
        var fraction = Math.Clamp(viewport / content, 0, 1);
        var thumbWidth = Math.Max(30, track.Width * fraction);
        var maxScroll = MaxScrollPixels;
        var progress = maxScroll <= 0 ? 0 : _scrollPixels / maxScroll;
        var x = track.X + (track.Width - thumbWidth) * progress;
        return new Rect(x, track.Y, thumbWidth, track.Height);
    }

    private void DrawVerticalScrollbar(DrawingContext dc, double width, double height)
    {
        if (MaxScrollY <= 0.5 || width <= HeaderWidth + 40) return;

        var track = VerticalScrollbarTrackRect(width, height);
        dc.DrawRoundedRectangle(ScrollTrackBrush, null, track, 3, 3);

        var thumb = VerticalScrollbarThumbRect(track);
        dc.DrawRoundedRectangle(_vScrollThumbHover ? ScrollThumbHoverBrush : ScrollThumbBrush,
            null, thumb, 3, 3);
    }

    private Rect VerticalScrollbarTrackRect(double width, double height) =>
        new(width - 10, RulerHeight, 8, Math.Max(1, LaneBottom - RulerHeight));

    private Rect VerticalScrollbarThumbRect(Rect track)
    {
        var viewport = Math.Max(1, TrackAreaHeight);
        var content = Math.Max(viewport, TotalTracksHeight);
        var thumbHeight = Math.Max(28, track.Height * Math.Min(1, viewport / content));
        var progress = MaxScrollY <= 0 ? 0 : _scrollY / MaxScrollY;
        return new Rect(track.X, track.Y + (track.Height - thumbHeight) * progress,
            track.Width, thumbHeight);
    }

    private void SetScrollYFromThumb(double thumbY, Rect track)
    {
        var viewport = Math.Max(1, TrackAreaHeight);
        var content = Math.Max(viewport, TotalTracksHeight);
        var thumbHeight = Math.Max(28, track.Height * Math.Min(1, viewport / content));
        var available = Math.Max(1, track.Height - thumbHeight);
        _scrollY = Math.Clamp((thumbY - track.Y) / available, 0, 1) * MaxScrollY;
    }

    private void DrawEmptyHint(DrawingContext dc, double width, IEnumerable<TimelineTrack> tracks)
    {
        if (_project == null || _project.Tracks.Any(t => t.Clips.Count > 0)) return;
        var areaWidth = width - HeaderWidth;
        if (areaWidth < 240) return;

        DrawText(dc,
            "Import media, then double-click it to add it to the timeline",
            HeaderWidth + areaWidth / 2 - 190, RulerHeight + 46, 11,
            Frozen("#43536A"), RegularTypeface, 380);
    }

    // ---------------------------------------------------------------- input

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_project == null) return;
        Focus();

        var p = e.GetPosition(this);
        var width = ActualWidth;
        var height = ActualHeight;

        // scrollbar first (it sits at the very bottom)
        var scrollTrack = ScrollbarTrackRect(width, height);
        if (MaxScrollPixels > 0.5 && p.Y >= scrollTrack.Y - 3 && p.Y <= scrollTrack.Y + scrollTrack.Height + 3 && p.X >= scrollTrack.X)
        {
            var thumb = ScrollbarThumbRect(scrollTrack);
            if (thumb.Contains(p))
            {
                _scrollGrabOffset = p.X - thumb.X;
            }
            else
            {
                _scrollGrabOffset = thumb.Width / 2;
                SetScrollFromThumbX(p.X - _scrollGrabOffset, scrollTrack);
            }
            _drag = DragMode.Scrollbar;
            _dragScrollStart = _scrollPixels;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // vertical scrollbar (only when the track stack overflows)
        if (MaxScrollY > 0.5 && p.X >= width - 14 && p.Y >= RulerHeight && p.Y < LaneBottom)
        {
            var vTrack = VerticalScrollbarTrackRect(width, height);
            var vThumb = VerticalScrollbarThumbRect(vTrack);
            if (vThumb.Contains(p))
            {
                _vScrollGrabOffset = p.Y - vThumb.Y;
            }
            else
            {
                _vScrollGrabOffset = vThumb.Height / 2;
                SetScrollYFromThumb(p.Y - _vScrollGrabOffset, vTrack);
            }
            _drag = DragMode.VScrollbar;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // ruler: scrub
        if (p.Y < RulerHeight && p.X >= HeaderWidth)
        {
            _drag = DragMode.Scrub;
            _project.Playhead = _project.SnapToFrame(TimeAtPoint(p));
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // layer headers
        if (p.X < HeaderWidth)
        {
            if (TryGetTrackRow(p.Y, out var headerTrack, out _) && headerTrack != null)
            {
                var button = HeaderButtonAt(headerTrack, p);
                if (button >= 0)
                {
                    _project.BeginEdit();
                    if (button == 0) headerTrack.IsLocked = !headerTrack.IsLocked;
                    else if (button == 1) headerTrack.IsMuted = !headerTrack.IsMuted;
                    else headerTrack.IsSolo = !headerTrack.IsSolo;
                    _project.NotifyTimelineChanged();
                    InvalidateVisual();
                }
            }
            e.Handled = true;
            return;
        }

        if (!TryGetTrackRow(p.Y, out var track, out _) || track == null)
        {
            // Empty space under the tracks still scrubs the playhead.
            _project.SelectedClipId = null;
            _drag = DragMode.Scrub;
            _project.Playhead = _project.SnapToFrame(TimeAtPoint(p));
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var hitClip = ClipAt(track, p);
        if (hitClip == null)
        {
            _project.SelectedClipId = null;
            _drag = DragMode.Scrub;
            _project.Playhead = _project.SnapToFrame(TimeAtPoint(p));
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // A single click anywhere on a clip both selects it and moves the
        // playhead (so S splits exactly where the user clicked).
        _project.SelectedClipId = hitClip.Id;
        _project.Playhead = _project.SnapToFrame(TimeAtPoint(p));

        if (track.IsLocked)
        {
            e.Handled = true;
            return;
        }

        if (_razorMode)
        {
            _project.BeginEdit();
            _project.SplitClip(hitClip, TimeAtPoint(p));
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2)
        {
            e.Handled = true;
            return;
        }

        if (!_razorMode && TryGetLevelHit(track, hitClip, p, out var level))
        {
            _drag = hitClip.Kind == MediaKind.Audio ? DragMode.Volume : DragMode.Opacity;
            _dragClip = hitClip;
            _dragStartPoint = p;
            _dragLevelStart = level;
            _editCaptured = false;
            _moved = false;
            Cursor = Cursors.SizeNS;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        _dragClip = hitClip;
        _dragStartPoint = p;
        _dragClipStart = hitClip.StartSeconds;
        _dragClipDuration = hitClip.DurationSeconds;
        _dragSourceLength = GetClipSourceLength(hitClip);
        _editCaptured = false;
        _moved = false;
        _dropTrack = null;
        _dropCreatesTrack = false;

        var clipRect = ClipRect(track, hitClip);
        if (_hoverLeft && clipRect.Width > 6) _drag = DragMode.TrimLeft;
        else if (_hoverRight && clipRect.Width > 6)
        {
            _drag = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _dragSourceLength > 0.05
                ? DragMode.StretchRight
                : DragMode.TrimRight;
        }
        else _drag = DragMode.Move;

        // Ctrl+drag on a clip body duplicates it and drags the copy, like Vegas.
        _ctrlDuplicate = _drag == DragMode.Move &&
                         Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);

        if (_drag == DragMode.None)
        {
            UpdateHover(p);
            return;
        }

        if (_project == null) return;

        if (_drag == DragMode.TrimRight && _dragSourceLength > 0.05 &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _drag = DragMode.StretchRight;
        }
        else if (_drag == DragMode.StretchRight &&
                 !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _drag = DragMode.TrimRight;
        }

        switch (_drag)
        {
            case DragMode.Scrollbar:
            {
                var track = ScrollbarTrackRect(ActualWidth, ActualHeight);
                SetScrollFromThumbX(p.X - _scrollGrabOffset, track);
                InvalidateVisual();
                break;
            }
            case DragMode.VScrollbar:
            {
                var track = VerticalScrollbarTrackRect(ActualWidth, ActualHeight);
                SetScrollYFromThumb(p.Y - _vScrollGrabOffset, track);
                InvalidateVisual();
                break;
            }
            case DragMode.Scrub:
            {
                _project.Playhead = _project.SnapToFrame(TimeAtPoint(p));
                InvalidateVisual();
                break;
            }
            case DragMode.Move when _dragClip != null:
            {
                var dragClip = _dragClip;
                var deltaSeconds = (p.X - _dragStartPoint.X) / _pixelsPerSecond;
                if (!_moved &&
                    Math.Abs(p.X - _dragStartPoint.X) < 2 &&
                    Math.Abs(p.Y - _dragStartPoint.Y) < 2) break;
                if (!_moved)
                {
                    _moved = true;
                    CaptureEdit();

                    if (_ctrlDuplicate && _project != null)
                    {
                        var copy = _project.DuplicateClip(dragClip);
                        if (copy != null)
                        {
                            copy.StartSeconds = dragClip.StartSeconds;
                            dragClip = copy;
                            _dragClip = copy;
                            _dragClipStart = copy.StartSeconds;
                            _dragClipDuration = copy.DurationSeconds;
                            _dragSourceLength = GetClipSourceLength(copy);
                            _project.SelectedClipId = copy.Id;
                        }
                        _ctrlDuplicate = false;
                    }
                }

                UpdateClipDropTarget(p.Y, dragClip);

                var desired = _dragClipStart + deltaSeconds;
                desired = SnapClipStart(desired, _dragClipDuration, dragClip);
                _project!.MoveClip(dragClip, desired);
                InvalidateVisual();
                break;
            }
            case DragMode.TrimLeft when _dragClip != null:
            {
                if (!_moved)
                {
                    _moved = true;
                    CaptureEdit();
                }
                var desired = _dragClipStart + (p.X - _dragStartPoint.X) / _pixelsPerSecond;
                desired = SnapEdge(desired, _dragClip);
                _project.TrimClipLeft(_dragClip, desired);
                InvalidateVisual();
                break;
            }
            case DragMode.TrimRight when _dragClip != null:
            {
                if (!_moved)
                {
                    _moved = true;
                    CaptureEdit();
                }
                var desired = _dragClipStart + _dragClipDuration +
                              (p.X - _dragStartPoint.X) / _pixelsPerSecond;
                desired = SnapEdge(desired, _dragClip);
                desired = SnapRegularEnd(_dragClip, desired);
                _project.TrimClipRight(_dragClip, desired);
                InvalidateVisual();
                break;
            }
            case DragMode.StretchRight when _dragClip != null:
            {
                if (!_moved)
                {
                    _moved = true;
                    CaptureEdit();
                }

                var sourceLength = _dragSourceLength;
                if (sourceLength <= 0.05) break;

                var end = _dragClipStart + _dragClipDuration +
                          (p.X - _dragStartPoint.X) / _pixelsPerSecond;
                var duration = Math.Max(_project.FrameDuration, end - _dragClipStart);
                var threshold = 8 / _pixelsPerSecond;

                double rate;
                if (Math.Abs(duration - sourceLength) <= threshold)
                {
                    rate = 1;
                    duration = sourceLength;
                }
                else
                {
                    rate = Math.Clamp(sourceLength / duration, 0.1, 10);
                }

                var roundedRate = Math.Round(rate, 3);
                _dragClip.PlaybackRate = roundedRate;
                _dragClip.DurationSeconds = Math.Max(_project.FrameDuration, sourceLength / roundedRate);
                InvalidateVisual();
                break;
            }
            case DragMode.Opacity when _dragClip != null:
            case DragMode.Volume when _dragClip != null:
            {
                if (!_moved)
                {
                    if (Math.Abs(p.Y - _dragStartPoint.Y) < 1) break;
                    _moved = true;
                    CaptureEdit();
                }

                var levelTrack = _project.TrackOf(_dragClip);
                if (levelTrack == null) break;

                var rect = ClipRect(levelTrack, _dragClip);
                var top = LevelTop(rect, _dragClip);
                var bottom = LevelBottom(rect);
                var delta = (_dragStartPoint.Y - p.Y) / Math.Max(1, bottom - top);
                var value = Math.Round(Math.Clamp(_dragLevelStart + delta, 0, 1), 2);

                if (_drag == DragMode.Opacity) _dragClip.Opacity = value;
                else _dragClip.Volume = value;

                ClipLevelChanged?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
                break;
            }
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        var wasEditing = _drag is DragMode.Move or DragMode.TrimLeft or DragMode.TrimRight
            or DragMode.StretchRight or DragMode.Opacity or DragMode.Volume;
        if (wasEditing && _moved && _project != null)
        {
            if (_drag == DragMode.Move && _dragClip != null)
                CommitClipLayerDrop();
            _project.NotifyTimelineChanged();
        }

        _drag = DragMode.None;
        _dragClip = null;
        _moved = false;
        _editCaptured = false;
        _ctrlDuplicate = false;
        _dropTrack = null;
        _dropCreatesTrack = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    /// <summary>
    /// Works out the layer a dragged clip will land on. Dropping below the last
    /// layer of the clip's kind creates a new one, like Vegas.
    /// </summary>
    private void UpdateClipDropTarget(double y, TimelineClip clip)
    {
        if (_project == null) return;

        _dropTrack = null;
        _dropCreatesTrack = false;

        var kind = clip.Kind == MediaKind.Audio ? TrackKind.Audio : TrackKind.Video;

        // Drop on an existing compatible layer.
        if (TryGetTrackRow(y, out var row, out _) && row != null &&
            row.Kind == kind && !row.IsLocked)
        {
            _dropTrack = row;
            return;
        }

        // Drop below the last layer of this kind -> new layer.
        TimelineTrack? last = null;
        var top = RulerHeight - _scrollY;
        var lastTop = top;
        foreach (var track in _project.Tracks)
        {
            if (track.Kind == kind)
            {
                last = track;
                lastTop = top;
            }
            top += track.Height;
        }

        if (last == null) return;
        if (y > lastTop + last.Height + 2 && y < LaneBottom)
        {
            _dropCreatesTrack = true;
            _dropNewTrackTop = lastTop + last.Height;
            _dropNewTrackKind = kind;
        }
    }

    private void CommitClipLayerDrop()
    {
        if (_project == null || _dragClip == null) return;

        if (_dropCreatesTrack)
        {
            var kind = _dragClip.Kind == MediaKind.Audio ? TrackKind.Audio : TrackKind.Video;
            var created = _project.AddTrack(kind);
            _project.MoveClipToTrack(_dragClip, created);
            _dropCreatesTrack = false;
            return;
        }

        if (_dropTrack != null)
            _project.MoveClipToTrack(_dragClip, _dropTrack);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_project == null) return;

        var p = e.GetPosition(this);
        if (!TryGetTrackRow(p.Y, out var track, out _) || track == null) return;

        if (p.X < HeaderWidth)
        {
            ShowTrackMenu(track);
            e.Handled = true;
            return;
        }

        if (TryGetFadeAt(p, out var fadeTrack, out var fade) && fadeTrack != null && fade != null)
        {
            ShowFadeMenu(fadeTrack, fade);
            e.Handled = true;
            return;
        }

        var clip = ClipAt(track, p);
        if (clip == null) return;

        _project.SelectedClipId = clip.Id;
        var menu = new ContextMenu();
        var split = new MenuItem { Header = "Split at playhead", Icon = MenuIcon("M 7,1 V 13 M 8,1 V 13 M 3,4 L 6,7 L 3,10") };
        split.Click += (_, _) =>
        {
            _project.BeginEdit();
            _project.SplitClip(clip, _project.Playhead);
        };
        var duplicate = new MenuItem { Header = "Duplicate", Icon = MenuIcon("M 3,2 H 10 V 9 H 3 Z M 6,5 H 13 V 12 H 6 Z") };
        duplicate.Click += (_, _) =>
        {
            _project.BeginEdit();
            _project.DuplicateClip(clip);
        };
        var delete = new MenuItem { Header = "Delete", Icon = MenuIcon("M 3,4 H 13 M 6,4 V 2 H 10 V 4 M 4.5,4 L 5,13 H 11 L 11.5,4") };
        delete.Click += (_, _) =>
        {
            _project.BeginEdit();
            _project.DeleteClip(clip);
        };
        menu.Items.Add(split);
        menu.Items.Add(duplicate);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.PlacementTarget = this;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static System.Windows.Shapes.Path MenuIcon(string data) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = Frozen("#7FA6C8"),
        StrokeThickness = 1.3,
        Width = 15,
        Height = 15,
        Stretch = Stretch.Uniform,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round
    };

    private void ShowTrackMenu(TimelineTrack track)
    {        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = track.Name, IsEnabled = false });
        menu.Items.Add(new Separator());

        var rename = new MenuItem { Header = "Rename layer", Icon = MenuIcon("M 2,12 L 3,9 L 10,2 L 12,4 L 5,11 Z M 9,3 L 11,5") };
        rename.Click += (_, _) => RenameTrackRequested?.Invoke(this, track);
        menu.Items.Add(rename);

        var remove = new MenuItem { Header = "Remove layer", Icon = MenuIcon("M 3,4 H 13 M 6,4 V 2 H 10 V 4 M 4.5,4 L 5,13 H 11 L 11.5,4") };
        remove.Click += (_, _) => RemoveTrackRequested?.Invoke(this, track);
        menu.Items.Add(remove);

        menu.PlacementTarget = this;
        menu.IsOpen = true;
    }

    private void ShowFadeMenu(TimelineTrack track, EditProject.ClipOverlap overlap)
    {
        if (_project == null || track.IsLocked) return;

        var incoming = overlap.Incoming;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Fade curve", IsEnabled = false });

        foreach (var kind in Enum.GetValues<FadeCurveKind>())
        {
            var curve = kind;
            var item = new MenuItem { Header = curve.ToLabel() };
            if (curve == incoming.FadeCurve)
            {
                item.Icon = new Border
                {
                    Width = 5,
                    Height = 5,
                    CornerRadius = new CornerRadius(2.5),
                    Background = AccentBrush,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            item.Click += (_, _) =>
            {
                if (incoming.FadeCurve == curve) return;
                _project.BeginEdit();
                incoming.FadeCurve = curve;
                _project.NotifyTimelineChanged();
                InvalidateVisual();
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var removeOverlap = new MenuItem { Header = "Remove overlap" };
        removeOverlap.Click += (_, _) =>
        {
            var target = Math.Max(incoming.StartSeconds, overlap.Outgoing.EndSeconds);
            if (Math.Abs(target - incoming.StartSeconds) < 0.0001) return;
            _project.BeginEdit();
            incoming.StartSeconds = target;
            _project.NotifyTimelineChanged();
            InvalidateVisual();
        };
        menu.Items.Add(removeOverlap);

        menu.PlacementTarget = this;
        menu.IsOpen = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_project == null) return;
        var p = e.GetPosition(this);

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            // Alt + wheel scrolls the track stack vertically.
            _scrollY -= e.Delta / 120.0 * 40;
            ClampScroll();
            InvalidateVisual();
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            // Shift + wheel pans the timeline horizontally.
            _scrollPixels -= e.Delta / 120.0 * 90;
            ClampScroll();
            InvalidateVisual();
        }
        else
        {
            // Plain wheel zooms around the pointer, keeping the time under the
            // cursor in place. Ctrl/Alt do the same for users used to it.
            var factor = e.Delta > 0 ? 1.18 : 1 / 1.18;
            ZoomAt(_pixelsPerSecond * factor, Math.Max(0, TimeAtPoint(p)), p.X);
        }
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_drag != DragMode.None) return;
        _hoverClipId = null;
        _hoverLeft = false;
        _hoverRight = false;
        _hoverHeaderButton = null;
        _hoverFade = null;
        _hoverLevel = null;
        _scrollThumbHover = false;
        _vScrollThumbHover = false;
        Cursor = _razorMode ? Cursors.Cross : Cursors.Arrow;
        InvalidateVisual();
    }

    private void CaptureEdit()
    {
        if (_editCaptured) return;
        _editCaptured = true;
        _project?.BeginEdit();
    }

    private void UpdateHover(Point p)
    {
        var previousClip = _hoverClipId;
        var previousLeft = _hoverLeft;
        var previousRight = _hoverRight;
        var previousHeader = _hoverHeaderButton;
        var previousScroll = _scrollThumbHover;
        var previousVScroll = _vScrollThumbHover;
        var previousFade = _hoverFade?.Overlap.Incoming.Id;
        var previousLevel = _hoverLevel?.ClipId;

        _hoverClipId = null;
        _hoverLeft = false;
        _hoverRight = false;
        _hoverHeaderButton = null;
        _hoverFade = null;
        _hoverLevel = null;
        _scrollThumbHover = false;
        _vScrollThumbHover = false;
        var cursor = _razorMode ? Cursors.Cross : Cursors.Arrow;

        var scrollTrack = ScrollbarTrackRect(ActualWidth, ActualHeight);
        if (MaxScrollPixels > 0.5 && p.Y >= scrollTrack.Y - 2 && p.Y <= scrollTrack.Y + scrollTrack.Height + 2 &&
            p.X >= scrollTrack.X)
        {
            _scrollThumbHover = ScrollbarThumbRect(scrollTrack).Contains(p);
            cursor = Cursors.Hand;
        }
        else if (MaxScrollY > 0.5 && p.X >= ActualWidth - 14 &&
                 p.Y >= RulerHeight && p.Y < LaneBottom)
        {
            _vScrollThumbHover = VerticalScrollbarThumbRect(
                VerticalScrollbarTrackRect(ActualWidth, ActualHeight)).Contains(p);
            cursor = Cursors.Hand;
        }
        else if (p.Y < RulerHeight && p.X >= HeaderWidth)
        {
            cursor = Cursors.SizeWE;
        }
        else if (p.X < HeaderWidth)
        {
            if (TryGetTrackRow(p.Y, out var headerTrack, out _) && headerTrack != null)
            {
                var button = HeaderButtonAt(headerTrack, p);
                if (button >= 0)
                {
                    _hoverHeaderButton = (headerTrack, button);
                    cursor = Cursors.Hand;
                }
            }
        }
        else if (TryGetTrackRow(p.Y, out var track, out _) && track != null)
        {
            var clip = ClipAt(track, p);
            if (!_razorMode && clip != null && TryGetLevelHit(track, clip, p, out _))
            {
                _hoverLevel = (track, clip.Id);
                cursor = Cursors.SizeNS;
            }
            else if (TryGetFadeAt(p, out var fadeTrack, out var fade) && fadeTrack != null && fade != null)
            {
                _hoverFade = (fadeTrack, fade);
                cursor = Cursors.Hand;
            }
            else if (clip != null)
            {
                _hoverClipId = clip.Id;
                var rect = ClipRect(track, clip);
                var edge = Math.Min(8, Math.Max(3, rect.Width / 3));
                _hoverLeft = Math.Abs(p.X - rect.X) <= edge;
                _hoverRight = Math.Abs(rect.Right - p.X) <= edge;
                cursor = _razorMode ? Cursors.Cross
                    : _hoverLeft || _hoverRight ? Cursors.SizeWE
                    : Cursors.Hand;
            }
        }

        if (previousClip != _hoverClipId || previousLeft != _hoverLeft || previousRight != _hoverRight ||
            previousHeader != _hoverHeaderButton || previousScroll != _scrollThumbHover ||
            previousVScroll != _vScrollThumbHover ||
            previousFade != _hoverFade?.Overlap.Incoming.Id ||
            previousLevel != _hoverLevel?.ClipId)
        {
            Cursor = cursor;
            InvalidateVisual();
        }
        else if (Cursor != cursor)
        {
            Cursor = cursor;
        }
    }

    private void SetScrollFromThumbX(double thumbX, Rect track)
    {
        var viewport = Math.Max(1, ViewportWidth);
        var content = Math.Max(viewport, ContentSeconds * _pixelsPerSecond);
        var thumbWidth = Math.Max(30, track.Width * Math.Clamp(viewport / content, 0, 1));
        var available = Math.Max(1, track.Width - thumbWidth);
        var progress = Math.Clamp((thumbX - track.X) / available, 0, 1);
        _scrollPixels = progress * MaxScrollPixels;
    }

    private bool TryGetTrackRow(double y, out TimelineTrack? track, out int index)
    {
        track = null;
        index = -1;
        if (_project == null || y < RulerHeight || y >= LaneBottom) return false;

        var top = RulerHeight - _scrollY;
        var i = 0;
        foreach (var candidate in _project.Tracks)
        {
            if (y >= top && y < top + candidate.Height)
            {
                track = candidate;
                index = i;
                return true;
            }
            top += candidate.Height;
            i++;
            if (top > LaneBottom) break;
        }
        return false;
    }

    private int HeaderButtonAt(TimelineTrack track, Point p)
    {
        if (p.X < HeaderWidth - 76 || p.X > HeaderWidth) return -1;
        if (p.Y < RulerHeight) return -1;
        var top = TrackTop(track);
        if (p.Y < top || p.Y > top + track.Height) return -1;

        for (var button = 0; button < 3; button++)
            if (HeaderButtonRect(track, button).Contains(p))
                return button;

        return -1;
    }

    private double TrackTop(TimelineTrack track)
    {
        double top = RulerHeight - _scrollY;
        if (_project == null) return top;
        foreach (var candidate in _project.Tracks)
        {
            if (ReferenceEquals(candidate, track)) return top;
            top += candidate.Height;
        }
        return top;
    }

    private Rect ClipRect(TimelineTrack track, TimelineClip clip)
    {
        var top = TrackTop(track);
        var clipHeight = track.Kind == TrackKind.Video
            ? Math.Max(18, track.Height - 8)
            : Math.Max(16, track.Height - 12);
        var y = top + (track.Height - clipHeight) / 2;
        var x1 = TimeToX(clip.StartSeconds);
        var x2 = TimeToX(clip.EndSeconds);
        return new Rect(Math.Max(HeaderWidth + 1, x1), y, Math.Max(3, x2 - Math.Max(HeaderWidth + 1, x1)), clipHeight);
    }

    private TimelineClip? ClipAt(TimelineTrack track, Point p)
    {
        foreach (var clip in track.Clips.OrderBy(c => c.StartSeconds))
        {
            var rect = ClipRect(track, clip);
            if (rect.Contains(p)) return clip;
        }
        return null;
    }

    private bool TryGetFadeAt(Point p, out TimelineTrack? track, out EditProject.ClipOverlap? overlap)
    {
        track = null;
        overlap = null;
        if (_project == null) return false;

        foreach (var candidate in _project.Tracks)
        {
            var top = TrackTop(candidate);
            if (p.Y < top || p.Y > top + candidate.Height) continue;

            foreach (var fade in _project.GetOverlaps(candidate))
            {
                var x1 = TimeToX(fade.StartSeconds);
                var x2 = TimeToX(fade.EndSeconds);
                if (p.X >= x1 - 3 && p.X <= x2 + 3)
                {
                    track = candidate;
                    overlap = fade;
                    return true;
                }
            }
        }

        return false;
    }

    // --------------------------------------------------------------- snapping

    private double SnapClipStart(double desiredStart, double duration, TimelineClip exclude)
    {
        var frameSnapped = _project!.SnapToFrame(desiredStart);
        if (_project == null || !_snapEnabled) return frameSnapped;

        var threshold = 8 / _pixelsPerSecond;
        var best = frameSnapped;
        var bestDistance = double.MaxValue;

        void Consider(double target)
        {
            var dStart = Math.Abs(desiredStart - target);
            if (dStart < bestDistance) { bestDistance = dStart; best = target; }
            var dEnd = Math.Abs(desiredStart + duration - target);
            if (dEnd < bestDistance) { bestDistance = dEnd; best = target - duration; }
        }

        Consider(0);
        Consider(_project.Playhead);
        foreach (var track in _project.Tracks)
            foreach (var clip in track.Clips)
            {
                if (ReferenceEquals(clip, exclude)) continue;
                Consider(clip.StartSeconds);
                Consider(clip.EndSeconds);
            }

        return bestDistance <= threshold ? _project.SnapToFrame(Math.Max(0, best)) : frameSnapped;
    }

    private double SnapEdge(double desired, TimelineClip exclude)
    {
        var frameSnapped = _project!.SnapToFrame(desired);
        if (_project == null || !_snapEnabled) return frameSnapped;

        var threshold = 8 / _pixelsPerSecond;
        var best = frameSnapped;
        var bestDistance = threshold;

        void Consider(double target)
        {
            var d = Math.Abs(desired - target);
            if (d < bestDistance) { bestDistance = d; best = target; }
        }

        Consider(0);
        Consider(_project.Playhead);
        foreach (var track in _project.Tracks)
            foreach (var clip in track.Clips)
            {
                if (ReferenceEquals(clip, exclude)) continue;
                Consider(clip.StartSeconds);
                Consider(clip.EndSeconds);
            }

        return _project.SnapToFrame(Math.Max(0, best));
    }
}
