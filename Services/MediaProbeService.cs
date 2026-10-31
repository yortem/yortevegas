using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nickvision.MPVSharp;
using YorteVegas.Models;

namespace YorteVegas.Services;

public readonly record struct MediaInfo(double Duration, int Width, int Height, bool HasAudio);

/// <summary>
/// Reads duration/size of imported media. Images are decoded with WPF; video
/// and audio use a short-lived libmpv instance (vo=null) so formats such as
/// MKV work without FFmpeg being installed separately.
/// </summary>
public static class MediaProbeService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".webm", ".wmv", ".mpg", ".mpeg", ".ts", ".m2ts", ".ogv"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".aac", ".flac", ".m4a", ".ogg", ".oga", ".opus", ".wma", ".aiff", ".aif"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"
    };

    public static readonly string OpenFileFilter =
        "Media files|" +
        "*.mp4;*.m4v;*.mov;*.mkv;*.avi;*.webm;*.wmv;*.mpg;*.mpeg;*.ts;*.m2ts;" +
        "*.mp3;*.wav;*.aac;*.flac;*.m4a;*.ogg;*.opus;*.wma;" +
        "*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|" +
        "Video|*.mp4;*.m4v;*.mov;*.mkv;*.avi;*.webm;*.wmv;*.mpg;*.mpeg;*.ts;*.m2ts|" +
        "Audio|*.mp3;*.wav;*.aac;*.flac;*.m4a;*.ogg;*.opus;*.wma|" +
        "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|" +
        "All files|*.*";

    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return VideoExtensions.Contains(ext) || AudioExtensions.Contains(ext) || ImageExtensions.Contains(ext);
    }

    public static MediaKind DetectKind(string path)
    {
        var ext = Path.GetExtension(path);
        if (ImageExtensions.Contains(ext)) return MediaKind.Image;
        if (AudioExtensions.Contains(ext)) return MediaKind.Audio;
        return MediaKind.Video;
    }

    public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var kind = DetectKind(path);
        if (kind == MediaKind.Image)
        {
            var (w, h) = await Task.Run(() => ProbeImage(path), cancellationToken).ConfigureAwait(false);
            return new MediaInfo(0, w, h, false);
        }

        return await ProbeWithMpvAsync(path, kind, cancellationToken).ConfigureAwait(false);
    }

    private static (int Width, int Height) ProbeImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            if (decoder.Frames.Count > 0)
                return (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
        }
        catch
        {
            // Unknown image: fall back to a sensible default.
        }
        return (1920, 1080);
    }

    private static async Task<MediaInfo> ProbeWithMpvAsync(
        string path, MediaKind kind, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Client? client = null;
        bool initialized = false;
        try
        {
            return await Task.Run(() =>
            {
                client = new Client();
                client.SetProperty("vo", "null");
                client.SetProperty("ao", "null");
                client.SetProperty("terminal", "no");
                client.SetProperty("osc", "no");
                client.SetProperty("input-default-bindings", "no");
                client.SetProperty("pause", "yes");
                client.SetProperty("keep-open", "yes");
                client.SetProperty("idle", "yes");
                client.Initialize();
                initialized = true;

                client.Command(new[] { "loadfile", path });

                double duration = 0;
                long width = 0;
                long height = 0;
                bool hasAudio = kind == MediaKind.Audio;

                // Poll briefly: duration usually arrives within a few hundred ms.
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
                {
                    TryGetDouble(client, "duration", out duration);
                    TryGetLong(client, "width", out width);
                    TryGetLong(client, "height", out height);

                    if (duration > 0 && (kind == MediaKind.Audio || (width > 0 && height > 0)))
                        break;

                    Thread.Sleep(60);
                }

                if (kind == MediaKind.Video)
                {
                    // channel-count only resolves when the file really has audio.
                    hasAudio = TryGetLong(client, "audio-params/channel-count", out var channels) && channels > 0;
                }

                return new MediaInfo(
                    duration > 0 ? duration : 0,
                    (int)Math.Max(0, width),
                    (int)Math.Max(0, height),
                    hasAudio);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return new MediaInfo(0, 0, 0, kind == MediaKind.Audio);
        }
        finally
        {
            if (client != null)
            {
                try
                {
                    if (initialized)
                    {
                        try { client.SetProperty("vo", "null"); } catch { }
                    }
                    client.Dispose();
                }
                catch
                {
                    // A probe must never crash the import.
                }
            }
            Gate.Release();
        }
    }

    private static bool TryGetDouble(Client client, string name, out double value)
    {
        try { client.GetProperty(name, out value); return true; }
        catch { value = 0; return false; }
    }

    private static bool TryGetLong(Client client, string name, out long value)
    {
        try { client.GetProperty(name, out value); return true; }
        catch { value = 0; return false; }
    }

    /// <summary>
    /// Grabs a Windows thumbnail for a video for the timeline filmstrip and the
    /// media library. Runs on a dedicated STA thread so Shell/COM stays happy.
    /// </summary>
    public static Task<ImageSource?> LoadShellThumbnailAsync(string path, int width = 320) =>
        RunOnStaThread(() => LoadShellThumbnail(path, width));

    private static Task<T> RunOnStaThread<T>(Func<T> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(action()); }
            catch (Exception ex) { result.SetException(ex); }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    private static ImageSource? LoadShellThumbnail(string path, int width)
    {
        IntPtr hBitmap = IntPtr.Zero;
        object? shellItem = null;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero,
                typeof(IShellItemImageFactory).GUID, out shellItem);
            var factory = (IShellItemImageFactory)shellItem;

            var requestedWidth = Math.Clamp(width, 64, 640);
            var requestedHeight = Math.Clamp((int)Math.Round(requestedWidth * 9.0 / 16.0), 36, 360);
            var hr = factory.GetImage(
                new SIZE { cx = requestedWidth, cy = requestedHeight },
                SIIGBF.THUMBNAILONLY | SIIGBF.BIGGERSIZEOK,
                out hBitmap);

            if (hr != 0 || hBitmap == IntPtr.Zero) return null;

            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();

            if (source.PixelWidth > requestedWidth)
            {
                var scale = requestedWidth / (double)source.PixelWidth;
                var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                scaled.Freeze();
                source = scaled;
            }

            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                try { DeleteObject(hBitmap); } catch { }
            }
            if (shellItem != null)
            {
                try { Marshal.ReleaseComObject(shellItem); } catch { }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [Flags]
    private enum SIIGBF
    {
        RESIZETOFIT = 0,
        BIGGERSIZEOK = 1,
        MEMORYONLY = 2,
        ICONONLY = 4,
        THUMBNAILONLY = 8,
        INCACHEONLY = 16,
        CROPTOSQUARE = 32,
        WIDETHUMBNAILS = 64,
        ICONBACKGROUND = 128,
        SCALEUP = 256
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>Small frozen thumbnail for the library (must run on the UI thread).</summary>
    public static ImageSource? LoadThumbnail(string path, int decodeWidth = 180)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = decodeWidth;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
