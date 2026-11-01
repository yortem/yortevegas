using System.Globalization;

namespace YorteVegas.Models;

public enum EffectParameterKind
{
    Slider,
    Choice
}

public sealed record EffectChoice(double Value, string Label);

/// <summary>A named set of values that fills every parameter of an effect.</summary>
public sealed record EffectPreset(
    string Name,
    IReadOnlyDictionary<string, double> Values);

public sealed record EffectParameter(
    string Key,
    string Label,
    double Min,
    double Max,
    double Default,
    double Step,
    string Format,
    EffectParameterKind Kind = EffectParameterKind.Slider,
    IReadOnlyList<EffectChoice>? Choices = null,
    string? Gradient = null);

public sealed record EffectDefinition(
    EffectKind Kind,
    string Name,
    string Description,
    IReadOnlyList<EffectParameter> Parameters,
    IReadOnlyList<EffectPreset> Presets);

/// <summary>
/// The six built-in effects, their editable parameters and the FFmpeg filters
/// used by the preview (mpv <c>vf</c>) and the export graph.
/// </summary>
public static class EffectCatalog
{
    private static readonly EffectDefinition[] Definitions =
    {
        new(EffectKind.ColorBalance, "Color Balance",
            "Tint the midtones and adjust saturation, brightness and contrast.",
            new EffectParameter[]
            {
                new("red", "Red", 0, 2, 1, 0.01, "0.00", Gradient: "red"),
                new("green", "Green", 0, 2, 1, 0.01, "0.00", Gradient: "green"),
                new("blue", "Blue", 0, 2, 1, 0.01, "0.00", Gradient: "blue"),
                new("saturation", "Saturation", 0, 2, 1, 0.01, "0.00", Gradient: "saturation"),
                new("brightness", "Brightness", -1, 1, 0, 0.01, "0.00", Gradient: "brightness"),
                new("contrast", "Contrast", 0, 2, 1, 0.01, "0.00", Gradient: "contrast")
            },
            new[]
            {
                Preset("Neutral", ("red", 1), ("green", 1), ("blue", 1),
                    ("saturation", 1), ("brightness", 0), ("contrast", 1)),
                Preset("Warm", ("red", 1.16), ("green", 1.02), ("blue", 0.86),
                    ("saturation", 1.08), ("brightness", 0.02), ("contrast", 1.05)),
                Preset("Cool", ("red", 0.86), ("green", 0.98), ("blue", 1.2),
                    ("saturation", 1.02), ("brightness", 0), ("contrast", 1.06)),
                Preset("Vintage", ("red", 1.14), ("green", 0.98), ("blue", 0.8),
                    ("saturation", 0.72), ("brightness", 0.03), ("contrast", 0.9)),
                Preset("Vivid", ("red", 1.06), ("green", 1.02), ("blue", 1.04),
                    ("saturation", 1.55), ("brightness", 0.02), ("contrast", 1.2)),
                Preset("Black & White", ("red", 1), ("green", 1), ("blue", 1),
                    ("saturation", 0), ("brightness", 0.02), ("contrast", 1.12))
            }),
        new(EffectKind.BrightnessContrast, "Brightness & Contrast",
            "Lift or crush the picture with simple brightness and contrast.",
            new EffectParameter[]
            {
                new("brightness", "Brightness", -1, 1, 0, 0.01, "0.00", Gradient: "brightness"),
                new("contrast", "Contrast", 0, 2, 1, 0.01, "0.00", Gradient: "contrast")
            },
            new[]
            {
                Preset("Neutral", ("brightness", 0), ("contrast", 1)),
                Preset("Bright", ("brightness", 0.14), ("contrast", 1.05)),
                Preset("Dark", ("brightness", -0.14), ("contrast", 1.05)),
                Preset("Punchy", ("brightness", 0.02), ("contrast", 1.4)),
                Preset("Soft", ("brightness", -0.02), ("contrast", 0.82))
            }),
        new(EffectKind.Blur, "Blur",
            "A box blur with a radius and extra passes.",
            new EffectParameter[]
            {
                new("radius", "Radius", 0, 30, 6, 0.5, "0.#"),
                new("power", "Passes", 1, 3, 1, 1, "0", EffectParameterKind.Choice,
                    new[] { new EffectChoice(1, "1 pass"), new EffectChoice(2, "2 passes"), new EffectChoice(3, "3 passes") })
            },
            new[]
            {
                Preset("Soft", ("radius", 4), ("power", 1)),
                Preset("Dreamy", ("radius", 12), ("power", 2)),
                Preset("Heavy", ("radius", 24), ("power", 3))
            }),
        new(EffectKind.GaussianBlur, "Gaussian Blur",
            "A smooth gaussian blur for a natural defocus.",
            new EffectParameter[]
            {
                new("sigma", "Sigma", 0, 30, 4, 0.1, "0.0")
            },
            new[]
            {
                Preset("Slight", ("sigma", 2)),
                Preset("Soft focus", ("sigma", 8)),
                Preset("Heavy", ("sigma", 20))
            }),
        new(EffectKind.Sharpen, "Sharpen",
            "Crisp up edges with an unsharp mask.",
            new EffectParameter[]
            {
                new("amount", "Amount", 0, 2, 0.8, 0.01, "0.00"),
                new("size", "Matrix", 3, 9, 5, 2, "0", EffectParameterKind.Choice,
                    new[]
                    {
                        new EffectChoice(3, "3 × 3"), new EffectChoice(5, "5 × 5"),
                        new EffectChoice(7, "7 × 7"), new EffectChoice(9, "9 × 9")
                    })
            },
            new[]
            {
                Preset("Crisp", ("amount", 0.6), ("size", 3)),
                Preset("Strong", ("amount", 1.2), ("size", 5)),
                Preset("Extreme", ("amount", 1.8), ("size", 7))
            }),
        new(EffectKind.FilmGrain, "Film Grain",
            "Add analog grain. Moving grain changes every frame.",
            new EffectParameter[]
            {
                new("amount", "Amount", 0, 100, 18, 1, "0"),
                new("type", "Type", 0, 2, 1, 1, "0", EffectParameterKind.Choice,
                    new[]
                    {
                        new EffectChoice(0, "Fine"), new EffectChoice(1, "Moving"),
                        new EffectChoice(2, "Pattern")
                    })
            },
            new[]
            {
                Preset("Fine", ("amount", 10), ("type", 0)),
                Preset("35 mm", ("amount", 22), ("type", 1)),
                Preset("VHS", ("amount", 38), ("type", 2)),
                Preset("Heavy", ("amount", 65), ("type", 1))
            }),
        new(EffectKind.Glow, "Glow",
            "Bloom the bright parts of the image.",
            new EffectParameter[]
            {
                new("radius", "Radius", 0, 40, 12, 0.5, "0.#"),
                new("threshold", "Threshold", 0, 0.95, 0.55, 0.01, "0.00"),
                new("intensity", "Intensity", 0, 1, 0.55, 0.01, "0.00")
            },
            new[]
            {
                Preset("Subtle", ("radius", 8), ("threshold", 0.72), ("intensity", 0.35)),
                Preset("Bloom", ("radius", 16), ("threshold", 0.55), ("intensity", 0.62)),
                Preset("Neon", ("radius", 26), ("threshold", 0.32), ("intensity", 0.88))
            }),
        new(EffectKind.ChromaKey, "Chroma Keyer",
            "Key out a green or blue screen so lower layers show through.",
            new EffectParameter[]
            {
                new("color", "Screen", 0, 1, 0, 1, "0", EffectParameterKind.Choice,
                    new[] { new EffectChoice(0, "Green"), new EffectChoice(1, "Blue") }),
                new("similarity", "Similarity", 0, 1, 0.12, 0.01, "0.00"),
                new("blend", "Softness", 0, 1, 0.15, 0.01, "0.00")
            },
            new[]
            {
                Preset("Green screen", ("color", 0), ("similarity", 0.12), ("blend", 0.15)),
                Preset("Blue screen", ("color", 1), ("similarity", 0.12), ("blend", 0.15)),
                Preset("Tight key", ("color", 0), ("similarity", 0.05), ("blend", 0.05)),
                Preset("Loose key", ("color", 0), ("similarity", 0.3), ("blend", 0.25)),
                Preset("Soft edges", ("color", 0), ("similarity", 0.15), ("blend", 0.45))
            }),
        new(EffectKind.Crop, "Crop",
            "Keep a part of the frame: halves, quarters or a custom box. " +
            "It writes the clip's Crop/Pan settings.",
            new EffectParameter[]
            {
                new("x", "Left", 0, 0.95, 0, 0.01, "0.00"),
                new("y", "Top", 0, 0.95, 0, 0.01, "0.00"),
                new("width", "Width", 0.05, 1, 1, 0.01, "0.00"),
                new("height", "Height", 0.05, 1, 1, 0.01, "0.00")
            },
            new[]
            {
                Preset("Full frame", ("x", 0), ("y", 0), ("width", 1), ("height", 1)),
                Preset("Left half", ("x", 0), ("y", 0), ("width", 0.5), ("height", 1)),
                Preset("Right half", ("x", 0.5), ("y", 0), ("width", 0.5), ("height", 1)),
                Preset("Top half", ("x", 0), ("y", 0), ("width", 1), ("height", 0.5)),
                Preset("Bottom half", ("x", 0), ("y", 0.5), ("width", 1), ("height", 0.5)),
                Preset("Top left", ("x", 0), ("y", 0), ("width", 0.5), ("height", 0.5)),
                Preset("Top right", ("x", 0.5), ("y", 0), ("width", 0.5), ("height", 0.5)),
                Preset("Bottom left", ("x", 0), ("y", 0.5), ("width", 0.5), ("height", 0.5)),
                Preset("Bottom right", ("x", 0.5), ("y", 0.5), ("width", 0.5), ("height", 0.5))
            }),
        new(EffectKind.Vignette, "Vignette",
            "Darken the corners for an old-film or vintage look.",
            new EffectParameter[]
            {
                new("amount", "Amount", 0, 1, 0.55, 0.01, "0.00")
            },
            new[]
            {
                Preset("Subtle", ("amount", 0.3)),
                Preset("Classic", ("amount", 0.55)),
                Preset("Old film", ("amount", 0.78)),
                Preset("Heavy", ("amount", 0.95))
            })
    };

