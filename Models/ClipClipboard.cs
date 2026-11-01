namespace YorteVegas.Models;

/// <summary>
/// In-app clipboard for whole clips (Ctrl+C / Ctrl+V). Only the values needed to
/// rebuild a clip are stored, so pasting never shares live clip instances.
/// </summary>
public static class ClipClipboard
{
    public sealed record Snapshot(
        Guid TrackId,
        Guid AssetId,
        string Name,
        MediaKind Kind,
        double Start,
        double Duration,
        double In,
        FadeCurveKind FadeCurve,
        double Opacity,
        double Volume,
        double PlaybackRate,
        double FadeIn,
        double FadeOut,
        double CropLeft,
        double CropRight,
        double CropTop,
        double CropBottom,
        double PanX,
        double PanY,
        double Scale,
        double PositionX,
        double PositionY,
        IReadOnlyList<ClipEffect> Effects,
        ClipTransition? TransitionIn,
        ClipTransition? TransitionOut);

    private static List<Snapshot> _items = new();

    public static bool HasClips => _items.Count > 0;
    public static int Count => _items.Count;
    public static IReadOnlyList<Snapshot> Items => _items;

    public static Snapshot Capture(TimelineClip clip, Guid trackId) => new(
        trackId,
        clip.AssetId,
        clip.Name,
        clip.Kind,
        clip.StartSeconds,
        clip.DurationSeconds,
        clip.InSeconds,
        clip.FadeCurve,
        clip.Opacity,
        clip.Volume,
        clip.PlaybackRate,
        clip.FadeInSeconds,
        clip.FadeOutSeconds,
        clip.CropLeft,
        clip.CropRight,
        clip.CropTop,
        clip.CropBottom,
        clip.PanX,
        clip.PanY,
        clip.Scale,
        clip.PositionX,
        clip.PositionY,
        clip.Effects.Select(e => e.Clone()).ToList(),
        clip.TransitionIn?.Clone(),
        clip.TransitionOut?.Clone());

    public static void Set(IEnumerable<Snapshot> items) => _items = items.ToList();

    public static void Clear() => _items = new();
}
