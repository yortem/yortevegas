namespace YorteVegas.Models;

/// <summary>
/// One placed piece of media on a track. <see cref="StartSeconds"/> is project
/// time; <see cref="InSeconds"/> is the source offset inside the asset.
/// </summary>
public sealed class TimelineClip : ObservableObject
{
    private double _startSeconds;
    private double _durationSeconds;
    private double _inSeconds;
    private string _name = "";
    private bool _isSelected;
    private FadeCurveKind _fadeCurve = FadeCurveKind.Linear;
    private double _opacity = 1;
    private double _volume = 1;
    private double _playbackRate = 1;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssetId { get; set; }
    public MediaKind Kind { get; set; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public double StartSeconds
    {
        get => _startSeconds;
        set
        {
            if (SetProperty(ref _startSeconds, value)) OnPropertyChanged(nameof(EndSeconds));
        }
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (SetProperty(ref _durationSeconds, value)) OnPropertyChanged(nameof(EndSeconds));
        }
    }

    public double InSeconds
    {
        get => _inSeconds;
        set => SetProperty(ref _inSeconds, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Curve used when this clip starts inside the previous clip on the same
    /// track (a crossfade). The timeline draws it over the overlap.
    /// </summary>
    public FadeCurveKind FadeCurve
    {
        get => _fadeCurve;
        set => SetProperty(ref _fadeCurve, value);
    }

    /// <summary>Visual opacity for video/image clips (0..1).</summary>
    public double Opacity
    {
        get => _opacity;
        set => SetProperty(ref _opacity, Math.Clamp(value, 0, 1));
    }

    /// <summary>Audio level for this clip (0..1), multiplied by the monitor volume.</summary>
    public double Volume
    {
        get => _volume;
        set => SetProperty(ref _volume, Math.Clamp(value, 0, 1));
    }

    /// <summary>
    /// Playback speed for the source media. 1 = normal speed; lower values are
    /// slow motion (Ctrl-drag the right edge). Timeline duration × rate is the
    /// amount of source media the clip consumes.
    /// </summary>
    public double PlaybackRate
    {
        get => _playbackRate;
        set => SetProperty(ref _playbackRate, Math.Clamp(value, 0.1, 10));
    }

    public double EndSeconds => StartSeconds + DurationSeconds;

    public double SourceOutSeconds => InSeconds + DurationSeconds * PlaybackRate;

    /// <summary>
    /// Source time for a project time. Asset-aware looping is handled by the
    /// preview service; this is the raw rate-mapped position.
    /// </summary>
    public double TimeAt(double projectSeconds) =>
        InSeconds + Math.Max(0, projectSeconds - StartSeconds) * PlaybackRate;
}
