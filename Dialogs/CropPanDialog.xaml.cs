using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using YorteVegas.Controls;
using YorteVegas.Models;
using YorteVegas.Services;

namespace YorteVegas.Dialogs;

public partial class CropPanDialog : Window
{
    private readonly BitmapSource _still;
    private bool _syncing;

    public double CropLeft { get; private set; }
    public double CropRight { get; private set; }
    public double CropTop { get; private set; }
    public double CropBottom { get; private set; }
    public double PanX { get; private set; }
    public double PanY { get; private set; }

    public double Scale { get; private set; } = 1;
    public double PositionX { get; private set; }
    public double PositionY { get; private set; }

    public CropPanDialog(TimelineClip clip, BitmapSource still, double projectAspect)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _still = still;

        var aspect = still.PixelHeight / (double)Math.Max(1, still.PixelWidth);
        var editorHeight = Math.Clamp(Math.Round(760 * aspect), 260, 520);
        Editor.Width = 760;
        Editor.Height = editorHeight;
        PositionView.Width = 760;
        PositionView.Height = editorHeight;
        PositionView.ProjectAspect = projectAspect;

        var width = Math.Max(0.05, 1 - clip.CropLeft - clip.CropRight);
        var height = Math.Max(0.05, 1 - clip.CropTop - clip.CropBottom);
        var x = Math.Clamp(clip.CropLeft + clip.PanX, 0, 1 - width);
        var y = Math.Clamp(clip.CropTop + clip.PanY, 0, 1 - height);
        Editor.Crop = new Rect(x, y, width, height);
        Editor.Image = still;

        PositionView.Scale = clip.Scale;
        PositionView.OffsetX = clip.PositionX;
        PositionView.OffsetY = clip.PositionY;

        Editor.CropChanged += (_, _) =>
        {
            if (Tabs.SelectedIndex == 1) UpdatePositionImage();
        };
        PositionView.Changed += (_, _) => SyncPlacementControls();
        UpdatePositionImage();
        SyncPlacementControls();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedIndex == 1 && PositionView != null && Editor != null)
            UpdatePositionImage();
    }

    private void UpdatePositionImage()
    {
        var crop = Editor.Crop;
        var pixelWidth = _still.PixelWidth;
        var pixelHeight = _still.PixelHeight;
        var x = (int)Math.Clamp(Math.Round(crop.X * pixelWidth), 0, Math.Max(0, pixelWidth - 1));
        var y = (int)Math.Clamp(Math.Round(crop.Y * pixelHeight), 0, Math.Max(0, pixelHeight - 1));
        var width = (int)Math.Clamp(Math.Round(crop.Width * pixelWidth), 1, Math.Max(1, pixelWidth - x));
        var height = (int)Math.Clamp(Math.Round(crop.Height * pixelHeight), 1, Math.Max(1, pixelHeight - y));

        try
        {
            var bitmap = new CroppedBitmap(_still, new Int32Rect(x, y, width, height));
            bitmap.Freeze();
            PositionView.Image = bitmap;
        }
        catch
        {
            PositionView.Image = _still;
        }
    }

    private void SyncPlacementControls()
    {
        _syncing = true;
        ScaleSlider.Value = Math.Clamp(PositionView.Scale,
            ScaleSlider.Minimum, ScaleSlider.Maximum);
        ScaleText.Text = $"{PositionView.Scale * 100:0}%";
        _syncing = false;
    }

    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || PositionView == null || ScaleText == null) return;
        PositionView.Scale = e.NewValue;
        ScaleText.Text = $"{e.NewValue * 100:0}%";
    }

    private void FullFrame_Click(object sender, RoutedEventArgs e) =>
        PositionView.Place(PlacementCorner.FullFrame);

    private void Center_Click(object sender, RoutedEventArgs e) =>
        PositionView.Place(PlacementCorner.Center);

    private void Corner_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        if (!Enum.TryParse<PlacementCorner>(tag, out var corner)) return;
        PositionView.Place(corner);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedIndex == 0) Editor.Crop = new Rect(0, 0, 1, 1);
        else PositionView.Reset();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var crop = Editor.Crop;
        CropLeft = crop.X;
        CropTop = crop.Y;
        CropRight = Math.Max(0, 1 - crop.Right);
        CropBottom = Math.Max(0, 1 - crop.Bottom);
        PanX = 0;
        PanY = 0;

        Scale = PositionView.Scale;
        PositionX = PositionView.OffsetX;
        PositionY = PositionView.OffsetY;

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
