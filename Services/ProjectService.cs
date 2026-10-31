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
            SelectedClipId = project.SelectedClipId
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
                    PlaybackRate = clip.PlaybackRate
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
                    PlaybackRate = Math.Clamp(c.PlaybackRate ?? 1, 0.1, 10)
                });
            }
            project.Tracks.Add(track);
        }

        project.Playhead = Math.Max(0, file.Playhead);
        project.RestoreSelection(file.SelectedClipId);
        project.NotifyReloaded();
    }

    public static void Save(EditProject project, string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var dto = Capture(project, directory);
        var json = JsonSerializer.Serialize(dto, JsonOptions);

        // Write-then-replace so a failed save never destroys the previous file.
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
    public double? PlaybackRate { get; set; } = 1;
}
