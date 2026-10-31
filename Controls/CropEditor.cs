using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YorteVegas.Controls;

/// <summary>
/// Visual crop window over a still frame. The crop rectangle is normalized
/// (0..1) to the image; drag inside to pan, drag an edge or corner to crop.
/// </summary>
public sealed class CropEditor : FrameworkElement
{
    private static readonly Brush DimBrush = Frozen(Color.FromArgb(165, 3, 7, 12));
    private static readonly Brush AccentBrush = Frozen(Color.FromRgb(46, 155, 255));
    private static readonly Brush HandleBrush = Frozen(Color.FromRgb(230, 238, 246));
    private static readonly Pen BorderPen = new(AccentBrush, 1.6);
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromArgb(70, 230, 238, 246)), 1);

    private enum Mode { None, Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    private BitmapSource? _image;
    private Rect _crop = new(0, 0, 1, 1);
    private Mode _mode;
    private Point _start;
    private Rect _startCrop;

    public BitmapSource? Image
    {
        get => _image;
        set
        {
            _image = value;
            InvalidateVisual();
        }
    }

    /// <summary>Normalized crop rectangle inside the image (0..1).</summary>
    public Rect Crop
    {
        get => _crop;
        set
        {
            _crop = ClampRect(value);
            InvalidateVisual();
            CropChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CropChanged;

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
        if (_image != null)
            dc.DrawImage(_image, new Rect(0, 0, width, height));

        var rect = ToPixels(_crop, width, height);

        dc.DrawRectangle(DimBrush, null, new Rect(0, 0, width, Math.Max(0, rect.Top)));
        dc.DrawRectangle(DimBrush, null, new Rect(0, rect.Bottom, width, Math.Max(0, height - rect.Bottom)));
        dc.DrawRectangle(DimBrush, null, new Rect(0, rect.Top, Math.Max(0, rect.Left), Math.Max(0, rect.Height)));
        dc.DrawRectangle(DimBrush, null, new Rect(rect.Right, rect.Top, Math.Max(0, width - rect.Right), Math.Max(0, rect.Height)));

        dc.DrawRectangle(null, BorderPen, rect);

        var thirdX = rect.Width / 3;
        var thirdY = rect.Height / 3;
        dc.DrawLine(GridPen, new Point(rect.X + thirdX, rect.Top), new Point(rect.X + thirdX, rect.Bottom));
        dc.DrawLine(GridPen, new Point(rect.X + thirdX * 2, rect.Top), new Point(rect.X + thirdX * 2, rect.Bottom));
        dc.DrawLine(GridPen, new Point(rect.Left, rect.Y + thirdY), new Point(rect.Right, rect.Y + thirdY));
        dc.DrawLine(GridPen, new Point(rect.Left, rect.Y + thirdY * 2), new Point(rect.Right, rect.Y + thirdY * 2));

        foreach (var point in HandlePoints(rect))
            dc.DrawRectangle(HandleBrush, null, new Rect(point.X - 3.5, point.Y - 3.5, 7, 7));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_image == null) return;

        var p = e.GetPosition(this);
        _start = p;
        _startCrop = _crop;
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
        var dx = (p.X - _start.X) / Math.Max(1, ActualWidth);
        var dy = (p.Y - _start.Y) / Math.Max(1, ActualHeight);
        var crop = _startCrop;

        switch (_mode)
        {
            case Mode.Move:
                crop = new Rect(
                    Math.Clamp(crop.X + dx, 0, 1 - crop.Width),
                    Math.Clamp(crop.Y + dy, 0, 1 - crop.Height),
                    crop.Width, crop.Height);
                break;
            case Mode.Left:
                crop = WithLeft(crop, crop.X + dx);
                break;
            case Mode.Right:
                crop = WithRight(crop, crop.Right + dx);
                break;
            case Mode.Top:
                crop = WithTop(crop, crop.Y + dy);
                break;
            case Mode.Bottom:
                crop = WithBottom(crop, crop.Bottom + dy);
                break;
            case Mode.TopLeft:
                crop = WithTop(WithLeft(crop, crop.X + dx), crop.Y + dy);
                break;
            case Mode.TopRight:
                crop = WithTop(WithRight(crop, crop.Right + dx), crop.Y + dy);
                break;
            case Mode.BottomLeft:
                crop = WithBottom(WithLeft(crop, crop.X + dx), crop.Bottom + dy);
                break;
            case Mode.BottomRight:
                crop = WithBottom(WithRight(crop, crop.Right + dx), crop.Bottom + dy);
                break;
        }

        Crop = crop;
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
        var rect = ToPixels(_crop, ActualWidth, ActualHeight);
        const double edge = 12;

        var left = Math.Abs(p.X - rect.Left) <= edge;
        var right = Math.Abs(p.X - rect.Right) <= edge;
        var top = Math.Abs(p.Y - rect.Top) <= edge;
        var bottom = Math.Abs(p.Y - rect.Bottom) <= edge;

        if (left && top) return Mode.TopLeft;
        if (right && top) return Mode.TopRight;
        if (left && bottom) return Mode.BottomLeft;
        if (right && bottom) return Mode.BottomRight;
        if (left) return Mode.Left;
        if (right) return Mode.Right;
        if (top) return Mode.Top;
        if (bottom) return Mode.Bottom;
        return rect.Contains(p) ? Mode.Move : Mode.None;
    }

    private static Rect WithLeft(Rect rect, double left)
    {
        var right = rect.Right;
        left = Math.Clamp(left, 0, right - 0.05);
        return new Rect(left, rect.Y, right - left, rect.Height);
    }

    private static Rect WithRight(Rect rect, double right)
    {
        var left = rect.X;
        right = Math.Clamp(right, left + 0.05, 1);
        return new Rect(left, rect.Y, right - left, rect.Height);
    }

    private static Rect WithTop(Rect rect, double top)
    {
        var bottom = rect.Bottom;
        top = Math.Clamp(top, 0, bottom - 0.05);
        return new Rect(rect.X, top, rect.Width, bottom - top);
    }

    private static Rect WithBottom(Rect rect, double bottom)
    {
        var top = rect.Y;
        bottom = Math.Clamp(bottom, top + 0.05, 1);
        return new Rect(rect.X, top, rect.Width, bottom - top);
    }

    private static Rect ClampRect(Rect rect)
    {
        var x = Math.Clamp(rect.X, 0, 0.95);
        var y = Math.Clamp(rect.Y, 0, 0.95);
        var w = Math.Clamp(rect.Width, 0.05, 1 - x);
        var h = Math.Clamp(rect.Height, 0.05, 1 - y);
        return new Rect(x, y, w, h);
    }

    private static Rect ToPixels(Rect normalized, double width, double height) =>
        new(normalized.X * width, normalized.Y * height,
            normalized.Width * width, normalized.Height * height);

    private static Point[] HandlePoints(Rect rect) => new[]
    {
        new Point(rect.Left, rect.Top),
        new Point(rect.Left + rect.Width / 2, rect.Top),
        new Point(rect.Right, rect.Top),
        new Point(rect.Right, rect.Top + rect.Height / 2),
        new Point(rect.Right, rect.Bottom),
        new Point(rect.Left + rect.Width / 2, rect.Bottom),
        new Point(rect.Left, rect.Bottom),
        new Point(rect.Left, rect.Top + rect.Height / 2)
    };

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
