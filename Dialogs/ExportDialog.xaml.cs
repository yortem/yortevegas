using System.IO;
using System.Windows;
using Microsoft.Win32;
using YorteVegas.Services;
using YorteVegas.ViewModels;

namespace YorteVegas.Dialogs;

public partial class ExportDialog : Window
{
    private readonly EditProject _project;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;
    private bool _exporting;

    public bool Exported { get; private set; }
    public string? ExportedPath { get; private set; }

    public ExportDialog(EditProject project, AppSettings settings)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        _project = project;
        _settings = settings;

        var great = ExportService.CreatePreset(project, ExportQuality.Great);
        var good = ExportService.CreatePreset(project, ExportQuality.Good);
        var light = ExportService.CreatePreset(project, ExportQuality.Light);

        GreatTitle.Text = great.Title;
        GreatTagline.Text = great.Tagline;
        GreatDescription.Text = great.Description;
        GoodTitle.Text = good.Title;
        GoodTagline.Text = good.Tagline;
        GoodDescription.Text = good.Description;
        LightTitle.Text = light.Title;
        LightTagline.Text = light.Tagline;
        LightDescription.Text = light.Description;

        OutputBox.Text = ExportService.DefaultOutputPath(project);
        GoodCard.IsChecked = true;

        Closing += ExportDialog_Closing;
        Loaded += (_, _) => ExportButton.Focus();
    }

    private ExportQuality SelectedQuality =>
        GreatCard.IsChecked == true ? ExportQuality.Great :
        LightCard.IsChecked == true ? ExportQuality.Light :
        ExportQuality.Good;

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Exported)
        {
            Close();
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;

        var outputPath = OutputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            ShowError("Choose where to save the video first.");
            return;
        }

        if (!Path.HasExtension(outputPath))
            outputPath += ".mp4";
        if (!outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            outputPath = Path.ChangeExtension(outputPath, ".mp4");

        var ffmpeg = ExportService.LocateFfmpeg(_settings.FFmpegPath);
        if (ffmpeg == null)
        {
            var locate = new OpenFileDialog
            {
                Title = "Locate ffmpeg.exe",
                Filter = "FFmpeg|ffmpeg.exe|Programs|*.exe",
                CheckFileExists = true
            };
            if (locate.ShowDialog(this) != true)
            {
                ShowError("Export needs FFmpeg. Locate ffmpeg.exe to continue.");
                return;
            }

            ffmpeg = locate.FileName;
            _settings.FFmpegPath = ffmpeg;
            _settings.Save();
        }

        _cts = new CancellationTokenSource();
        _exporting = true;
        SetBusy(true);
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = "Preparing export…";

        var progress = new Progress<ExportProgress>(value => ProgressText.Text = value.Stage);

        try
        {
            await ExportService.ExportAsync(_project, SelectedQuality, outputPath,
                ffmpeg, progress, _cts.Token);

            Exported = true;
            ExportedPath = outputPath;
            ProgressText.Text = $"Saved to {outputPath}";
            ExportButton.Content = "Done";
            CancelButton.Content = "Close";
            ExportButton.IsEnabled = true;
            SetBusy(false);
            SetBusyFinal();
        }
        catch (OperationCanceledException)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            ProgressText.Text = "Export cancelled.";
            SetBusy(false);
        }
        catch (Exception ex)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            ShowError(ex.Message);
            SetBusy(false);
        }
        finally
        {
            _exporting = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var current = OutputBox.Text.Trim();
        var dialog = new SaveFileDialog
        {
            Title = "Export video",
            Filter = "MP4 video|*.mp4",
            DefaultExt = ".mp4",
            AddExtension = true,
            FileName = string.IsNullOrWhiteSpace(current) ? "YorteVegas.mp4" : Path.GetFileName(current)
        };

        var folder = string.IsNullOrWhiteSpace(current) ? null : Path.GetDirectoryName(current);
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            dialog.InitialDirectory = folder;

        if (dialog.ShowDialog(this) == true)
            OutputBox.Text = dialog.FileName;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting)
        {
            _cts?.Cancel();
            ProgressText.Text = "Cancelling…";
            return;
        }

        Close();
    }

    private void ExportDialog_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_exporting) return;
        _cts?.Cancel();
        e.Cancel = true;
    }

    private void SetBusy(bool busy)
    {
        GreatCard.IsEnabled = !busy;
        GoodCard.IsEnabled = !busy;
        LightCard.IsEnabled = !busy;
        OutputBox.IsEnabled = !busy;
        ExportButton.IsEnabled = !busy;
        CancelButton.IsEnabled = true;
        CancelButton.Content = busy ? "Cancel export" : "Cancel";
        if (busy) ProgressPanel.Visibility = Visibility.Visible;
    }

    private void SetBusyFinal()
    {
        GreatCard.IsEnabled = false;
        GoodCard.IsEnabled = false;
        LightCard.IsEnabled = false;
        OutputBox.IsEnabled = false;
        ExportButton.IsEnabled = true;
        CancelButton.IsEnabled = true;
        CancelButton.Content = "Close";
        ExportButton.Content = "Done";
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
