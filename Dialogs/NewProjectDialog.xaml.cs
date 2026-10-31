using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using YorteVegas.Models;
using YorteVegas.Services;

namespace YorteVegas.Dialogs;

public partial class NewProjectDialog : Window
{
    private static readonly string[] FrameRates =
        { "23.976", "24", "25", "29.97", "30", "50", "59.94", "60" };

    private bool _initializing = true;

    public ProjectSettings Settings { get; private set; } = new();

    public NewProjectDialog(int width, int height, double fps)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        FpsBox.ItemsSource = FrameRates;
        FpsBox.SelectedItem = NearestFrameRate(fps);

        WidthBox.Text = width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = height.ToString(CultureInfo.InvariantCulture);

        SelectMatchingPreset(width, height);
        _initializing = false;

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
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

    private void SelectMatchingPreset(int width, int height)
    {
        _initializing = true;
        PresetCustom.IsChecked = true;

        if (width == 1920 && height == 1080) Preset1080.IsChecked = true;
        else if (width == 3840 && height == 2160) Preset2160.IsChecked = true;
        else if (width == 1080 && height == 1920) PresetVertical.IsChecked = true;
        else if (width == 1080 && height == 1080) PresetSquare.IsChecked = true;
        else if (width == 1280 && height == 720) Preset720.IsChecked = true;

        UpdateCustomFields();
        _initializing = false;
    }

    private void Preset_Checked(object sender, RoutedEventArgs e)
    {
        if (WidthBox == null || _initializing) return;
        UpdateCustomFields();
    }

    private void UpdateCustomFields()
    {
        var isCustom = PresetCustom.IsChecked == true;
        WidthBox.IsEnabled = isCustom;
        HeightBox.IsEnabled = isCustom;

        if (isCustom) return;
        if (Preset1080.IsChecked == true) SetSize(1920, 1080);
        else if (Preset2160.IsChecked == true) SetSize(3840, 2160);
        else if (PresetVertical.IsChecked == true) SetSize(1080, 1920);
        else if (PresetSquare.IsChecked == true) SetSize(1080, 1080);
        else if (Preset720.IsChecked == true) SetSize(1280, 720);
    }

    private void SetSize(int width, int height)
    {
        WidthBox.Text = width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = height.ToString(CultureInfo.InvariantCulture);
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        if (!int.TryParse(WidthBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            width < 16 || width > 7680)
        {
            ShowError("Width must be a whole number between 16 and 7680.");
            return;
        }

        if (!int.TryParse(HeightBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
            height < 16 || height > 4320)
        {
            ShowError("Height must be a whole number between 16 and 4320.");
            return;
        }

        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "Untitled";

        double fps = 30;
        if (FpsBox.SelectedItem is string fpsText)
            double.TryParse(fpsText, NumberStyles.Float, CultureInfo.InvariantCulture, out fps);

        Settings = new ProjectSettings
        {
            Name = name,
            Width = width,
            Height = height,
            Fps = fps > 0 ? fps : 30
        };

        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
