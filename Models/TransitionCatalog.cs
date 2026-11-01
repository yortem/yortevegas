namespace YorteVegas.Models;

public sealed record TransitionDefinition(
    TransitionKind Kind,
    string Name,
    string Description,
    IReadOnlyList<EffectParameter> Parameters,
    IReadOnlyList<EffectPreset> Presets);

/// <summary>
/// The built-in transitions, their parameters, presets and the small amount of
/// geometry the preview and export share. A transition is always drawn across
/// the overlap of two clips, or across the first/last seconds of one clip.
/// </summary>
public static class TransitionCatalog
{
    private static readonly EffectChoice[] Directions =
    {
        new(0, "Left"),
        new(1, "Right"),
        new(2, "Up"),
        new(3, "Down")
    };

    private static readonly TransitionDefinition[] Definitions =
    {
        new(TransitionKind.Dissolve, "Dissolve",
            "A clean crossfade: the outgoing clip melts into the incoming one.",
            new EffectParameter[]
            {
                new("duration", "Duration", 0.1, 5, 0.6, 0.05, "0.00")
            },
            new[]
            {
                Preset("Quick", ("duration", 0.3)),
                Preset("Standard", ("duration", 0.6)),
                Preset("Slow", ("duration", 1.2))
            }),
        new(TransitionKind.Flash, "Flash",
            "A white flash at the midpoint, like a camera flash or a light hit.",
            new EffectParameter[]
            {
                new("duration", "Duration", 0.1, 3, 0.35, 0.05, "0.00"),
                new("strength", "Strength", 0, 1, 0.9, 0.01, "0.00")
            },
            new[]
            {
                Preset("Quick", ("duration", 0.22), ("strength", 1)),
                Preset("Soft", ("duration", 0.45), ("strength", 0.62)),
                Preset("Slow", ("duration", 0.9), ("strength", 0.42))
            }),
        new(TransitionKind.Cross, "Cross Effect",
            "Both pictures slide diagonally across each other for a punchy hand-off.",
            new EffectParameter[]
            {
                new("duration", "Duration", 0.1, 5, 0.6, 0.05, "0.00")
            },
            new[]
            {
                Preset("Quick", ("duration", 0.35)),
                Preset("Standard", ("duration", 0.6)),
                Preset("Slow", ("duration", 1.1))
            }),
        new(TransitionKind.Slide, "Slide",
            "The incoming clip slides in over the outgoing one.",
            new EffectParameter[]
            {
                new("duration", "Duration", 0.1, 5, 0.6, 0.05, "0.00"),
                new("direction", "Direction", 0, 3, 0, 1, "0",
                    EffectParameterKind.Choice, Directions)
            },
            new[]
            {
                Preset("From right", ("duration", 0.55), ("direction", 0)),
                Preset("From left", ("duration", 0.55), ("direction", 1)),
                Preset("From bottom", ("duration", 0.55), ("direction", 2)),
                Preset("Slow", ("duration", 1.1), ("direction", 0))
            }),
        new(TransitionKind.Push, "Push",
            "The incoming clip pushes the outgoing clip off the frame.",
            new EffectParameter[]
            {
                new("duration", "Duration", 0.1, 5, 0.6, 0.05, "0.00"),
                new("direction", "Direction", 0, 3, 0, 1, "0",
                    EffectParameterKind.Choice, Directions)
            },
            new[]
            {
                Preset("Push left", ("duration", 0.55), ("direction", 0)),
                Preset("Push right", ("duration", 0.55), ("direction", 1)),
                Preset("Push up", ("duration", 0.55), ("direction", 2)),
                Preset("Slow", ("duration", 1.1), ("direction", 0))
            }),
        new(TransitionKind.Wipe, "Wipe",
            "A soft-edged line travels across the frame and reveals the next clip.",
            new EffectParameter[]
            {
                new("duration", "Duration", 0.1, 5, 0.6, 0.05, "0.00"),
                new("direction", "Direction", 0, 3, 1, 1, "0",
                    EffectParameterKind.Choice, Directions),
                new("feather", "Soft edge", 0, 0.4, 0.06, 0.01, "0.00")
            },
            new[]
            {
                Preset("Hard", ("duration", 0.45), ("direction", 1), ("feather", 0.01)),
                Preset("Soft", ("duration", 0.65), ("direction", 1), ("feather", 0.12)),
                Preset("Slow", ("duration", 1.2), ("direction", 1), ("feather", 0.2))
            }),
        new(TransitionKind.Iris, "Iris",
            "A circle opens from the centre of the frame onto the next clip.",
            new EffectParameter[]
            {
                new("duration", "Duration", 0.1, 5, 0.7, 0.05, "0.00"),
                new("feather", "Soft edge", 0, 0.4, 0.08, 0.01, "0.00")
            },
            new[]
            {
                Preset("Quick", ("duration", 0.35), ("feather", 0.02)),
                Preset("Soft", ("duration", 0.7), ("feather", 0.12)),
                Preset("Slow", ("duration", 1.2), ("feather", 0.2))
            })
    };

    private static EffectPreset Preset(string name, params (string Key, double Value)[] values) =>
        new(name, values.ToDictionary(v => v.Key, v => v.Value));

    public static IReadOnlyList<TransitionDefinition> All => Definitions;

    public static TransitionDefinition Get(TransitionKind kind) =>
        Definitions.First(d => d.Kind == kind);

    public static ClipTransition Create(TransitionKind kind) =>
        TryCreate(kind) ?? throw new ArgumentOutOfRangeException(nameof(kind), kind,
            "Unknown transition.");

    public static ClipTransition? TryCreate(TransitionKind kind)
    {
        var definition = Definitions.FirstOrDefault(d => d.Kind == kind);
        if (definition == null) return null;

        var transition = new ClipTransition { Kind = kind };
        foreach (var parameter in definition.Parameters)
            transition.Values[parameter.Key] = parameter.Default;

        if (definition.Presets.Count > 0)
        {
            foreach (var pair in definition.Presets[0].Values)
                transition.Values[pair.Key] = pair.Value;
        }

        return transition;
    }

    public static double Duration(ClipTransition? transition, double fallback = 0.6) =>
        transition == null ? fallback : Math.Clamp(transition.Get("duration", fallback), 0.05, 10);

    public static int Direction(ClipTransition transition) =>
        Math.Clamp((int)Math.Round(transition.Get("direction", 0)), 0, 3);

    /// <summary>Vector of the motion in frame fractions (left is -X).</summary>
    public static (double X, double Y) DirectionVector(int direction) => direction switch
    {
        0 => (-1, 0),
        1 => (1, 0),
        2 => (0, -1),
        _ => (0, 1)
    };

    /// <summary>Reveal mode understood by the compositor shader.</summary>
    public static int RevealMode(TransitionKind kind, int direction)
    {
        if (kind == TransitionKind.Iris) return 5;
        if (kind != TransitionKind.Wipe) return 0;

        return direction switch
        {
            0 => 2, // travels left, reveals from the right
            1 => 1, // travels right, reveals from the left
            2 => 4, // travels up, reveals from the bottom
            _ => 3  // travels down, reveals from the top
        };
    }

    public static double Ease(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }
}
