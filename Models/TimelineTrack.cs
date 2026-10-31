using System.Collections.ObjectModel;

namespace YorteVegas.Models;

public enum TrackKind
{
    Video,
    Audio
}

/// <summary>A timeline layer. Video tracks carry video and still-image clips.</summary>
public sealed class TimelineTrack : ObservableObject
{
    private bool _isMuted;
    private bool _isLocked;
    private bool _isSolo;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Track";
    public TrackKind Kind { get; set; }

    public ObservableCollection<TimelineClip> Clips { get; } = new();

    public bool IsMuted
    {
        get => _isMuted;
        set => SetProperty(ref _isMuted, value);
    }

    public bool IsLocked
    {
        get => _isLocked;
        set => SetProperty(ref _isLocked, value);
    }

    public bool IsSolo
    {
        get => _isSolo;
        set => SetProperty(ref _isSolo, value);
    }

    public double Height => Kind == TrackKind.Video ? 54 : 40;

    public bool Accepts(MediaKind kind) =>
        Kind == TrackKind.Video ? kind.IsVisual() : kind == MediaKind.Audio;
}
