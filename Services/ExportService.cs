using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using YorteVegas.Models;
using YorteVegas.ViewModels;

namespace YorteVegas.Services;

public enum ExportQuality
{
    Great,
    Good,
    Light
}

public sealed record ExportPreset(
    ExportQuality Quality,
    string Title,
    string Tagline,
    string Description,
    int Width,
    int Height,
    double Fps,
    int Crf,
    string AudioBitrate,
    string X264Preset);

public sealed record ExportProgress(string Stage, int Completed, int Total);

/// <summary>
/// Renders the timeline as the compositor shows it: every video track stacked
/// bottom to top with clip opacity and overlap fades, and every audible clip
/// mixed with its volume and fade. One FFmpeg filter graph does the whole
/// render, so the output matches the preview instead of concatenating cuts.
/// </summary>
public static class ExportService
{
    private sealed class ClipInput
    {
        public TimelineClip Clip = null!;
        public MediaAsset Asset = null!;
        public TimelineTrack Track = null!;
        public int InputIndex;
        public bool Visual;
        public bool HasAudio;
        public double Volume;
        public double Opacity = 1;
        public double FadeIn;          // overlap fade-in, seconds from the clip start
        public double FadeOutAt = -1;  // overlap fade-out, local seconds
        public double FadeOut;
        public double ManualIn;        // per-clip fade-in
        public double ManualOut;       // per-clip fade-out
        public double Start;
        public double Duration;
        public double SourceIn;
        public double Rate = 1;
    }

    public static ExportPreset CreatePreset(EditProject project, ExportQuality quality)
    {
        var longEdge = Math.Max(1, Math.Max(project.Width, project.Height));
        var cap = quality switch
        {
            ExportQuality.Great => longEdge,
            ExportQuality.Good => 1920,
            _ => 1280
        };
        var scale = Math.Min(1.0, cap / (double)longEdge);
        var width = MakeEven((int)Math.Round(project.Width * scale));
        var height = MakeEven((int)Math.Round(project.Height * scale));

        return quality switch
        {
            ExportQuality.Great => new ExportPreset(quality, "Great",
                "Best quality",
                "Keeps the full project resolution. Ideal for keeping or watching on a big screen.",
                width, height, ClampFps(project.Fps), 18, "192k", "slow"),
            ExportQuality.Good => new ExportPreset(quality, "Good",
                "Balanced",
                "Up to 1080p with a sensible file size for everyday sharing.",
                width, height, ClampFps(project.Fps), 22, "128k", "medium"),
            _ => new ExportPreset(quality, "Light for WhatsApp",
                "Made for sharing",
                "Up to 720p and a smaller file that sends easily in WhatsApp.",
                width, height, ClampFps(project.Fps), 28, "96k", "veryfast")
        };
    }

