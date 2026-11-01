using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using YorteVegas.Models;
using YorteVegas.ViewModels;

namespace YorteVegas.Services;

/// <summary>
/// Reads/writes <c>.yveg</c> project files. Media files are referenced by path
/// (relative when they live inside the project folder) and never embedded.
/// The same DTO doubles as the undo snapshot so round-trips are lossless.
/// </summary>
public static class ProjectService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public const string Extension = ".yveg";
    public const string FileFilter = "YorteVegas project (*.yveg)|*.yveg|All files (*.*)|*.*";

    public static ProjectFile Capture(EditProject project, string? projectDirectory = null)
    {
        var file = new ProjectFile
        {
            Name = project.Name,
            Width = project.Width,
            Height = project.Height,
            Fps = project.Fps,
            Playhead = project.Playhead,
            SelectedClipId = project.SelectedClipId,
            SelectedClipIds = project.SelectedClipIds.ToList(),
            RangeStart = project.RangeStart,
            RangeEnd = project.RangeEnd
        };

        foreach (var asset in project.Assets)
        {
            file.Assets.Add(new AssetDto
            {
                Id = asset.Id,
                Name = asset.Name,
                Path = projectDirectory == null
                    ? asset.FilePath
                    : StorePath(asset.FilePath, projectDirectory),
                Kind = asset.Kind,
                Duration = asset.DurationSeconds,
                Width = asset.Width,
                Height = asset.Height,
                HasAudio = asset.HasAudio
            });
        }

        foreach (var track in project.Tracks)
        {
            var trackDto = new TrackDto
            {
                Id = track.Id,
                Name = track.Name,
                Kind = track.Kind,
                Muted = track.IsMuted,
                Locked = track.IsLocked,
                Solo = track.IsSolo
            };
            foreach (var clip in track.Clips)
            {
                trackDto.Clips.Add(new ClipDto
                {
                    Id = clip.Id,
                    AssetId = clip.AssetId,
                    Name = clip.Name,
                    Kind = clip.Kind,
                    Start = clip.StartSeconds,
                    Duration = clip.DurationSeconds,
                    In = clip.InSeconds,
                    FadeCurve = clip.FadeCurve,
                    Opacity = clip.Opacity,
                    Volume = clip.Volume,
                    PlaybackRate = clip.PlaybackRate,
                    FadeIn = clip.FadeInSeconds,
                    FadeOut = clip.FadeOutSeconds,
                    CropLeft = clip.CropLeft,
                    CropRight = clip.CropRight,
                    CropTop = clip.CropTop,
                    CropBottom = clip.CropBottom,
                    PanX = clip.PanX,
                    PanY = clip.PanY,
                    Scale = clip.Scale,
                    PositionX = clip.PositionX,
                    PositionY = clip.PositionY,
                    Effects = clip.Effects.Select(e => new EffectDto
                    {
                        Kind = e.Kind,
                        Enabled = e.Enabled,
                        Values = new Dictionary<string, double>(e.Values)
                    }).ToList(),
                    TransitionIn = CaptureTransition(clip.TransitionIn),
                    TransitionOut = CaptureTransition(clip.TransitionOut)
                });
            }
            file.Tracks.Add(trackDto);
        }

        return file;
    }

    public static void Apply(ProjectFile file, EditProject project, string? projectDirectory = null)
    {
        project.Name = string.IsNullOrWhiteSpace(file.Name) ? "Untitled" : file.Name;
        project.Width = file.Width > 0 ? file.Width : 1920;
        project.Height = file.Height > 0 ? file.Height : 1080;
        project.Fps = file.Fps > 0 ? file.Fps : 30;

        project.Assets.Clear();
        foreach (var dto in file.Assets)
        {
            var path = projectDirectory == null ? dto.Path : ResolvePath(dto.Path, projectDirectory);
            project.Assets.Add(new MediaAsset
            {
                Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id,
                Name = string.IsNullOrEmpty(dto.Name) ? Path.GetFileName(path) : dto.Name,
                FilePath = path,
                Kind = dto.Kind,
                DurationSeconds = dto.Duration,
                Width = dto.Width,
                Height = dto.Height,
                HasAudio = dto.HasAudio,
                IsMissing = !string.IsNullOrEmpty(path) && !File.Exists(path)
            });
        }

        project.Tracks.Clear();
        foreach (var dto in file.Tracks)
        {
            var track = new TimelineTrack
            {
                Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id,
                Name = dto.Name,
                Kind = dto.Kind,
                IsMuted = dto.Muted,
                IsLocked = dto.Locked,
                IsSolo = dto.Solo
            };
            foreach (var c in dto.Clips)
            {
                track.Clips.Add(new TimelineClip
                {
                    Id = c.Id == Guid.Empty ? Guid.NewGuid() : c.Id,
                    AssetId = c.AssetId,
                    Name = c.Name,
                    Kind = c.Kind,
                    StartSeconds = Math.Max(0, c.Start),
                    DurationSeconds = Math.Max(1.0 / 120, c.Duration),
                    InSeconds = Math.Max(0, c.In),
                    FadeCurve = c.FadeCurve,
                    Opacity = Math.Clamp(c.Opacity ?? 1, 0, 1),
                    Volume = Math.Clamp(c.Volume ?? 1, 0, 1),
                    PlaybackRate = Math.Clamp(c.PlaybackRate ?? 1, 0.1, 10),
                    FadeInSeconds = Math.Clamp(c.FadeIn ?? 0, 0, 30),
                    FadeOutSeconds = Math.Clamp(c.FadeOut ?? 0, 0, 30),
                    CropLeft = Math.Clamp(c.CropLeft ?? 0, 0, 0.95),
                    CropRight = Math.Clamp(c.CropRight ?? 0, 0, 0.95),
                    CropTop = Math.Clamp(c.CropTop ?? 0, 0, 0.95),
                    CropBottom = Math.Clamp(c.CropBottom ?? 0, 0, 0.95),
                    PanX = Math.Clamp(c.PanX ?? 0, -1, 1),
                    PanY = Math.Clamp(c.PanY ?? 0, -1, 1),
                    Scale = Math.Clamp(c.Scale ?? 1, 0.05, 4),
                    PositionX = Math.Clamp(c.PositionX ?? 0, -1, 1),
                    PositionY = Math.Clamp(c.PositionY ?? 0, -1, 1),
                    Effects = (c.Effects ?? new List<EffectDto>()).Select(CreateEffect)
                        .Where(e => e != null)
                        .Select(e => e!)
                        .ToList(),
                    TransitionIn = CreateTransition(c.TransitionIn),
                    TransitionOut = CreateTransition(c.TransitionOut)
                });
            }
            project.Tracks.Add(track);
        }

        project.Playhead = Math.Max(0, file.Playhead);

        if (file.RangeStart is { } rangeStart && file.RangeEnd is { } rangeEnd &&
            rangeEnd - rangeStart > 0.001)
        {
            project.SetRange(rangeStart, rangeEnd);
        }
        else
        {
            project.ClearRange();
        }

        var savedSelection = file.SelectedClipIds ?? new List<Guid>();
        var selected = savedSelection.Count > 0
            ? savedSelection
            : file.SelectedClipId is { } single ? new List<Guid> { single } : new List<Guid>();
        var primary = file.SelectedClipId ?? (selected.Count > 0 ? selected[^1] : (Guid?)null);
        project.RestoreSelection(selected, primary);

        project.NotifyReloaded();
    }

    /// <summary>Rebuilds a stored effect, merging the catalog defaults.</summary>
    private static ClipEffect? CreateEffect(EffectDto dto)
    {
        var effect = EffectCatalog.TryCreate(dto.Kind);
        if (effect == null) return null;

        effect.Enabled = dto.Enabled;
        foreach (var pair in dto.Values ?? new Dictionary<string, double>())
            effect.Values[pair.Key] = pair.Value;
        return effect;
    }

    private static TransitionDto? CaptureTransition(ClipTransition? transition) =>
        transition == null
            ? null
            : new TransitionDto
            {
                Kind = transition.Kind,
                Enabled = transition.Enabled,
                Values = new Dictionary<string, double>(transition.Values)
            };

    /// <summary>Rebuilds a stored transition, merging the catalog defaults.</summary>
    private static ClipTransition? CreateTransition(TransitionDto? dto)
    {
        if (dto == null) return null;

        var transition = TransitionCatalog.TryCreate(dto.Kind);
        if (transition == null) return null;

        transition.Enabled = dto.Enabled;
        foreach (var pair in dto.Values ?? new Dictionary<string, double>())
            transition.Values[pair.Key] = pair.Value;
        return transition;
    }

    /// <param name="relativeTo">
    /// Folder used to store media paths relative to. Defaults to the folder of
    /// <paramref name="path"/>; autosave passes the real project folder so the
    /// recovered file resolves the same media.
    /// </param>
    public static void Save(EditProject project, string path, string? relativeTo = null)
    {
        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(targetDirectory)) Directory.CreateDirectory(targetDirectory);

        // relativeTo only decides how media paths are stored; it must not be
        // created (an autosave can outlive its project folder).
        var directory = string.IsNullOrEmpty(relativeTo) ? targetDirectory : relativeTo;

        var dto = Capture(project, directory);
        var json = JsonSerializer.Serialize(dto, JsonOptions);

        // Keep the previous version once per save, then write-then-replace so a
        // failed save never destroys the current file.
        if (File.Exists(path))
        {
            try { File.Copy(path, path + ".bak", true); }
            catch { /* a locked backup must not block the save */ }
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, true);
    }

    public static ProjectFile Read(string path)
    {
        var json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<ProjectFile>(json, JsonOptions);
        if (dto == null) throw new InvalidDataException("The project file is empty or invalid.");
        if (dto.Version > 1)
            throw new InvalidDataException(
                "This project was created by a newer version of YorteVegas.");
        return dto;
    }

    public static void LoadInto(string path, EditProject project)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var dto = Read(path);
        Apply(dto, project, directory);
    }

    public static ProjectFile CreateDefault(string name, int width, int height, double fps) => new()
    {
        Name = name,
        Width = width,
        Height = height,
        Fps = fps,
        Tracks =
        {
            new TrackDto { Name = "Video 1", Kind = TrackKind.Video },
            new TrackDto { Name = "Audio 1", Kind = TrackKind.Audio }
        }
    };

    private static string StorePath(string fullPath, string projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return fullPath;
        try
        {
            var relative = Path.GetRelativePath(projectDirectory, fullPath);
            if (!Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal))
                return relative;
        }
        catch
        {
            // Fall back to the absolute path.
        }
        return fullPath;
    }

    private static string ResolvePath(string path, string projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        try
        {
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(projectDirectory, path));
        }
        catch
        {
            return path;
        }
    }
}

