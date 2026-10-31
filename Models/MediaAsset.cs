using System.Windows.Media;

namespace YorteVegas.Models;

/// <summary>
/// A video, audio or image file referenced by the project. Media is never
/// copied into the .yveg file — only its path is stored.
/// </summary>
public sealed class MediaAsset : ObservableObject
{
    private bool _isMissing;
    private double _durationSeconds;
    private int _width;
    private int _height;
    private ImageSource? _thumbnail;
    private bool _filmstripUnavailable;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public MediaKind Kind { get; set; }

    /// <summary>Frames sampled from a video for the timeline filmstrip (not serialized).</summary>
    public List<ImageSource> Filmstrip { get; } = new();

    /// <summary>True after an extraction attempt produced nothing; stops retry loops.</summary>
    public bool FilmstripUnavailable
    {
        get => _filmstripUnavailable;
        set => SetProperty(ref _filmstripUnavailable, value);
    }

    public void SetFilmstrip(IReadOnlyList<ImageSource> frames)
    {
        Filmstrip.Clear();
        Filmstrip.AddRange(frames);
        FilmstripUnavailable = frames.Count == 0;
        OnPropertyChanged(nameof(Filmstrip));
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (!SetProperty(ref _durationSeconds, value)) return;
            OnPropertyChanged(nameof(DurationLabel));
            OnPropertyChanged(nameof(MetaLabel));
        }
    }

    public int Width
    {
        get => _width;
        set
        {
            if (SetProperty(ref _width, value)) OnPropertyChanged(nameof(MetaLabel));
        }
    }

    public int Height
    {
        get => _height;
        set
        {
            if (SetProperty(ref _height, value)) OnPropertyChanged(nameof(MetaLabel));
        }
    }

    public bool HasAudio { get; set; }

    public bool IsMissing
    {
        get => _isMissing;
        set => SetProperty(ref _isMissing, value);
    }

    /// <summary>Library thumbnail (images only, for now). Not serialized.</summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    /// <summary>Normalized audio peaks (0..1) for the timeline waveform. Not serialized.</summary>
    public float[]? Peaks { get; set; }

    public string DurationLabel => Kind == MediaKind.Image
        ? "still"
        : FormatDuration(DurationSeconds);

    public string MetaLabel
    {
        get
        {
            if (Kind == MediaKind.Image)
                return Width > 0 && Height > 0 ? $"Image · {Width} × {Height}" : "Image · still";

            var size = Width > 0 && Height > 0 ? $" · {Width} × {Height}" : "";
            var length = DurationSeconds > 0.05 ? $" · {DurationLabel}" : " · length unknown";
            return $"{Kind.ToLabel()}{size}{length}";
        }
    }

    public static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}
