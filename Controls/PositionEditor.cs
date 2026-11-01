using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YorteVegas.Controls;

public enum PlacementCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Center,
    FullFrame
}

/// <summary>
/// Places the (already cropped) clip on the project frame. Drag inside to move
/// it, drag a corner to scale it uniformly around its centre. The values are
/// normalized: scale 1 = fit the frame, offsets are frame fractions.
/// </summary>
public sealed class PositionEditor : FrameworkElement
{
    private static readonly Brush BackgroundBrush = Frozen("#05070A");
    private static readonly Brush FrameBrush = Frozen("#000000");
    private static readonly Pen FramePen = new(Frozen("#223248"), 1);
    private static readonly Pen ClipPen = new(Frozen("#2E9BFF"), 1.4);
    private static readonly Brush HandleBrush = Frozen("#E6EEF6");

    private enum Mode { None, Move, Scale }

    private BitmapSource? _image;
    private double _projectAspect = 16.0 / 9.0;
    private double _scale = 1;
    private double _offsetX;
    private double _offsetY;
    private Mode _mode;
    private Point _start;
    private double _startScale = 1;
    private double _startOffsetX;
    private double _startOffsetY;
    private Rect _frame;
    private Rect _clip;

    public BitmapSource? Image
    {
        get => _image;
        set
        {
            _image = value;
            InvalidateVisual();
        }
    }

    public double ProjectAspect
    {
        get => _projectAspect;
        set
        {
            _projectAspect = value > 0.01 ? value : 16.0 / 9.0;
            InvalidateVisual();
        }
    }

    /// <summary>1 = fit the frame; smaller values shrink the clip.</summary>
    public double Scale
    {
        get => _scale;
        set
        {
            _scale = Math.Clamp(value, 0.05, 4);
            InvalidateVisual();
        }
    }

    /// <summary>Centre offset from the frame centre, as a fraction of the frame.</summary>
    public double OffsetX
    {
        get => _offsetX;
        set
        {
            _offsetX = Math.Clamp(value, -1, 1);
            InvalidateVisual();
        }
    }

    public double OffsetY
    {
        get => _offsetY;
        set
        {
            _offsetY = Math.Clamp(value, -1, 1);
            InvalidateVisual();
        }
    }

    public event EventHandler? Changed;

    public void Reset() => Place(PlacementCorner.FullFrame);

