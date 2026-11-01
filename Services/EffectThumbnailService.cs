using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YorteVegas.Models;

namespace YorteVegas.Services;

/// <summary>
/// Renders small "before/after" tiles for the effect presets, like the Vegas
/// preset browser. A built-in sample frame is processed on the CPU at thumbnail
/// size, so opening the panel never touches the preview or FFmpeg.
/// </summary>
public static class EffectThumbnailService
{
    private const int Width = 240;
    private const int Height = 135;
    private const double RadiusScale = 0.25;

    private static readonly Dictionary<string, BitmapSource> Cache = new();
    private static byte[]? _sample;

    public static BitmapSource Render(EffectKind kind,
        IReadOnlyDictionary<string, double> values)
    {
        var key = BuildKey(kind, values);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        BitmapSource bitmap;
        if (kind == EffectKind.Crop)
        {
            bitmap = RenderCrop(values);
        }
        else if (kind == EffectKind.ChromaKey)
        {
            bitmap = RenderChromaKey(values);
        }
        else
        {
            var sample = _sample ??= CreateSample();
            var pixels = (byte[])sample.Clone();
            Apply(kind, pixels, values);

            bitmap = BitmapSource.Create(Width, Height, 96, 96,
                PixelFormats.Bgra32, null, pixels, Width * 4);
        }

        bitmap.Freeze();
        Cache[key] = bitmap;
        return bitmap;
    }

    /// <summary>
    /// The built-in 240×135 BGRA sample, used by the transition thumbnails.
    /// The returned array is a copy, so callers can tint or shift it freely.
    /// </summary>
    public static (int Width, int Height, byte[] Pixels) GetSamplePixels()
    {
        var pixels = _sample ??= CreateSample();
        return (Width, Height, (byte[])pixels.Clone());
    }

    /// <summary>The built-in sample as a frozen bitmap (used by text previews).</summary>
    public static BitmapSource GetSampleBitmap()
    {
        var (width, height, pixels) = GetSamplePixels();
        var bitmap = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static string BuildKey(EffectKind kind,
        IReadOnlyDictionary<string, double> values)
    {
        var parts = values
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key + "=" +
                        p.Value.ToString("0.####", CultureInfo.InvariantCulture));
        return kind + "|" + string.Join(",", parts);
    }

    // ------------------------------------------------------- special previews

