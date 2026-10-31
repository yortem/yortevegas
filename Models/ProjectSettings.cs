namespace YorteVegas.Models;

/// <summary>Resolution / frame-rate chosen when a project is created.</summary>
public sealed class ProjectSettings
{
    public string Name { get; set; } = "Untitled";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public double Fps { get; set; } = 30;

    public string ResolutionLabel => $"{Width} × {Height}";
    public string FpsLabel => Fps % 1 == 0 ? $"{Fps:0} fps" : $"{Fps:0.###} fps";
}
