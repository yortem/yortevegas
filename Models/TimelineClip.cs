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
    private double _fadeInSeconds;
    private double _fadeOutSeconds;
    private double _cropLeft;
    private double _cropRight;
    private double _cropTop;
    private double _cropBottom;
    private double _panX;
    private double _panY;
    private double _scale = 1;
    private double _positionX;
    private double _positionY;

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
    /// <summary>Crop fractions of the source (0..0.9 in total per axis).</summary>
    public double CropLeft
    {
        get => _cropLeft;
        set => SetProperty(ref _cropLeft, Math.Clamp(value, 0, 0.95));
    }

    public double CropRight
    {
        get => _cropRight;
        set => SetProperty(ref _cropRight, Math.Clamp(value, 0, 0.95));
    }

    public double CropTop
    {
        get => _cropTop;
        set => SetProperty(ref _cropTop, Math.Clamp(value, 0, 0.95));
    }

    public double CropBottom
    {
        get => _cropBottom;
        set => SetProperty(ref _cropBottom, Math.Clamp(value, 0, 0.95));
    }

    /// <summary>Pan offset in source fractions (-1..1), clamped to the cropped window.</summary>
    public double PanX
    {
        get => _panX;
        set => SetProperty(ref _panX, Math.Clamp(value, -1, 1));
    }

    public double PanY
    {
        get => _panY;
        set => SetProperty(ref _panY, Math.Clamp(value, -1, 1));
    }

    public bool HasCrop => _cropLeft > 0.001 || _cropRight > 0.001 ||
                           _cropTop > 0.001 || _cropBottom > 0.001 ||
                           Math.Abs(_panX) > 0.001 || Math.Abs(_panY) > 0.001;

    /// <summary>
    /// Size of the clip on the project frame, relative to the fitted size.
    /// 1 = fit (full frame for matching aspects, letterboxed otherwise).
    /// </summary>
    public double Scale
    {
        get => _scale;
        set => SetProperty(ref _scale, Math.Clamp(value, 0.05, 4));
    }

    /// <summary>Centre offset from the frame centre, as a fraction of the frame.</summary>
    public double PositionX
    {
        get => _positionX;
        set => SetProperty(ref _positionX, Math.Clamp(value, -1, 1));
    }

    public double PositionY
    {
        get => _positionY;
        set => SetProperty(ref _positionY, Math.Clamp(value, -1, 1));
    }

    public bool HasPlacement => Math.Abs(_scale - 1) > 0.001 ||
                                Math.Abs(_positionX) > 0.001 ||
                                Math.Abs(_positionY) > 0.001;

    /// <summary>
    /// The clip's effect stack, applied top to bottom. Effects are visual only;
    /// they render in the monitor and in the export, never on the audio.
    /// </summary>
    public List<ClipEffect> Effects { get; set; } = new();

    public bool HasEffects => Effects.Count > 0;

    public int EnabledEffectCount => Effects.Count(e => e.Enabled);

    /// <summary>Manual fade-in at the start of the clip, in project seconds.</summary>
    public double FadeInSeconds
    {
        get => _fadeInSeconds;
        set => SetProperty(ref _fadeInSeconds, Math.Clamp(value, 0, 30));
    }

    /// <summary>Manual fade-out at the end of the clip, in project seconds.</summary>
    public double FadeOutSeconds
    {
        get => _fadeOutSeconds;
        set => SetProperty(ref _fadeOutSeconds, Math.Clamp(value, 0, 30));
    }

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
