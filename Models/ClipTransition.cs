namespace YorteVegas.Models;

/// <summary>
/// The transition styles available between two clips, or at the open end of a
/// single clip. A transition lives on a clip edge, not on a pair: the incoming
/// clip carries the transition-in and the outgoing clip carries the
/// transition-out. When the clips overlap, both sides use the same transition.
/// </summary>
public enum TransitionKind
{
    Dissolve,
    Flash,
    Cross,
    Slide,
    Push,
    Wipe,
    Iris
}

/// <summary>
/// One transition attached to a clip edge. Values are stored by key so the
/// catalog can add parameters without changing the project schema.
/// </summary>
public sealed class ClipTransition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public TransitionKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    public Dictionary<string, double> Values { get; set; } = new();

    public double Get(string key, double fallback) =>
        Values.TryGetValue(key, out var value) ? value : fallback;

    public void Set(string key, double value) => Values[key] = value;

    public ClipTransition Clone() => new()
    {
        Id = Guid.NewGuid(),
        Kind = Kind,
        Enabled = Enabled,
        Values = new Dictionary<string, double>(Values)
    };
}

/// <summary>Payload for dragging a transition preset from the panel onto a clip.</summary>
public sealed record TransitionDropData(
    TransitionKind Kind,
    Dictionary<string, double> Values);
