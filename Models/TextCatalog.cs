namespace YorteVegas.Models;

public sealed record TextPreset(string Name, string Description, TextOverlay Style);

/// <summary>
/// The built-in text designs. Every preset fills the whole style so the panel
/// can start from a known look, and the user can then adjust any value.
/// </summary>
public static class TextCatalog
{
    private static readonly TextPreset[] Presets =
    {
        new("Subtitle", "Clean white subtitles near the bottom of the frame.",
            new TextOverlay
            {
                FontSize = 48, Weight = 600, Color = "#FFFFFF",
                Align = TextAlign.Center, PositionX = 0.5, PositionY = 0.86,
                MaxWidth = 0.8, Shadow = true, ShadowBlur = 7, ShadowOpacity = 0.7,
                ShadowDepth = 2, Outline = false, Background = false,
                Animation = TextAnimation.Fade, AnimationSeconds = 0.25
            }),
        new("Caption", "A crisp caption with a thin dark outline for busy footage.",
            new TextOverlay
            {
                FontSize = 42, Weight = 700, Color = "#FFFFFF",
                Align = TextAlign.Center, PositionX = 0.5, PositionY = 0.88,
                MaxWidth = 0.82, Shadow = false, Outline = true,
                OutlineThickness = 2.5, OutlineColor = "#000000", Background = false,
                Animation = TextAnimation.Fade, AnimationSeconds = 0.2
            }),
        new("Title", "A large centred title that lifts into place.",
            new TextOverlay
            {
                FontSize = 96, Weight = 700, Color = "#FFFFFF",
                Align = TextAlign.Center, PositionX = 0.5, PositionY = 0.5,
                MaxWidth = 0.8, Shadow = true, ShadowBlur = 12, ShadowOpacity = 0.6,
                ShadowDepth = 4, Outline = false, Background = false,
                Animation = TextAnimation.SlideUp, AnimationSeconds = 0.6
            }),
        new("Intro", "A bold opening card with a gentle zoom.",
            new TextOverlay
            {
                FontSize = 120, Weight = 900, Color = "#FFFFFF",
                Align = TextAlign.Center, PositionX = 0.5, PositionY = 0.5,
                MaxWidth = 0.9, Shadow = true, ShadowBlur = 14, ShadowOpacity = 0.55,
                ShadowDepth = 4, Outline = false, Background = false,
                Animation = TextAnimation.Zoom, AnimationSeconds = 0.8
            }),
        new("Credits", "Small centred credits that fade in and out.",
            new TextOverlay
            {
                FontSize = 44, Weight = 400, Color = "#FFFFFF",
                Align = TextAlign.Center, PositionX = 0.5, PositionY = 0.5,
                MaxWidth = 0.7, Shadow = false, Outline = false, Background = false,
                Animation = TextAnimation.Fade, AnimationSeconds = 1.0
            }),
        new("Lower third", "A left-aligned name plate with a quiet dark bar.",
            new TextOverlay
            {
                FontSize = 48, Weight = 600, Color = "#FFFFFF",
                Align = TextAlign.Left, PositionX = 0.18, PositionY = 0.72,
                MaxWidth = 0.45, Shadow = false, Outline = false,
                Background = true, BackgroundColor = "#000000",
                BackgroundOpacity = 0.55, BackgroundPadding = 16,
                Animation = TextAnimation.SlideLeft, AnimationSeconds = 0.4
            }),
        new("Minimal", "A light, unobtrusive line of text.",
            new TextOverlay
            {
                FontSize = 56, Weight = 300, Color = "#FFFFFF",
                Align = TextAlign.Center, PositionX = 0.5, PositionY = 0.5,
                MaxWidth = 0.8, Shadow = false, Outline = false, Background = false,
                Animation = TextAnimation.Fade, AnimationSeconds = 0.3
            }),
        new("Quote", "An italic serif quote with a soft shadow.",
            new TextOverlay
            {
                FontFamily = "Georgia", FontSize = 52, Weight = 400, Italic = true,
                Color = "#FFFFFF", Align = TextAlign.Center, PositionX = 0.5,
                PositionY = 0.5, MaxWidth = 0.7, Shadow = true, ShadowBlur = 10,
                ShadowOpacity = 0.5, ShadowDepth = 3, Outline = false,
                Background = false, Animation = TextAnimation.Fade,
                AnimationSeconds = 0.5
            })
    };

    public static IReadOnlyList<TextPreset> All => Presets;

    public static TextPreset Get(string name) =>
        Presets.FirstOrDefault(p => string.Equals(p.Name, name,
            StringComparison.OrdinalIgnoreCase)) ?? Presets[0];

    public static TextOverlay Create(string preset, string text)
    {
        var style = Get(preset).Style.Clone();
        style.Text = string.IsNullOrWhiteSpace(text) ? "Your text" : text;
        style.Preset = Get(preset).Name;
        return style;
    }

    public static TextOverlay CreateDefault(string text) => Create("Subtitle", text);

    public static IReadOnlyList<string> FontFamilies { get; } = new[]
    {
        "Segoe UI", "Arial", "Tahoma", "David", "Gisha", "Miriam",
        "FrankRuehl", "Narkisim", "Times New Roman", "Verdana",
        "Calibri", "Georgia", "Trebuchet MS", "Impact", "Consolas"
    };

    public static IReadOnlyList<string> Colors { get; } = new[]
    {
        "#FFFFFF", "#F2F2F2", "#FFD84D", "#FF6B6B", "#7FC4FF",
        "#7CF29B", "#FFB86B", "#FF9BD2", "#9A9A9A", "#000000"
    };
}
