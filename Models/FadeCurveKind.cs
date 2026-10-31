namespace YorteVegas.Models;

/// <summary>Shape of the crossfade created when two clips overlap.</summary>
public enum FadeCurveKind
{
    Linear,
    EaseIn,
    EaseOut,
    SCurve,
    Exponential
}

public static class FadeCurveKindExtensions
{
    public static string ToLabel(this FadeCurveKind kind) => kind switch
    {
        FadeCurveKind.EaseIn => "Ease in",
        FadeCurveKind.EaseOut => "Ease out",
        FadeCurveKind.SCurve => "S-curve",
        FadeCurveKind.Exponential => "Exponential",
        _ => "Linear"
    };
}
