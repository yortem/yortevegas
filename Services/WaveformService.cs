using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace YorteVegas.Services;

/// <summary>
/// Decodes a file's audio and reduces it to normalized peaks for the timeline
/// waveform. Uses Media Foundation (through NAudio) so video files work without
/// an external FFmpeg. Results are cached by path for undo / project reloads.
/// </summary>
public static class WaveformService
{
    public const int Bins = 1600;

    private static readonly ConcurrentDictionary<string, float[]> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim Gate = new(2);

    public static async Task<float[]?> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Cache.TryGetValue(path, out cached)) return cached;

            var peaks = await Task.Run(() => Read(path, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            if (peaks != null) Cache[path] = peaks;
            return peaks;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static float[]? Read(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = OpenReader(path);
            if (reader == null) return null;

            var format = reader.WaveFormat;
            if (format.Channels <= 0 || format.SampleRate <= 0) return null;

            var channels = format.Channels;
            var totalFrames = Math.Max(1, reader.Length / Math.Max(1, format.BlockAlign));
            var samples = reader.ToSampleProvider();
            var buffer = new float[channels * 4096];
            var peaks = new float[Bins];
            long frame = 0;
            int read;

            while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
            {
                var frames = read / channels;
                for (var i = 0; i < frames; i++)
                {
                    var peak = 0f;
                    for (var c = 0; c < channels; c++)
                    {
                        var value = Math.Abs(buffer[i * channels + c]);
                        if (value > peak) peak = value;
                    }

                    var bin = (int)((frame + i) * Bins / totalFrames);
                    if (bin >= Bins) bin = Bins - 1;
                    if (peak > peaks[bin]) peaks[bin] = peak;
                }

                frame += frames;
                if (cancellationToken.IsCancellationRequested) return null;
            }

            var max = 0f;
            foreach (var value in peaks)
                if (value > max) max = value;
            if (max <= 0.0001f) return null;

            // Normalize so quiet dialogue still shows its shape; the absolute
            // level is not the point of the timeline waveform.
            for (var i = 0; i < peaks.Length; i++)
                peaks[i] = Math.Min(1f, peaks[i] / max);

            return peaks;
        }
        catch
        {
            return null;
        }
    }

    private static WaveStream? OpenReader(string path)
    {
        try
        {
            return new MediaFoundationReader(path);
        }
        catch
        {
            try
            {
                return new AudioFileReader(path);
            }
            catch
            {
                return null;
            }
        }
    }
}
