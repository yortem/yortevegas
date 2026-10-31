using System.Globalization;
using System.Windows;
using YorteVegas.Services;

namespace YorteVegas.Dialogs;

public partial class SettingsDialog : Window
{
    private static readonly string[] FrameRates =
        { "23.976", "24", "25", "29.97", "30", "50", "59.94", "60" };

    public AppSettings Settings { get; }

    public SettingsDialog(AppSettings settings)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        Settings = settings;
        WidthBox.Text = settings.DefaultWidth.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = settings.DefaultHeight.ToString(CultureInfo.InvariantCulture);
        ImageDurationBox.Text = settings.ImageDuration.ToString("0.##", CultureInfo.InvariantCulture);
        VolumeSlider.Value = Math.Clamp(settings.PreviewVolume, 0, 100);
        OpenLastCheck.IsChecked = settings.OpenLastProject;

        FpsBox.ItemsSource = FrameRates;
        FpsBox.SelectedItem = NearestFrameRate(settings.DefaultFps);

        VolumeSlider.ValueChanged += (_, _) =>
            VolumeText.Text = $"{VolumeSlider.Value:0}%";
        VolumeText.Text = $"{VolumeSlider.Value:0}%";

        Loaded += (_, _) => WidthBox.Focus();
    }

    private static string NearestFrameRate(double fps)
    {
        var best = FrameRates[4];
        double bestDistance = double.MaxValue;
        foreach (var item in FrameRates)
        {
            if (!double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                continue;
            var distance = Math.Abs(value - fps);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = item;
            }
        }
        return best;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        if (!int.TryParse(WidthBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            width < 16 || width > 7680)
        {
            ShowError("Default width must be between 16 and 7680.");
            return;
        }

        if (!int.TryParse(HeightBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
            height < 16 || height > 4320)
        {
            ShowError("Default height must be between 16 and 4320.");
            return;
        }

        if (!double.TryParse(ImageDurationBox.Text.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var imageDuration) ||
            imageDuration < 0.2 || imageDuration > 600)
        {
            ShowError("Still image duration must be between 0.2 and 600 seconds.");
            return;
        }

        double fps = 30;
        if (FpsBox.SelectedItem is string fpsText)
            double.TryParse(fpsText, NumberStyles.Float, CultureInfo.InvariantCulture, out fps);

        Settings.DefaultWidth = width;
        Settings.DefaultHeight = height;
        Settings.DefaultFps = fps > 0 ? fps : 30;
        Settings.PreviewVolume = VolumeSlider.Value;
        Settings.ImageDuration = imageDuration;
        Settings.OpenLastProject = OpenLastCheck.IsChecked == true;
        Settings.Save();

        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