public sealed class ProjectFile
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Untitled";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public double Fps { get; set; } = 30;
    public double Playhead { get; set; }
    public Guid? SelectedClipId { get; set; }

    /// <summary>Export range; null means the whole timeline.</summary>
    public double? RangeStart { get; set; }
    public double? RangeEnd { get; set; }

    /// <summary>Multi-selection; older projects only carry <see cref="SelectedClipId"/>.</summary>
    public List<Guid> SelectedClipIds { get; set; } = new();
    public List<AssetDto> Assets { get; set; } = new();
    public List<TrackDto> Tracks { get; set; } = new();
}

public sealed class AssetDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public MediaKind Kind { get; set; }
    public double Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool HasAudio { get; set; }
}

public sealed class TrackDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Track";
    public TrackKind Kind { get; set; }
    public bool Muted { get; set; }
    public bool Locked { get; set; }
    public bool Solo { get; set; }
    public List<ClipDto> Clips { get; set; } = new();
}

public sealed class ClipDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssetId { get; set; }
    public string Name { get; set; } = "";
    public MediaKind Kind { get; set; }
    public double Start { get; set; }
    public double Duration { get; set; }
    public double In { get; set; }
    public FadeCurveKind FadeCurve { get; set; } = FadeCurveKind.Linear;
    public double? Opacity { get; set; } = 1;
    public double? Volume { get; set; } = 1;
    public double? FadeIn { get; set; }
    public double? FadeOut { get; set; }
    public double? CropLeft { get; set; }
    public double? CropRight { get; set; }
    public double? CropTop { get; set; }
    public double? CropBottom { get; set; }
    public double? PanX { get; set; }
    public double? PanY { get; set; }
    public double? Scale { get; set; } = 1;
    public double? PositionX { get; set; }
    public double? PositionY { get; set; }
    public double? PlaybackRate { get; set; } = 1;
    public List<EffectDto> Effects { get; set; } = new();
    public TransitionDto? TransitionIn { get; set; }
    public TransitionDto? TransitionOut { get; set; }
}

/// <summary>One stored effect. Values are keyed so new parameters can be added.</summary>
public sealed class EffectDto
{
    public EffectKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    public Dictionary<string, double> Values { get; set; } = new();
}

/// <summary>One stored transition on a clip edge.</summary>
public sealed class TransitionDto
{
    public TransitionKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    public Dictionary<string, double> Values { get; set; } = new();
}
