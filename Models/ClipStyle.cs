using System;

namespace YorteVegas.Models;

/// <summary>
/// The look of a clip: levels, fades and crop/pan. Timing (start, in-point,
/// duration, rate) is not part of a style.
/// </summary>
public sealed class ClipStyle
{
    public double Opacity { get; init; } = 1;
    public double Volume { get; init; } = 1;
    public double FadeInSeconds { get; init; }
    public double FadeOutSeconds { get; init; }
    public double CropLeft { get; init; }
    public double CropRight { get; init; }
    public double CropTop { get; init; }
    public double CropBottom { get; init; }
    public double PanX { get; init; }
    public double PanY { get; init; }
    public FadeCurveKind FadeCurve { get; init; } = FadeCurveKind.Linear;

    public static ClipStyle From(TimelineClip clip) => new()
    {
        Opacity = clip.Opacity,
        Volume = clip.Volume,
        FadeInSeconds = clip.FadeInSeconds,
        FadeOutSeconds = clip.FadeOutSeconds,
        CropLeft = clip.CropLeft,
        CropRight = clip.CropRight,
        CropTop = clip.CropTop,
        CropBottom = clip.CropBottom,
        PanX = clip.PanX,
        PanY = clip.PanY,
        FadeCurve = clip.FadeCurve
    };

    public void ApplyTo(TimelineClip clip)
    {
        clip.Opacity = Opacity;
        clip.Volume = Volume;
        clip.FadeInSeconds = FadeInSeconds;
        clip.FadeOutSeconds = FadeOutSeconds;
        clip.FadeCurve = FadeCurve;

        var width = Math.Clamp(1 - CropLeft - CropRight, 0.05, 1);
        var height = Math.Clamp(1 - CropTop - CropBottom, 0.05, 1);
        clip.CropLeft = Math.Clamp(CropLeft, 0, 1 - width);
        clip.CropTop = Math.Clamp(CropTop, 0, 1 - height);
        clip.CropRight = Math.Clamp(1 - clip.CropLeft - width, 0, 0.45);
        clip.CropBottom = Math.Clamp(1 - clip.CropTop - height, 0, 0.45);
        clip.PanX = PanX;
        clip.PanY = PanY;
    }
}

/// <summary>Session-wide clipboard for clip styles (copy/paste style).</summary>
public static class ClipStyleClipboard
{
    public static ClipStyle? Current { get; private set; }
    public static bool HasStyle => Current != null;

    public static void Copy(TimelineClip clip) => Current = ClipStyle.From(clip);

    public static bool ApplyTo(TimelineClip clip)
    {
        if (Current == null) return false;
        Current.ApplyTo(clip);
        return true;
    }
}