    private static BitmapSource RenderCrop(IReadOnlyDictionary<string, double> values)
    {
        var photo = LoadSampleBitmap();
        if (photo == null) return RenderFallbackSample();

        double Get(string key, double fallback) =>
            values.TryGetValue(key, out var value) ? value : fallback;

        var x = Math.Clamp(Get("x", 0), 0, 0.95);
        var y = Math.Clamp(Get("y", 0), 0, 0.95);
        var width = Math.Clamp(Get("width", 1), 0.05, 1 - x);
        var height = Math.Clamp(Get("height", 1), 0.05, 1 - y);

        var rect = new Int32Rect(
            (int)Math.Round(x * photo.PixelWidth),
            (int)Math.Round(y * photo.PixelHeight),
            Math.Max(1, (int)Math.Round(width * photo.PixelWidth)),
            Math.Max(1, (int)Math.Round(height * photo.PixelHeight)));

        var cropped = new CroppedBitmap(photo, rect);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
            dc.DrawImage(cropped, new Rect(0, 0, Width, Height));

        var target = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static BitmapSource RenderFallbackSample()
    {
        var pixels = CreateDrawnSample();
        var bitmap = BitmapSource.Create(Width, Height, 96, 96,
            PixelFormats.Bgra32, null, pixels, Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Chroma key needs a green/blue subject, so this preview puts the photo
    /// over a key-colour card and then composites the keyed result over a
    /// checkerboard (the usual transparency pattern).
    /// </summary>
    private static BitmapSource RenderChromaKey(
        IReadOnlyDictionary<string, double> values)
    {
        double Get(string key, double fallback) =>
            values.TryGetValue(key, out var value) ? value : fallback;

        var blue = (int)Math.Round(Get("color", 0)) == 1;
        var key = blue
            ? Color.FromRgb(0x00, 0x00, 0xFF)
            : Color.FromRgb(0x00, 0xFF, 0x00);

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(key), null,
                new Rect(0, 0, Width, Height));

            var photo = LoadSampleBitmap();
            if (photo != null)
            {
                dc.PushClip(new EllipseGeometry(
                    new Point(Width / 2.0, Height / 2.0), 74, 62));
                dc.DrawImage(photo, new Rect(Width / 2.0 - 100, 6, 200, 123));
                dc.Pop();
            }
        }

        var target = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        var converted = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[Width * Height * 4];
        converted.CopyPixels(pixels, Width * 4, 0);

        var similarity = Math.Clamp(Get("similarity", 0.12), 0, 1);
        var blend = Math.Clamp(Get("blend", 0.15), 0, 1);
        var inner = similarity * 255 * 1.6;
        var outer = inner + blend * 255 * 1.2 + 1;

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var index = (y * Width + x) * 4;
                var b = pixels[index];
                var g = pixels[index + 1];
                var r = pixels[index + 2];

                var dr = r - key.R;
                var dg = g - key.G;
                var db = b - key.B;
                var distance = Math.Sqrt(dr * dr + dg * dg + db * db);
                var alpha = Math.Clamp((distance - inner) / (outer - inner), 0, 1);

                var checker = ((x / 12 + y / 12) % 2 == 0) ? 0x2A : 0x18;
                pixels[index] = (byte)Math.Round(b * alpha + checker * (1 - alpha));
                pixels[index + 1] = (byte)Math.Round(g * alpha + checker * (1 - alpha));
                pixels[index + 2] = (byte)Math.Round(r * alpha + checker * (1 - alpha));
                pixels[index + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(Width, Height, 96, 96,
            PixelFormats.Bgra32, null, pixels, Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    // ---------------------------------------------------------------- sample

    private static byte[] CreateSample()
    {
        var photo = LoadPhotoSample();
        if (photo != null) return photo;

        return CreateDrawnSample();
    }

    /// <summary>
    /// Uses Assets/sample2.png as the preset preview. It is centre-cropped to
    /// 16:9 and scaled to thumbnail size with high-quality interpolation.
    /// </summary>
    private static byte[]? LoadPhotoSample()
    {
        try
        {
            var image = LoadSampleBitmap();
            if (image == null) return null;

            var targetAspect = (double)Width / Height;
            var sourceAspect = image.PixelWidth / (double)Math.Max(1, image.PixelHeight);

            double cropWidth, cropHeight;
            if (sourceAspect > targetAspect)
            {
                cropHeight = image.PixelHeight;
                cropWidth = cropHeight * targetAspect;
            }
            else
            {
                cropWidth = image.PixelWidth;
                cropHeight = cropWidth / targetAspect;
            }

            var cropX = (image.PixelWidth - cropWidth) / 2;
            var cropY = (image.PixelHeight - cropHeight) / 2;
            var cropped = new CroppedBitmap(image, new Int32Rect(
                (int)Math.Round(cropX), (int)Math.Round(cropY),
                Math.Max(1, (int)Math.Round(cropWidth)),
                Math.Max(1, (int)Math.Round(cropHeight))));

            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var dc = visual.RenderOpen())
                dc.DrawImage(cropped, new Rect(0, 0, Width, Height));

            var target = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);

            var converted = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[Width * Height * 4];
            converted.CopyPixels(pixels, Width * 4, 0);
            return pixels;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads Assets/sample2.png straight out of the assembly resource set. This
    /// works from the app and from test harnesses, unlike a pack URI.
    /// </summary>
    private static BitmapSource? LoadSampleBitmap()
    {
        try
        {
            var assembly = typeof(EffectThumbnailService).Assembly;
            var resourceName = assembly.GetName().Name + ".g.resources";
            using var resources = assembly.GetManifestResourceStream(resourceName);
            if (resources == null) return null;

            using var reader = new System.Resources.ResourceReader(resources);
            foreach (System.Collections.DictionaryEntry entry in reader)
            {
                if (entry.Key is not string key ||
                    !key.Equals("assets/sample2.png", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry.Value is not Stream stream) return null;

                var decoder = BitmapDecoder.Create(stream,
                    BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                frame.Freeze();
                return frame;
            }
        }
        catch
        {
            // Fall back to the drawn sample below.
        }

        return null;
    }

    private static byte[] CreateDrawnSample()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var sky = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            sky.GradientStops.Add(new GradientStop(Color.FromRgb(0x1C, 0x4C, 0x8C), 0));
            sky.GradientStops.Add(new GradientStop(Color.FromRgb(0x74, 0xAE, 0xDE), 0.58));
            sky.GradientStops.Add(new GradientStop(Color.FromRgb(0xF4, 0xC8, 0x7A), 1));
            dc.DrawRectangle(sky, null, new Rect(0, 0, Width, 96));

            var sun = new RadialGradientBrush(
                Color.FromArgb(255, 255, 250, 224),
                Color.FromArgb(0, 255, 226, 150));
            dc.DrawEllipse(sun, null, new Point(190, 34), 30, 30);

            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x22, 0x52, 0x5A)), null,
                Geometry.Parse("M0 84 L44 58 L86 82 L128 52 L172 84 L214 62 L240 78 V96 H0 Z"));
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x18, 0x3E, 0x48)), null,
                Geometry.Parse("M0 92 L52 74 L104 94 L150 76 L198 96 L240 82 V96 H0 Z"));