    /// <summary>Snaps to a corner (shrinking to 50% first when still full-frame).</summary>
    public void Place(PlacementCorner corner)
    {
        var aspect = ImageAspect;
        var frameAspect = _projectAspect;
        double fitWidth;
        double fitHeight;
        if (frameAspect > aspect)
        {
            fitHeight = 1;
            fitWidth = aspect / frameAspect;
        }
        else
        {
            fitWidth = 1;
            fitHeight = frameAspect / aspect;
        }

        if (corner == PlacementCorner.FullFrame)
        {
            _scale = 1;
            _offsetX = 0;
            _offsetY = 0;
        }
        else if (corner == PlacementCorner.Center)
        {
            _offsetX = 0;
            _offsetY = 0;
        }
        else
        {
            if (_scale > 0.999) _scale = 0.5;

            const double margin = 0.04;
            var halfWidth = fitWidth * _scale / 2;
            var halfHeight = fitHeight * _scale / 2;
            var signX = corner is PlacementCorner.TopLeft or PlacementCorner.BottomLeft ? -1 : 1;
            var signY = corner is PlacementCorner.TopLeft or PlacementCorner.TopRight ? -1 : 1;

            _offsetX = signX * Math.Max(0, 0.5 - halfWidth - margin);
            _offsetY = signY * Math.Max(0, 0.5 - halfHeight - margin);
        }

        InvalidateVisual();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private double ImageAspect =>
        _image is { PixelWidth: > 0, PixelHeight: > 0 }
            ? _image.PixelWidth / (double)_image.PixelHeight
            : _projectAspect;

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));

        const double padding = 14;
        var available = new Rect(padding, padding,
            Math.Max(1, width - padding * 2), Math.Max(1, height - padding * 2));

        var frameWidth = available.Width;
        var frameHeight = frameWidth / _projectAspect;
        if (frameHeight > available.Height)
        {
            frameHeight = available.Height;
            frameWidth = frameHeight * _projectAspect;
        }

        _frame = new Rect(
            available.X + (available.Width - frameWidth) / 2,
            available.Y + (available.Height - frameHeight) / 2,
            frameWidth, frameHeight);

        dc.DrawRectangle(FrameBrush, null, _frame);
        dc.DrawRectangle(null, FramePen, _frame);

        _clip = ClipRect(_frame, ImageAspect, _scale, _offsetX, _offsetY);

        if (_image != null)
        {
            dc.PushClip(new RectangleGeometry(_frame));
            dc.DrawImage(_image, _clip);
            dc.Pop();
        }

        dc.DrawRectangle(null, ClipPen, _clip);
        foreach (var point in CornerPoints(_clip))
            dc.DrawRectangle(HandleBrush, null, new Rect(point.X - 4, point.Y - 4, 8, 8));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_image == null) return;

        var p = e.GetPosition(this);
        _start = p;
        _startScale = _scale;
        _startOffsetX = _offsetX;
        _startOffsetY = _offsetY;
        _mode = HitMode(p);
        if (_mode == Mode.None) return;

        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_mode == Mode.None) return;

        var p = e.GetPosition(this);
        if (_mode == Mode.Move)
        {
            _offsetX = Math.Clamp(_startOffsetX + (p.X - _start.X) / Math.Max(1, _frame.Width), -1, 1);
            _offsetY = Math.Clamp(_startOffsetY + (p.Y - _start.Y) / Math.Max(1, _frame.Height), -1, 1);
        }
        else
        {
            var center = new Point(
                _frame.X + _frame.Width / 2 + _startOffsetX * _frame.Width,
                _frame.Y + _frame.Height / 2 + _startOffsetY * _frame.Height);
            var startDistance = Math.Max(1, Distance(_start, center));
            var distance = Distance(p, center);
            _scale = Math.Clamp(_startScale * distance / startDistance, 0.05, 4);
        }

        InvalidateVisual();
        Changed?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_mode == Mode.None) return;
        _mode = Mode.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        e.Handled = true;
    }

    private Mode HitMode(Point p)
    {
        foreach (var point in CornerPoints(_clip))
        {
            if (Math.Abs(p.X - point.X) <= 10 && Math.Abs(p.Y - point.Y) <= 10)
                return Mode.Scale;
        }

        return _clip.Contains(p) ? Mode.Move : Mode.None;
    }

    private static Rect ClipRect(Rect frame, double imageAspect, double scale,
        double offsetX, double offsetY)
    {
        var frameAspect = frame.Width / Math.Max(1, frame.Height);
        double fitWidth;
        double fitHeight;
        if (frameAspect > imageAspect)
        {
            fitHeight = frame.Height;
            fitWidth = fitHeight * imageAspect;
        }
        else
        {
            fitWidth = frame.Width;
            fitHeight = fitWidth / imageAspect;
        }

        var width = fitWidth * scale;
        var height = fitHeight * scale;
        var centerX = frame.X + frame.Width / 2 + offsetX * frame.Width;
        var centerY = frame.Y + frame.Height / 2 + offsetY * frame.Height;
        return new Rect(centerX - width / 2, centerY - height / 2, width, height);
    }

    private static Point[] CornerPoints(Rect rect) => new[]
    {
        new Point(rect.Left, rect.Top),
        new Point(rect.Right, rect.Top),
        new Point(rect.Left, rect.Bottom),
        new Point(rect.Right, rect.Bottom)
    };

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
