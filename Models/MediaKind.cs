namespace YorteVegas.Models;

/// <summary>What a library asset / timeline clip is made of.</summary>
public enum MediaKind
{
    Video,
    Audio,
    Image,
    Text
}

public static class MediaKindExtensions
{
    public static bool IsVisual(this MediaKind kind) => kind != MediaKind.Audio;

    public static string ToLabel(this MediaKind kind) => kind switch
    {
        MediaKind.Video => "Video",
        MediaKind.Audio => "Audio",
        MediaKind.Text => "Text",
        _ => "Image"
    };
}