            // A face keeps blur/sharpen/grain readable.
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xE8, 0xB4, 0x92)), null,
                new Point(56, 68), 18, 22);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xF6, 0xD2, 0xB6)), null,
                new Point(50, 59), 8, 6);
            dc.DrawEllipse(Brushes.White, null, new Point(49, 66), 3.2, 2.3);
            dc.DrawEllipse(Brushes.White, null, new Point(63, 66), 3.2, 2.3);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x2A, 0x1E, 0x1A)), null,
                new Point(49, 66), 1.5, 1.5);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x2A, 0x1E, 0x1A)), null,
                new Point(63, 66), 1.5, 1.5);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x9C, 0x5C, 0x4A)), null,
                new Point(56, 78), 5, 1.8);

            // Grey ramp + colour bars make tint, saturation and contrast obvious.
            for (var i = 0; i < 8; i++)
            {
                var level = (byte)(i * 36);
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(level, level, level)), null,
                    new Rect(i * 30, 96, 30, 14));
            }

            var bars = new[]
            {
                Color.FromRgb(0xE0, 0x3A, 0x3A), Color.FromRgb(0xE8, 0x88, 0x3A),
                Color.FromRgb(0xE8, 0xD2, 0x4A), Color.FromRgb(0x3D, 0xC4, 0x5C),
                Color.FromRgb(0x3A, 0xC8, 0xC8), Color.FromRgb(0x3A, 0x7B, 0xD8),
                Color.FromRgb(0xC0, 0x4A, 0xC0), Color.FromRgb(0xE8, 0xEE, 0xF6)
            };
            for (var i = 0; i < bars.Length; i++)
            {
                dc.DrawRectangle(new SolidColorBrush(bars[i]), null,
                    new Rect(i * 30, 110, 30, 25));
            }
        }

        var target = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        var converted = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[Width * Height * 4];
        converted.CopyPixels(pixels, Width * 4, 0);
        return pixels;
    }

    // ---------------------------------------------------------------- filters

    private static void Apply(EffectKind kind, byte[] pixels,
        IReadOnlyDictionary<string, double> values)
    {
        double Get(string key, double fallback) =>
            values.TryGetValue(key, out var value) ? value : fallback;

        switch (kind)
        {
            case EffectKind.ColorBalance:
            {
                var red = (Get("red", 1) - 1) * 128;
                var green = (Get("green", 1) - 1) * 128;
                var blue = (Get("blue", 1) - 1) * 128;
                var saturation = Get("saturation", 1);
                var brightness = Get("brightness", 0) * 255;
                var contrast = Get("contrast", 1);

                for (var i = 0; i < pixels.Length; i += 4)
                {
                    double b = pixels[i] + blue;
                    double g = pixels[i + 1] + green;
                    double r = pixels[i + 2] + red;

                    b = (b - 128) * contrast + 128 + brightness;
                    g = (g - 128) * contrast + 128 + brightness;
                    r = (r - 128) * contrast + 128 + brightness;

                    var luma = 0.299 * r + 0.587 * g + 0.114 * b;
                    pixels[i] = Clamp(luma + (b - luma) * saturation);
                    pixels[i + 1] = Clamp(luma + (g - luma) * saturation);
                    pixels[i + 2] = Clamp(luma + (r - luma) * saturation);
                }

                break;
            }
            case EffectKind.BrightnessContrast:
            {
                var brightness = Get("brightness", 0) * 255;
                var contrast = Get("contrast", 1);

                for (var i = 0; i < pixels.Length; i += 4)
                {
                    pixels[i] = Clamp((pixels[i] - 128) * contrast + 128 + brightness);
                    pixels[i + 1] = Clamp((pixels[i + 1] - 128) * contrast + 128 + brightness);
                    pixels[i + 2] = Clamp((pixels[i + 2] - 128) * contrast + 128 + brightness);
                }

                break;
            }
            case EffectKind.Vignette:
            {
                var amount = Math.Clamp(Get("amount", 0.55), 0, 1);
                var centerX = (Width - 1) / 2.0;
                var centerY = (Height - 1) / 2.0;
                var maxDistance = Math.Sqrt(centerX * centerX + centerY * centerY);

                for (var y = 0; y < Height; y++)
                {
                    for (var x = 0; x < Width; x++)
                    {
                        var dx = (x - centerX) / maxDistance;
                        var dy = (y - centerY) / maxDistance;
                        var distance = Math.Sqrt(dx * dx + dy * dy);
                        var t = Math.Clamp((distance - 0.32) / 0.68, 0, 1);
                        var factor = 1 - amount * t * t;

                        var index = (y * Width + x) * 4;
                        pixels[index] = Clamp(pixels[index] * factor);
                        pixels[index + 1] = Clamp(pixels[index + 1] * factor);
                        pixels[index + 2] = Clamp(pixels[index + 2] * factor);
                    }
                }

                break;
            }
            case EffectKind.Blur:
            {
                var radius = (int)Math.Round(Get("radius", 6) * RadiusScale);
                var passes = Math.Clamp((int)Math.Round(Get("power", 1)), 1, 3);
                for (var i = 0; i < passes; i++)
                    BoxBlur(pixels, Math.Max(1, radius));
                break;
            }
            case EffectKind.GaussianBlur:
            {
                var radius = (int)Math.Round(Get("sigma", 4) * RadiusScale);
                BoxBlur(pixels, Math.Max(1, radius));
                BoxBlur(pixels, Math.Max(1, radius));
                break;
            }
            case EffectKind.Sharpen:
            {
                var amount = Get("amount", 0.8);
                var blurred = (byte[])pixels.Clone();
                BoxBlur(blurred, 1);

                for (var i = 0; i < pixels.Length; i += 4)
                {
                    pixels[i] = Clamp(pixels[i] + (pixels[i] - blurred[i]) * amount);
                    pixels[i + 1] = Clamp(pixels[i + 1] + (pixels[i + 1] - blurred[i + 1]) * amount);
                    pixels[i + 2] = Clamp(pixels[i + 2] + (pixels[i + 2] - blurred[i + 2]) * amount);
                }

                break;
            }
            case EffectKind.FilmGrain:
            {
                var amount = Get("amount", 18) * 0.9;
                var type = (int)Math.Round(Get("type", 1));
                var random = new Random(type * 977 + 31);

                for (var i = 0; i < pixels.Length; i += 4)
                {
                    var noise = (random.NextDouble() - 0.5) * 2 * amount;
                    pixels[i] = Clamp(pixels[i] + noise);
                    pixels[i + 1] = Clamp(pixels[i + 1] + noise);
                    pixels[i + 2] = Clamp(pixels[i + 2] + noise);
                }

                break;
            }
            case EffectKind.Glow:
            {
                var radius = Math.Max(1, (int)Math.Round(Get("radius", 12) * RadiusScale));
                var threshold = Math.Clamp(Get("threshold", 0.55), 0.01, 0.95);
                var intensity = Math.Clamp(Get("intensity", 0.55), 0, 1);
                var upper = Math.Min(0.995, threshold + 0.15);

                var glow = (byte[])pixels.Clone();
                BoxBlur(glow, radius);
                BoxBlur(glow, radius);

                for (var i = 0; i < pixels.Length; i += 4)
                {
                    var luma = (0.299 * pixels[i + 2] + 0.587 * pixels[i + 1] +
                                0.114 * pixels[i]) / 255.0;
                    var ramp = Math.Clamp((luma - threshold) / (upper - threshold), 0, 1);

                    for (var c = 0; c < 3; c++)
                    {
                        var baseValue = pixels[i + c];
                        var add = glow[i + c] * ramp * intensity;
                        pixels[i + c] = Clamp(255 - (255 - baseValue) * (255 - add) / 255);
                    }
                }

                break;
            }
        }
    }

    private static void BoxBlur(byte[] pixels, int radius)
    {
        if (radius < 1) return;

        var horizontal = new byte[pixels.Length];
        var window = radius * 2 + 1;

        for (var y = 0; y < Height; y++)
        {
            var row = y * Width * 4;
            int sumB = 0, sumG = 0, sumR = 0;

            for (var offset = -radius; offset <= radius; offset++)
            {
                var x = Math.Clamp(offset, 0, Width - 1);
                sumB += pixels[row + x * 4];
                sumG += pixels[row + x * 4 + 1];
                sumR += pixels[row + x * 4 + 2];
            }

            for (var x = 0; x < Width; x++)
            {
                horizontal[row + x * 4] = (byte)(sumB / window);
                horizontal[row + x * 4 + 1] = (byte)(sumG / window);
                horizontal[row + x * 4 + 2] = (byte)(sumR / window);
                horizontal[row + x * 4 + 3] = pixels[row + x * 4 + 3];

                var remove = Math.Clamp(x - radius, 0, Width - 1);
                var add = Math.Clamp(x + radius + 1, 0, Width - 1);
                sumB += pixels[row + add * 4] - pixels[row + remove * 4];
                sumG += pixels[row + add * 4 + 1] - pixels[row + remove * 4 + 1];
                sumR += pixels[row + add * 4 + 2] - pixels[row + remove * 4 + 2];
            }
        }

        for (var x = 0; x < Width; x++)
        {
            int sumB = 0, sumG = 0, sumR = 0;

            for (var offset = -radius; offset <= radius; offset++)
            {
                var y = Math.Clamp(offset, 0, Height - 1);
                var index = y * Width * 4 + x * 4;
                sumB += horizontal[index];
                sumG += horizontal[index + 1];
                sumR += horizontal[index + 2];
            }

            for (var y = 0; y < Height; y++)
            {
                var index = y * Width * 4 + x * 4;
                pixels[index] = (byte)(sumB / window);
                pixels[index + 1] = (byte)(sumG / window);
                pixels[index + 2] = (byte)(sumR / window);

                var remove = Math.Clamp(y - radius, 0, Height - 1);
                var add = Math.Clamp(y + radius + 1, 0, Height - 1);
                sumB += horizontal[add * Width * 4 + x * 4] -
                        horizontal[remove * Width * 4 + x * 4];
                sumG += horizontal[add * Width * 4 + x * 4 + 1] -
                        horizontal[remove * Width * 4 + x * 4 + 1];
                sumR += horizontal[add * Width * 4 + x * 4 + 2] -
                        horizontal[remove * Width * 4 + x * 4 + 2];
            }
        }
    }

    private static byte Clamp(double value) =>
        (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
