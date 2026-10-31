using System;
using System.Windows;
using System.Windows.Media.Imaging;
using YorteVegas.Models;
using YorteVegas.Services;

namespace YorteVegas.Dialogs;

public partial class CropPanDialog : Window
{
    public double CropLeft { get; private set; }
    public double CropRight { get; private set; }
    public double CropTop { get; private set; }
    public double CropBottom { get; private set; }
    public double PanX { get; private set; }
    public double PanY { get; private set; }

    public CropPanDialog(TimelineClip clip, BitmapSource still)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        var aspect = still.PixelHeight / (double)Math.Max(1, still.PixelWidth);
        Editor.Width = 760;
        Editor.Height = Math.Clamp(Math.Round(760 * aspect), 260, 520);
        Editor.Image = still;

        var width = Math.Max(0.05, 1 - clip.CropLeft - clip.CropRight);
        var height = Math.Max(0.05, 1 - clip.CropTop - clip.CropBottom);
        var x = Math.Clamp(clip.CropLeft + clip.PanX, 0, 1 - width);
        var y = Math.Clamp(clip.CropTop + clip.PanY, 0, 1 - height);
        Editor.Crop = new Rect(x, y, width, height);
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => Editor.Crop = new Rect(0, 0, 1, 1);

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var crop = Editor.Crop;
        CropLeft = crop.X;
        CropTop = crop.Y;
        CropRight = Math.Max(0, 1 - crop.Right);
        CropBottom = Math.Max(0, 1 - crop.Bottom);
        PanX = 0;
        PanY = 0;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