    public static string DefaultOutputPath(EditProject project)
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(videos))
            videos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(videos))
            videos = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        var folder = Path.Combine(videos, "YorteVegas");
        return Path.Combine(folder, SanitizeFileName(project.Name) + ".mp4");
    }

    public static string? LocateFfmpeg(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred) && File.Exists(preferred))
            return preferred;

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YorteVegas", "ffmpeg.exe")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVariable))
        {
            foreach (var directory in pathVariable.Split(Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    var path = Path.Combine(directory, "ffmpeg.exe");
                    if (File.Exists(path)) return path;
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return null;
    }

    public static async Task ExportAsync(
        EditProject project,
        ExportQuality quality,
        string outputPath,
        string ffmpegPath,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (project.ContentDuration < 0.05)
            throw new InvalidOperationException("There is nothing on the timeline to export.");

        var preset = CreatePreset(project, quality);
        var total = project.ContentDuration;

        var clips = CollectClips(project);
        if (!clips.Any(c => c.Visual))
            throw new InvalidOperationException("There are no video clips to render.");

        var arguments = new List<string> { "-y" };
        foreach (var clip in clips)
        {
            if (clip.Clip.Kind == MediaKind.Image)
            {
                arguments.AddRange(new[] { "-loop", "1", "-i", clip.Asset.FilePath });
            }
            else
            {
                arguments.AddRange(new[] { "-stream_loop", "-1", "-i", clip.Asset.FilePath });
            }
        }

        var hasAudio = clips.Any(c => c.HasAudio && c.Volume > 0.0001);
        var silenceIndex = -1;
        if (!hasAudio)
        {
            silenceIndex = clips.Count;
            arguments.AddRange(new[]
            {
                "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"
            });
        }

        var graph = BuildGraph(clips, preset, total, silenceIndex);
        progress?.Report(new ExportProgress("Rendering…", 0, 1));

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        var level = preset.Width > 1920 || preset.Height > 1080
            ? (preset.Fps >= 50 ? "5.2" : "5.1")
            : (preset.Fps > 30 ? "4.2" : "4.0");
        var gop = Math.Max(1, (int)Math.Round(preset.Fps * 2));

        arguments.AddRange(new[]
        {
            "-filter_complex", graph,
            "-map", "[v]",
            "-map", "[a]",
            "-t", total.ToString("0.######", CultureInfo.InvariantCulture),
            "-c:v", "libx264",
            "-preset", preset.X264Preset,
            "-crf", preset.Crf.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p",
            "-profile:v", "high",
            "-level", level,
            "-r", preset.Fps.ToString("0.###", CultureInfo.InvariantCulture),
            "-g", gop.ToString(CultureInfo.InvariantCulture),
            "-video_track_timescale", "90000",
            "-c:a", "aac",
            "-b:a", preset.AudioBitrate,
            "-ar", "48000",
            "-ac", "2",
            "-movflags", "+faststart",
            outputPath
        });

        await RunFfmpegAsync(ffmpegPath, arguments, cancellationToken).ConfigureAwait(false);
        progress?.Report(new ExportProgress("Finished", 1, 1));
    }

    private static List<ClipInput> CollectClips(EditProject project)
    {
        var clips = new List<ClipInput>();
        var anySolo = project.Tracks.Any(t => t.IsSolo);

        // Visual layers: bottom track first so the filter graph stacks like the preview.
        foreach (var track in project.Tracks.Reverse())
        {
            if (track.Kind != TrackKind.Video) continue;

            foreach (var clip in track.Clips.OrderBy(c => c.StartSeconds))
            {
                var asset = project.FindAsset(clip.AssetId);
                if (asset == null || asset.IsMissing || asset.DurationSeconds <= 0.01) continue;
                if (clip.DurationSeconds <= 0.001) continue;

                var input = new ClipInput
                {
                    Clip = clip,
                    Asset = asset,
                    Track = track,
                    Visual = true,
                    HasAudio = asset.HasAudio && clip.Kind != MediaKind.Image,
                    Volume = (track.IsMuted || (anySolo && !track.IsSolo)) ? 0 : clip.Volume,
                    Opacity = Math.Clamp(clip.Opacity, 0, 1),
                    ManualIn = clip.FadeInSeconds,
                    ManualOut = clip.FadeOutSeconds,
                    Start = clip.StartSeconds,
                    Duration = clip.DurationSeconds,
                    SourceIn = clip.InSeconds,
                    Rate = Math.Max(0.1, clip.PlaybackRate)
                };
                clips.Add(input);
            }
        }

        // Audio layers. Keep them audible even when no video track exists.
        foreach (var track in project.Tracks)
        {
            if (track.Kind != TrackKind.Audio) continue;
            if (track.IsMuted || (anySolo && !track.IsSolo)) continue;

            foreach (var clip in track.Clips.OrderBy(c => c.StartSeconds))
            {
                var asset = project.FindAsset(clip.AssetId);
                if (asset == null || asset.IsMissing) continue;
                if (clip.DurationSeconds <= 0.001) continue;

                var input = new ClipInput
                {
                    Clip = clip,
                    Asset = asset,
                    Track = track,
                    Visual = false,
                    HasAudio = asset.HasAudio,
                    Volume = clip.Volume,
                    ManualIn = clip.FadeInSeconds,
                    ManualOut = clip.FadeOutSeconds,
                    Start = clip.StartSeconds,
                    Duration = clip.DurationSeconds,
                    SourceIn = clip.InSeconds,
                    Rate = Math.Max(0.1, clip.PlaybackRate)
                };
                clips.Add(input);
            }
        }

        ApplyOverlaps(project, clips);

        for (var i = 0; i < clips.Count; i++) clips[i].InputIndex = i;
        return clips;
    }

    private static void ApplyOverlaps(EditProject project, List<ClipInput> clips)
    {
        foreach (var track in project.Tracks)
        {
            var overlaps = project.GetOverlaps(track).ToList();
            if (overlaps.Count == 0) continue;

            foreach (var overlap in overlaps)
            {
                var duration = overlap.EndSeconds - overlap.StartSeconds;
                if (duration <= 0.01) continue;

                var incoming = clips.FirstOrDefault(c => c.Clip.Id == overlap.Incoming.Id);
                if (incoming != null)
                    incoming.FadeIn = duration;

                var outgoing = clips.FirstOrDefault(c => c.Clip.Id == overlap.Outgoing.Id);
                if (outgoing != null)
                {
                    outgoing.FadeOutAt = Math.Max(0, overlap.StartSeconds - outgoing.Clip.StartSeconds);
                    outgoing.FadeOut = duration;
                }
            }
        }
    }

    private static string BuildGraph(List<ClipInput> clips, ExportPreset preset,
        double total, int silenceIndex)
    {
        var fps = preset.Fps.ToString("0.###", CultureInfo.InvariantCulture);
        var width = preset.Width.ToString(CultureInfo.InvariantCulture);
        var height = preset.Height.ToString(CultureInfo.InvariantCulture);
        var totalText = total.ToString("0.######", CultureInfo.InvariantCulture);

        var parts = new List<string>
        {
            $"color=c=black:s={width}x{height}:r={fps}:d={totalText},setsar=1,format=rgba[b0]"
        };

        var overlays = clips.Where(c => c.Visual).ToList();
        var baseLabel = "b0";
        for (var i = 0; i < overlays.Count; i++)
        {
            var clip = overlays[i];
            var duration = clip.Duration.ToString("0.######", CultureInfo.InvariantCulture);
            var rate = clip.Rate.ToString("0.###", CultureInfo.InvariantCulture);
            var start = clip.Start.ToString("0.######", CultureInfo.InvariantCulture);
            var end = (clip.Start + clip.Duration).ToString("0.######", CultureInfo.InvariantCulture);
            var opacity = clip.Opacity.ToString("0.###", CultureInfo.InvariantCulture);

            string video;
            if (clip.Clip.Kind == MediaKind.Image)
            {
                video = $"[{clip.InputIndex}:v]fps={fps}," +
                        $"scale={width}:{height}:force_original_aspect_ratio=decrease," +
                        $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1," +
                        $"format=rgba,colorchannelmixer=aa={opacity}";
            }
            else
            {
                var sourceIn = clip.SourceIn.ToString("0.######", CultureInfo.InvariantCulture);
                var sourceDuration = (clip.Duration * clip.Rate + 0.1)
                    .ToString("0.######", CultureInfo.InvariantCulture);
                video = $"[{clip.InputIndex}:v]trim=start={sourceIn}:duration={sourceDuration}," +
                        $"setpts=(PTS-STARTPTS)/{rate},fps={fps}," +
                        $"scale={width}:{height}:force_original_aspect_ratio=decrease," +
                        $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1," +
                        $"format=rgba,colorchannelmixer=aa={opacity}";
            }

            if (clip.FadeIn > 0.01)
            {
                video += ",fade=t=in:st=0:d=" +
                         clip.FadeIn.ToString("0.######", CultureInfo.InvariantCulture) +
                         ":alpha=1";
            }

            if (clip.ManualIn > 0.01)
            {
                video += ",fade=t=in:st=0:d=" +
                         clip.ManualIn.ToString("0.######", CultureInfo.InvariantCulture) +
                         ":alpha=1";
            }

            if (clip.ManualOut > 0.01)
            {
                var fadeStart = Math.Max(0, clip.Duration - clip.ManualOut);
                video += ",fade=t=out:st=" +
                         fadeStart.ToString("0.######", CultureInfo.InvariantCulture) +
                         ":d=" + clip.ManualOut.ToString("0.######", CultureInfo.InvariantCulture) +
                         ":alpha=1";
            }

            video += $",setpts=PTS+{start}/TB[v{i}]";
            parts.Add(video);

            var nextBase = $"b{i + 1}";
            parts.Add($"[{baseLabel}][v{i}]overlay=eof_action=pass:repeatlast=0:" +
                      $"enable='between(t,{start},{end})'[{nextBase}]");
            baseLabel = nextBase;
        }

        parts.Add($"[{baseLabel}]format=yuv420p[v]");

        var audioInputs = new List<string>();
        var audioIndex = 0;
        foreach (var clip in clips)
        {
            if (!clip.HasAudio || clip.Volume <= 0.0001) continue;

            var duration = clip.Duration.ToString("0.######", CultureInfo.InvariantCulture);
            var sourceIn = clip.SourceIn.ToString("0.######", CultureInfo.InvariantCulture);
            var sourceDuration = (clip.Duration * clip.Rate + 0.1)
                .ToString("0.######", CultureInfo.InvariantCulture);
            var volume = clip.Volume.ToString("0.###", CultureInfo.InvariantCulture);
            var delay = ((int)Math.Round(clip.Start * 1000)).ToString(CultureInfo.InvariantCulture);

            var chain = $"[{clip.InputIndex}:a]atrim=start={sourceIn}:duration={sourceDuration}," +
                        $"asetpts=PTS-STARTPTS,";

            if (Math.Abs(clip.Rate - 1) > 0.001)
                chain += BuildAtempo(clip.Rate) + ",";

            chain += $"volume={volume},";

            if (clip.FadeIn > 0.01)
            {
                chain += "afade=t=in:st=0:d=" +
                         clip.FadeIn.ToString("0.######", CultureInfo.InvariantCulture) + ",";
            }

            if (clip.FadeOutAt >= 0 && clip.FadeOut > 0.01)
            {
                chain += "afade=t=out:st=" +
                         clip.FadeOutAt.ToString("0.######", CultureInfo.InvariantCulture) +
                         ":d=" + clip.FadeOut.ToString("0.######", CultureInfo.InvariantCulture) + ",";
            }

            if (clip.ManualIn > 0.01)
            {
                chain += "afade=t=in:st=0:d=" +
                         clip.ManualIn.ToString("0.######", CultureInfo.InvariantCulture) + ",";
            }

            if (clip.ManualOut > 0.01)
            {
                var fadeStart = Math.Max(0, clip.Duration - clip.ManualOut);
                chain += "afade=t=out:st=" +
                         fadeStart.ToString("0.######", CultureInfo.InvariantCulture) +
                         ":d=" + clip.ManualOut.ToString("0.######", CultureInfo.InvariantCulture) + ",";
            }

            chain += $"atrim=duration={duration},asetpts=PTS-STARTPTS," +
                     $"adelay={delay}:all=1[a{audioIndex}]";
            parts.Add(chain);
            audioInputs.Add($"[a{audioIndex}]");
            audioIndex++;
        }

        if (audioInputs.Count == 0)
        {
            parts.Add($"[{silenceIndex}:a]atrim=duration={totalText},asetpts=PTS-STARTPTS[a]");
        }
        else
        {
            parts.Add(string.Concat(audioInputs) +
                      $"amix=inputs={audioInputs.Count}:normalize=0:duration=longest," +
                      $"alimiter=limit=0.95:attack=5:release=50," +
                      $"atrim=duration={totalText},asetpts=PTS-STARTPTS[a]");
        }

        return string.Join(";", parts);
    }

    private static async Task RunFfmpegAsync(
        string ffmpegPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        var stderr = new StringBuilder();

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stderr)
            {
                if (stderr.Length < 16000) stderr.AppendLine(e.Data);
            }
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Could not start FFmpeg.");

            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw;
        }

        if (process.ExitCode != 0)
        {
            string message;
            lock (stderr) message = stderr.ToString();
            var tail = string.Join(Environment.NewLine,
                message.Split('\n').TakeLast(18).Select(line => line.TrimEnd()));
            throw new InvalidOperationException(
                $"FFmpeg could not render the timeline (exit code {process.ExitCode}).{Environment.NewLine}{tail}");
        }
    }

    private static string BuildAtempo(double rate)
    {
        rate = Math.Clamp(rate, 0.1, 10);
        var parts = new List<string>();

        while (rate > 2.0)
        {
            parts.Add("atempo=2.0");
            rate /= 2.0;
        }
        while (rate < 0.5)
        {
            parts.Add("atempo=0.5");
            rate /= 0.5;
        }

        parts.Add($"atempo={rate.ToString("0.###", CultureInfo.InvariantCulture)}");
        return string.Join(",", parts);
    }

    private static int MakeEven(int value) => Math.Max(2, value - value % 2);

    private static double ClampFps(double fps) =>
        Math.Clamp(fps > 0 ? fps : 30, 1, 120);

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "YorteVegas export" : cleaned;
    }
}