    private static EffectPreset Preset(string name, params (string Key, double Value)[] values) =>
        new(name, values.ToDictionary(v => v.Key, v => v.Value));

    public static IReadOnlyList<EffectDefinition> All => Definitions;

    public static EffectDefinition Get(EffectKind kind) =>
        Definitions.First(d => d.Kind == kind);

    public static ClipEffect Create(EffectKind kind) =>
        TryCreate(kind) ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown effect.");

    /// <summary>Creates an effect with defaults, or null for an unknown kind.</summary>
    public static ClipEffect? TryCreate(EffectKind kind)
    {
        var definition = Definitions.FirstOrDefault(d => d.Kind == kind);
        if (definition == null) return null;

        var effect = new ClipEffect { Kind = kind };
        foreach (var parameter in definition.Parameters)
            effect.Values[parameter.Key] = parameter.Default;

        // Start from the first preset so the inspector opens on a known look.
        if (definition.Presets.Count > 0)
        {
            foreach (var pair in definition.Presets[0].Values)
                effect.Values[pair.Key] = pair.Value;
        }

        return effect;
    }

    /// <summary>Filter chain for one simple effect; empty when it is a no-op.</summary>
    public static string SimpleFilter(ClipEffect effect)
    {
        switch (effect.Kind)
        {
            case EffectKind.ColorBalance:
            {
                var red = effect.Get("red", 1);
                var green = effect.Get("green", 1);
                var blue = effect.Get("blue", 1);
                var saturation = effect.Get("saturation", 1);
                var brightness = effect.Get("brightness", 0);
                var contrast = effect.Get("contrast", 1);

                if (Near(red, 1) && Near(green, 1) && Near(blue, 1) &&
                    Near(saturation, 1) && Near(brightness, 0) && Near(contrast, 1))
                {
                    return "";
                }

                return $"colorbalance=rm={F(red - 1)}:gm={F(green - 1)}:bm={F(blue - 1)}," +
                       $"eq=brightness={F(brightness)}:contrast={F(contrast)}:saturation={F(saturation)}";
            }
            case EffectKind.BrightnessContrast:
            {
                var brightness = effect.Get("brightness", 0);
                var contrast = effect.Get("contrast", 1);
                if (Near(brightness, 0) && Near(contrast, 1)) return "";

                return $"eq=brightness={F(brightness)}:contrast={F(contrast)}";
            }
            case EffectKind.ChromaKey:
            {
                var blue = (int)Math.Round(effect.Get("color", 0)) == 1;
                var similarity = Math.Clamp(effect.Get("similarity", 0.12), 0, 1);
                var blend = Math.Clamp(effect.Get("blend", 0.15), 0, 1);
                return $"chromakey=color={(blue ? "blue" : "green")}:" +
                       $"similarity={F(similarity)}:blend={F(blend)}";
            }
            case EffectKind.Vignette:
            {
                var amount = Math.Clamp(effect.Get("amount", 0.55), 0, 1);
                if (amount < 0.01) return "";

                var angle = 0.1 + amount * 1.35;
                return $"vignette=angle={F(angle)}";
            }
            case EffectKind.Crop:
                // Crop writes the clip's Crop/Pan fields instead of a filter.
                return "";
            case EffectKind.Blur:
            {
                var radius = effect.Get("radius", 6);
                if (radius < 0.5) return "";
                var power = Math.Clamp((int)Math.Round(effect.Get("power", 1)), 1, 3);
                return $"boxblur=luma_radius={F(radius)}:luma_power={power}:" +
                       $"chroma_radius={F(radius)}:chroma_power={power}";
            }
            case EffectKind.GaussianBlur:
            {
                var sigma = effect.Get("sigma", 4);
                return sigma < 0.1 ? "" : $"gblur=sigma={F(sigma)}";
            }
            case EffectKind.Sharpen:
            {
                var amount = effect.Get("amount", 0.8);
                if (amount < 0.01) return "";
                var size = Math.Clamp((int)Math.Round(effect.Get("size", 5)), 3, 9);
                return $"unsharp=luma_msize_x={size}:luma_msize_y={size}:luma_amount={F(amount)}";
            }
            case EffectKind.FilmGrain:
            {
                var amount = effect.Get("amount", 18);
                if (amount < 0.1) return "";
                var type = (int)Math.Round(effect.Get("type", 1));
                var flags = type switch { 0 => "u", 1 => "t+u", _ => "u+p" };
                return $"noise=alls={(int)Math.Round(amount)}:allf={flags}";
            }
            default:
                return "";
        }
    }

