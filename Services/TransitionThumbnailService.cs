using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YorteVegas.Models;

namespace YorteVegas.Services;

/// <summary>
/// Static mid-transition previews for the transition presets. They are built
/// from the same Assets/sample2.png as the effect tiles, so the browser has a
/// consistent look without touching the preview engine.
/// </summary>
public static class TransitionThumbnailService
{
    private static readonly Dictionary<TransitionKind, BitmapSource> Cache = new();

    public static BitmapSource Render(TransitionKind kind)
    {
        if (Cache.TryGetValue(kind, out var cached)) return cached;

        var (width, height, first) = EffectThumbnailService.GetSamplePixels();
        var second = BuildSecond(first, width, height);
        var output = new byte[first.Length];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * width + x) * 4;
                double u = width <= 1 ? 0.5 : x / (double)(width - 1);
                double v = height <= 1 ? 0.5 : y / (double)(height - 1);

                switch (kind)
                {
                    case TransitionKind.Slide:
                    {
                        var shift = (int)Math.Round(width * 0.24);
                        if (x >= shift)
                            CopyInto(second, width, x - shift, y, output, index);
                        else
                            CopyInto(first, width, x, y, output, index);
                        continue;
                    }
                    case TransitionKind.Push:
                    {
                        var split = (int)Math.Round(width * 0.76);
                        var shift = (int)Math.Round(width * 0.24);
                        if (x < split)
                            CopyInto(first, width, Math.Clamp(x + shift, 0, width - 1),
                                y, output, index);
                        else
                            CopyInto(second, width, Math.Clamp(x - split, 0, width - 1),
                                y, output, index);
                        continue;
                    }
                    case TransitionKind.Cross:
                        BlendInto(first, second, Math.Clamp((u + v) / 2, 0, 1),
                            output, index);
                        break;
                    case TransitionKind.Wipe:
                        BlendInto(first, second, Math.Clamp((u - 0.46) / 0.08, 0, 1),
                            output, index);
                        break;
                    case TransitionKind.Iris:
                    {
                        var dx = x - width / 2.0;
                        var dy = y - height / 2.0;
                        var distance = Math.Sqrt(dx * dx + dy * dy) /
                                       Math.Max(1, Math.Sqrt(
                                           width * width / 4.0 + height * height / 4.0));
                        BlendInto(first, second, Math.Clamp((0.58 - distance) / 0.1, 0, 1),
                            output, index);
                        break;
                    }
                    case TransitionKind.Flash:
                        BlendInto(first, second, 0.5, output, index);
                        output[index] = (byte)Math.Round(output[index] * 0.35 + 255 * 0.65);
                        output[index + 1] = (byte)Math.Round(output[index + 1] * 0.35 + 255 * 0.65);
                        output[index + 2] = (byte)Math.Round(output[index + 2] * 0.35 + 255 * 0.65);
                        output[index + 3] = 255;
                        break;
                    default: // Dissolve
                        BlendInto(first, second, 0.5, output, index);
                        break;
                }
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, output, width * 4);
        bitmap.Freeze();
        Cache[kind] = bitmap;
        return bitmap;
    }

    private static byte[] BuildSecond(byte[] first, int width, int height)
    {
        var second = new byte[first.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var source = (y * width + (width - 1 - x)) * 4;
                var target = (y * width + x) * 4;
                second[target] = Clamp(first[source] * 1.14 + 16);
                second[target + 1] = Clamp(first[source + 1] * 0.94);
                second[target + 2] = Clamp(first[source + 2] * 0.78);
                second[target + 3] = 255;
            }
        }

        return second;
    }

    private static void BlendInto(byte[] a, byte[] b, double t,
        byte[] output, int index)
    {
        var inverse = 1 - t;
        output[index] = (byte)Math.Round(a[index] * inverse + b[index] * t);
        output[index + 1] = (byte)Math.Round(a[index + 1] * inverse + b[index + 1] * t);
        output[index + 2] = (byte)Math.Round(a[index + 2] * inverse + b[index + 2] * t);
        output[index + 3] = 255;
    }

    private static void CopyInto(byte[] source, int width, int x, int y,
        byte[] output, int index)
    {
        var sourceIndex = (y * width + x) * 4;
        output[index] = source[sourceIndex];
        output[index + 1] = source[sourceIndex + 1];
        output[index + 2] = source[sourceIndex + 2];
        output[index + 3] = 255;
    }

    private static byte Clamp(double value) =>
        (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
