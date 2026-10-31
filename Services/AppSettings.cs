using System.IO;
using System.Text.Json;

namespace YorteVegas.Services;

/// <summary>
/// Persisted user preferences under <c>%AppData%\YorteVegas\settings.json</c>.
/// A missing or corrupt file simply falls back to the defaults.
/// </summary>
public sealed class AppSettings
{
    public int DefaultWidth { get; set; } = 1920;
    public int DefaultHeight { get; set; } = 1080;
    public double DefaultFps { get; set; } = 30;
    public double PreviewVolume { get; set; } = 100;

    /// <summary>Duration used when a still image is added to the timeline.</summary>
    public double ImageDuration { get; set; } = 5;

    public bool OpenLastProject { get; set; } = true;
    public string LastProjectPath { get; set; } = "";

    /// <summary>Optional path to ffmpeg.exe used for export. Empty = auto-detect.</summary>
    public string FFmpegPath { get; set; } = "";

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "YorteVegas", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // Corrupt or unreadable: use defaults rather than fail to start.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings are best-effort; never crash the editor over them.
        }
    }
}