    /// <summary>
    /// The complete mpv <c>vf</c> chain for a clip. Glow becomes a lavfi graph
    /// because it needs to split and blend the frame.
    /// </summary>
    public static string PreviewStack(IEnumerable<ClipEffect> effects)
    {
        var parts = new List<string>();
        var glowIndex = 0;

        foreach (var effect in effects)
        {
            if (!effect.Enabled) continue;

            if (effect.Kind == EffectKind.Glow)
            {
                if (effect.Get("intensity", 0.55) < 0.01) continue;
                parts.Add($"lavfi=[{GlowGraph(effect, $"g{glowIndex++}")}]");
            }
            else if (effect.Kind == EffectKind.ChromaKey)
            {
                // The compositor shader keys this layer; see PreviewCompositor.
                continue;
            }
            else
            {
                var filter = SimpleFilter(effect);
                if (filter.Length > 0) parts.Add(filter);
            }
        }

        return string.Join(",", parts);
    }

    public static string GlowGraph(ClipEffect effect, string prefix)
    {
        var (blur, blend) = GlowParts(effect, prefix);
        return $"format=gbrp,split=2[{prefix}a][{prefix}b];{blur};{blend}";
    }

    /// <summary>Split the glow graph so the export can inline it in a filter_complex.</summary>
    public static (string Blur, string Blend) GlowParts(ClipEffect effect, string prefix)
    {
        var radius = Math.Max(0.1, effect.Get("radius", 12));
        var threshold = Math.Clamp(effect.Get("threshold", 0.55), 0.01, 0.95);
        var intensity = Math.Clamp(effect.Get("intensity", 0.55), 0, 1);
        var upper = Math.Min(0.995, threshold + 0.15);
        var curve = $"curves=all='0/0 {F(threshold)}/0 {F(upper)}/1 1/1'";

        var blur = $"[{prefix}b]gblur=sigma={F(radius)},{curve}[{prefix}c]";
        var blend = $"[{prefix}a][{prefix}c]blend=all_mode=screen:all_opacity={F(intensity)}";
        return (blur, blend);
    }

    private static bool Near(double value, double target) => Math.Abs(value - target) < 0.0005;

    private static string F(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);
}
