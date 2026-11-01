namespace YorteVegas.Models;

public enum EffectKind
{
    ColorBalance,
    BrightnessContrast,
    Blur,
    GaussianBlur,
    Sharpen,
    FilmGrain,
    Glow,
    ChromaKey,
    Crop,
    Vignette
}

/// <summary>
/// One effect in a clip's stack. Parameters are stored by key so the catalog can
/// add parameters without changing the project schema.
/// </summary>
public sealed class ClipEffect
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public EffectKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    public Dictionary<string, double> Values { get; set; } = new();
    public double Get(string key, double fallback) =>
        Values.TryGetValue(key, out var value) ? value : fallback;

    public void Set(string key, double value) => Values[key] = value;

    public ClipEffect Clone() => new()
    {
        Id = Guid.NewGuid(),
        Kind = Kind,
        Enabled = Enabled,
        Values = new Dictionary<string, double>(Values)
    };
}

/// <summary>Payload for dragging a preset from the effects panel onto a clip.</summary>
public sealed record EffectDropData(
    EffectKind Kind,
    Dictionary<string, double> Values);
