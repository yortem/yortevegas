using System.Globalization;

namespace YorteVegas.Services;

/// <summary>Timecode helpers (drop-frame for 29.97/59.94, non-drop otherwise).</summary>
public static class Timecode
{
    public static string Format(double seconds, double fps)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        if (fps <= 0) fps = 30;

        var nominalFps = (int)Math.Round(fps);
        if (nominalFps <= 0) nominalFps = 30;

        var dropFrame = IsDropFrameRate(fps);
        var totalFrames = (long)Math.Floor(seconds * fps + 0.000001);

        if (dropFrame)
            totalFrames = ToDropFrameNumber(totalFrames, nominalFps, fps);

        var frames = totalFrames % nominalFps;
        var totalSeconds = totalFrames / nominalFps;
        var secs = totalSeconds % 60;
        var mins = (totalSeconds / 60) % 60;
        var hours = totalSeconds / 3600;

        var separator = dropFrame ? ';' : ':';
        return $"{hours:00}:{mins:00}:{secs:00}{separator}{frames:00}";
    }

    public static bool IsDropFrameRate(double fps) =>
        Math.Abs(fps - 29.97) < 0.02 || Math.Abs(fps - 59.94) < 0.02;

    /// <summary>Maps a real frame count to drop-frame display frame numbers.</summary>
    private static long ToDropFrameNumber(long totalFrames, int nominalFps, double fps)
    {
        var dropFrames = (long)Math.Round(fps * 0.066666);
        if (dropFrames <= 0) return totalFrames;

        var framesPer10Minutes = (long)Math.Round(fps * 600);
        var framesPerMinute = nominalFps * 60L - dropFrames;
        if (framesPer10Minutes <= 0 || framesPerMinute <= 0) return totalFrames;

        var tenMinuteBlocks = totalFrames / framesPer10Minutes;
        var remainder = totalFrames % framesPer10Minutes;

        var display = totalFrames + dropFrames * 9 * tenMinuteBlocks;
        if (remainder > dropFrames)
            display += dropFrames * ((remainder - dropFrames) / framesPerMinute);

        return display;
    }

    /// <summary>Short label used on the timeline ruler.</summary>
    public static string Ruler(double seconds, double fps)
    {
        if (seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    public static string Short(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    public static double ParseFps(string text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
            return v;
        return 30;
    }
}
