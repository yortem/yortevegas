namespace YorteVegas.Models;

public enum TextAnimation
{
    None,
    Fade,
    SlideUp,
    SlideLeft,
    Zoom,
    Wipe
}

public enum TextAlign
{
    Left,
    Center,
    Right
}

public enum TextDirection
{
    Auto,
    Ltr,
    Rtl
}

/// <summary>
/// A text event on the timeline. Font size and spacing are expressed for a
/// 1080p frame; the renderer scales them for other project sizes.
/// </summary>
public sealed class TextOverlay
{
    public string Text { get; set; } = "Your text";
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 64;
    public int Weight { get; set; } = 600;
    public bool Italic { get; set; }
    public string Color { get; set; } = "#FFFFFF";
    public TextAlign Align { get; set; } = TextAlign.Center;
    public TextDirection Direction { get; set; } = TextDirection.Auto;
    public double PositionX { get; set; } = 0.5;
    public double PositionY { get; set; } = 0.82;
    public double MaxWidth { get; set; } = 0.8;

    public bool Shadow { get; set; } = true;
    public double ShadowBlur { get; set; } = 8;
    public double ShadowOpacity { get; set; } = 0.65;
    public double ShadowDepth { get; set; } = 3;

    public bool Outline { get; set; }
    public double OutlineThickness { get; set; } = 2;
    public string OutlineColor { get; set; } = "#000000";

    public bool Background { get; set; }
    public string BackgroundColor { get; set; } = "#000000";
    public double BackgroundOpacity { get; set; } = 0.55;
    public double BackgroundPadding { get; set; } = 16;

    public TextAnimation Animation { get; set; } = TextAnimation.Fade;
    public double AnimationSeconds { get; set; } = 0.35;

    /// <summary>Name of the last preset applied, used to highlight the tile.</summary>
    public string Preset { get; set; } = "Subtitle";

    public TextOverlay Clone() => (TextOverlay)MemberwiseClone();

    public string DisplayName
    {
        get
        {
            var line = Text?.Trim().Split('\n').FirstOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(line)) return "Text";
            return line.Length <= 42 ? line : line[..41] + "…";
        }
    }
}

/// <summary>Payload for dragging a text preset from the panel onto the timeline.</summary>
public sealed record TextDropData(string Preset, string Text);
