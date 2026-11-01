using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using YorteVegas.Models;

namespace YorteVegas.Services;

/// <summary>
/// Rasterises a <see cref="TextOverlay"/> into a transparent bitmap. The monitor,
/// the panel preview and the export all use this one renderer, so the text looks
/// the same everywhere. The bitmap is cached by its full style, which keeps the
/// preview cheap while a clip is playing.
/// </summary>
public static class TextOverlayRenderer
{
    private const int CacheLimit = 32;
    private static readonly Dictionary<string, BitmapSource> Cache = new();
    private static readonly object Gate = new();
    private static BitmapSource? _sample;

    public static void ClearCache()
    {
        lock (Gate) Cache.Clear();
    }

    public static BitmapSource Render(TextOverlay text, int width, int height,
        bool withSampleBackground = false)
    {
        width = Math.Max(8, width);
        height = Math.Max(8, height);

        var key = BuildKey(text, width, height, withSampleBackground);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }

        var bitmap = RunOnSta(() => RenderCore(text, width, height, withSampleBackground));
        lock (Gate)
        {
            if (Cache.Count >= CacheLimit) Cache.Clear();
            Cache[key] = bitmap;
        }

        return bitmap;
    }

    public static void SavePng(BitmapSource bitmap, string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static BitmapSource RenderCore(TextOverlay text, int width, int height,
        bool withSampleBackground)
    {
        var scale = height / 1080.0;
        var fontSize = Math.Max(4, text.FontSize * scale);
        var typeface = new Typeface(
            new FontFamily(string.IsNullOrWhiteSpace(text.FontFamily) ? "Segoe UI" : text.FontFamily),
            text.Italic ? FontStyles.Italic : FontStyles.Normal,
            FontWeight.FromOpenTypeWeight(Math.Clamp(text.Weight, 100, 900)),
            FontStretches.Normal);

        var brush = new SolidColorBrush(ParseColor(text.Color));
        var flow = IsRtl(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var formatted = new FormattedText(
            text.Text ?? "",
            CultureInfo.CurrentCulture,
            flow,
            typeface,
            fontSize,
            brush,
            1.0)
        {
            MaxTextWidth = Math.Max(8, width * Math.Clamp(text.MaxWidth, 0.1, 1)),
            TextAlignment = text.Align switch
            {
                TextAlign.Left => TextAlignment.Left,
                TextAlign.Right => TextAlignment.Right,
                _ => TextAlignment.Center
            },
            MaxLineCount = 8
        };

        var boxWidth = formatted.MaxTextWidth;
        var boxHeight = Math.Max(fontSize, formatted.Height);
        var x = text.Align switch
        {
            TextAlign.Left => text.PositionX * width,
            TextAlign.Right => text.PositionX * width - boxWidth,
            _ => text.PositionX * width - boxWidth / 2
        };
        var y = text.PositionY * height - boxHeight / 2;

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);

        if (text.Shadow)
        {
            visual.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = Math.Max(0, text.ShadowBlur * scale),
                ShadowDepth = Math.Max(0, text.ShadowDepth * scale),
                Direction = 315,
                Opacity = Math.Clamp(text.ShadowOpacity, 0, 1)
            };
        }

        using (var dc = visual.RenderOpen())
        {
            if (withSampleBackground)
            {
                _sample ??= EffectThumbnailService.GetSampleBitmap();
                dc.DrawImage(_sample, new Rect(0, 0, width, height));
            }

            if (text.Background)
            {
                var padding = Math.Max(0, text.BackgroundPadding * scale);
                var background = new SolidColorBrush(WithAlpha(
                    ParseColor(text.BackgroundColor),
                    Math.Clamp(text.BackgroundOpacity, 0, 1)));
                dc.DrawRoundedRectangle(background, null,
                    new Rect(x - padding, y - padding,
                        boxWidth + padding * 2, boxHeight + padding * 2),
                    Math.Max(2, 6 * scale), Math.Max(2, 6 * scale));
            }

            var geometry = formatted.BuildGeometry(new Point(x, y));
            Pen? outline = null;
            if (text.Outline)
            {
                outline = new Pen(new SolidColorBrush(ParseColor(text.OutlineColor)),
                    Math.Max(0.4, text.OutlineThickness * scale));
            }

            dc.DrawGeometry(brush, outline, geometry);
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        // Straight alpha is what the compositor shader and the PNG expect.
        var converted = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static BitmapSource RunOnSta(Func<BitmapSource> factory)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return factory();

        BitmapSource? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = factory(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (error != null) throw error;
        return result ?? throw new InvalidOperationException("Text rendering failed.");
    }

    /// <summary>True when the text should be laid out right-to-left.</summary>
    public static bool IsRtl(TextOverlay text) => text.Direction switch
    {
        TextDirection.Rtl => true,
        TextDirection.Ltr => false,
        _ => DetectRtl(text.Text)
    };

    /// <summary>Auto direction: the first strong character decides.</summary>
    private static bool DetectRtl(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;

        foreach (var c in value)
        {
            if (IsRtlChar(c)) return true;
            if (IsLtrChar(c)) return false;
        }

        return false;
    }

    private static bool IsRtlChar(char c) =>
        (c >= '\u0590' && c <= '\u05FF') ||   // Hebrew
        (c >= '\u0600' && c <= '\u06FF') ||   // Arabic
        (c >= '\u0750' && c <= '\u077F') ||
        (c >= '\u08A0' && c <= '\u08FF') ||
        (c >= '\uFB50' && c <= '\uFDFF') ||
        (c >= '\uFE70' && c <= '\uFEFF') ||
        c == '\u200F' || c == '\u061C';       // RTL / Arabic letter marks

    private static bool IsLtrChar(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
        (c >= '\u00C0' && c <= '\u024F');

    private static string BuildKey(TextOverlay text, int width, int height, bool sample)
    {
        static string F(double value) =>
            value.ToString("0.####", CultureInfo.InvariantCulture);

        return string.Join("|",
            width, height, sample,
            text.Text, text.FontFamily, F(text.FontSize), text.Weight, text.Italic,
            text.Color, (int)text.Align, (int)text.Direction,
            F(text.PositionX), F(text.PositionY),
            F(text.MaxWidth), text.Shadow, F(text.ShadowBlur), F(text.ShadowOpacity),
            F(text.ShadowDepth), text.Outline, F(text.OutlineThickness),
            text.OutlineColor, text.Background, text.BackgroundColor,
            F(text.BackgroundOpacity), F(text.BackgroundPadding));
    }

    private static Color ParseColor(string value)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(
                string.IsNullOrWhiteSpace(value) ? "#FFFFFF" : value);
        }
        catch
        {
            return Colors.White;
        }
    }

    private static Color WithAlpha(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255),
            color.R, color.G, color.B);
}
