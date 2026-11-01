#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YorteVegas.Models;

namespace YorteVegas.Controls;

/// <summary>A single video/audio layer for the compositor.</summary>
public sealed class PreviewLayer
{
    public Guid Key { get; init; }
    public string Path { get; init; } = "";
    public double SourceSeconds { get; init; }
    public long SeekToken { get; init; }
    public double Volume { get; init; } = 1;
    public double Opacity { get; init; } = 1;
    public double Rate { get; init; } = 1;
    public double LoopA { get; init; } = -1;
    public double LoopB { get; init; } = -1;
    public bool HasVisual { get; init; } = true;
    public double Aspect { get; init; } = 16.0 / 9.0;
    public bool Pending { get; init; }
    public bool Still { get; init; }
    public double CropLeft { get; init; }
    public double CropRight { get; init; }
    public double CropTop { get; init; }
    public double CropBottom { get; init; }
    public double PanX { get; init; }
    public double PanY { get; init; }
    public double Scale { get; init; } = 1;
    public double PositionX { get; init; }
    public double PositionY { get; init; }

    /// <summary>mpv <c>vf</c> chain for the clip's effect stack (empty = none).</summary>
    public string Filters { get; init; } = "";

    /// <summary>Chroma key is applied in the compositor shader, not in mpv.</summary>
    public bool ChromaKey { get; init; }
    public double KeyRed { get; init; }
    public double KeyGreen { get; init; }
    public double KeyBlue { get; init; }
    public double KeySimilarity { get; init; }
    public double KeyBlend { get; init; }

    /// <summary>Transition reveal: 1 = fully visible, 0 = fully hidden.</summary>
    public double Reveal { get; init; } = 1;

    /// <summary>0 = none, 1 = left→right, 2 = right→left, 3 = top→down, 4 = bottom→up, 5 = iris.</summary>
    public int RevealMode { get; init; }

    public double RevealFeather { get; init; }

    /// <summary>A text overlay layer is drawn from a pre-rendered transparent bitmap.</summary>
    public bool IsText { get; init; }
    public BitmapSource TextImage { get; init; }
}

/// <summary>
/// GPU preview compositor. One libmpv render context per active layer, each
/// rendering into its own FBO; the layers are composited in order with per-layer
/// alpha. This is the only way to show track stacking, opacity and crossfades,
/// because a single player attached to a window can only show one file.
/// </summary>
public sealed class PreviewCompositor : HwndHost
{
    private readonly DispatcherTimer _timer;
    private readonly List<LayerState> _layers = new();
    private readonly List<PreviewLayer> _specs = new();
    private readonly List<PreviewLayer> _pendingSpecs = new();
    private bool _specsDirty;
    private DateTime _redrawUntil = DateTime.UtcNow.AddSeconds(1);

    private bool _ready;
    private bool _playing;
    private bool _disposed;
    private string _error = "";

    private IntPtr _hwnd;
    private IntPtr _hdc;
    private IntPtr _glrc;
    private int _pixelW = 1;
    private int _pixelH = 1;

    private uint _program;
    private uint _vbo;
    private uint _vao;
    private int _uScale = -1, _uOffset = -1, _uTex = -1, _uAlpha = -1;
    private int _uUvScale = -1, _uUvOffset = -1;
    private int _uChromaKey = -1, _uKeyColor = -1, _uKeySimilarity = -1, _uKeyBlend = -1;
    private int _uReveal = -1, _uRevealMode = -1, _uRevealFeather = -1, _uSolid = -1;
    private int _uUseTextureAlpha = -1;

    public event EventHandler Failed;

    internal static void Log(string message)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yv-comp.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
        }
        catch { }
    }

    public PreviewCompositor()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _timer.Tick += (_, _) => RenderFrame();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _timer.Start();
                RequestRedraw();
            }
        };
    }

    public bool IsReady => _ready;
    public string Error => _error;
    public double FrameAspect { get; set; } = 16.0 / 9.0;

    /// <summary>White flash overlay for the Flash transition (0..1).</summary>
    public double Flash { get; set; }

    /// <summary>Grabs the last rendered source frame of a layer (for the crop editor).</summary>
    public bool TryCaptureLayer(Guid key, out BitmapSource image)
    {
        image = null;
        if (!_ready || _disposed || _hwnd == IntPtr.Zero || _glrc == IntPtr.Zero) return false;

        try
        {
            MakeCurrent();
            var layer = _layers.FirstOrDefault(l =>
                l.Active && l.Key == key && l.Visual && l.Loaded && l.Fbo != 0);
            if (layer == null) return false;

            layer.Player.RenderTo(layer.Fbo, layer.TargetW, layer.TargetH);
            _glBindFramebuffer(GL_FRAMEBUFFER, layer.Fbo);

            var width = layer.TargetW;
            var height = layer.TargetH;
            var stride = width * 4;
            var buffer = new byte[stride * height];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                glReadPixels(0, 0, width, height, GL_BGRA, GL_UNSIGNED_BYTE, handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
                _glBindFramebuffer(GL_FRAMEBUFFER, 0);
            }

            var bitmap = BitmapSource.Create(width, height, 96, 96,
                PixelFormats.Bgra32, null, buffer, stride);
            var flipped = new TransformedBitmap(bitmap, new ScaleTransform(1, -1));
            flipped.Freeze();
            image = flipped;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void SetPlaying(bool playing)
    {
        _playing = playing;
        RequestRedraw();
        foreach (var layer in _layers)
        {
            if (layer.Active && layer.Loaded)
                layer.Player.SetPause(!playing || layer.Pending);
        }
    }

    /// <summary>
    /// Applies the desired layer stack. Layers are matched by key so a clip
    /// keeps its player while it stays on screen.
    /// </summary>
    public void SetLayers(IReadOnlyList<PreviewLayer> layers)
    {
        if (!_ready || _disposed) return;

        // Creating players and render contexts is deferred to the render tick,
        // where the GL context is guaranteed current. Never create them from a
        // drop handler or any other arbitrary UI callback.
        _pendingSpecs.Clear();
        _pendingSpecs.AddRange(layers);
        _specsDirty = true;
        RequestRedraw();
    }

    private void ApplyPendingSpecs()
    {
        if (!_specsDirty || !_ready || _disposed) return;
        _specsDirty = false;

        _specs.Clear();
        _specs.AddRange(_pendingSpecs);

        var used = new HashSet<LayerState>();
        var ordered = new List<LayerState>(_specs.Count);
        foreach (var spec in _specs)
        {
            var layer = _layers.FirstOrDefault(l => l.Active && l.Key == spec.Key)
                        ?? _layers.FirstOrDefault(l => !used.Contains(l) && l.Key == spec.Key)
                        ?? _layers.FirstOrDefault(l => !used.Contains(l))
                        ?? CreateLayer();

            used.Add(layer);
            ordered.Add(layer);
            ApplySpec(layer, spec);
        }

        foreach (var layer in _layers)
        {
            if (used.Contains(layer)) continue;
            layer.Active = false;
            layer.Player.SetPause(true);
        }

        // Keep the render list in composite order, but keep unused layers in
        // the pool so their mpv instances can be reused by later clips.
        var spare = _layers.Where(l => !used.Contains(l)).ToList();
        _layers.Clear();
        _layers.AddRange(ordered);
        _layers.AddRange(spare);
        RequestRedraw();
    }

    private void RequestRedraw(int milliseconds = 600) =>
        _redrawUntil = DateTime.UtcNow.AddMilliseconds(milliseconds);

    private LayerState CreateLayer()
    {
        var layer = new LayerState(this);
        layer.Player.Initialize();
        _layers.Add(layer);
        return layer;
    }

    private void ApplySpec(LayerState layer, PreviewLayer spec)
    {
        layer.Active = true;
        var wasPending = layer.Pending;
        layer.Pending = spec.Pending;
        layer.Still = spec.Still;
        layer.Key = spec.Key;
        layer.Visual = spec.HasVisual;
        layer.Opacity = Math.Clamp(spec.Opacity, 0, 1);
        layer.Volume = Math.Clamp(spec.Volume, 0, 2);
        layer.Aspect = spec.Aspect <= 0.01 ? 16.0 / 9.0 : spec.Aspect;
        layer.CropLeft = Math.Clamp(spec.CropLeft, 0, 0.45);
        layer.CropRight = Math.Clamp(spec.CropRight, 0, 0.45);
        layer.CropTop = Math.Clamp(spec.CropTop, 0, 0.45);
        layer.CropBottom = Math.Clamp(spec.CropBottom, 0, 0.45);
        layer.PanX = Math.Clamp(spec.PanX, -1, 1);
        layer.PanY = Math.Clamp(spec.PanY, -1, 1);
        layer.Scale = Math.Clamp(spec.Scale, 0.05, 4);
        layer.PositionX = Math.Clamp(spec.PositionX, -1, 1);
        layer.PositionY = Math.Clamp(spec.PositionY, -1, 1);
        layer.ChromaKey = spec.ChromaKey;
        layer.KeyRed = spec.KeyRed;
        layer.KeyGreen = spec.KeyGreen;
        layer.KeyBlue = spec.KeyBlue;
        layer.KeySimilarity = spec.KeySimilarity;
        layer.KeyBlend = spec.KeyBlend;
        layer.Reveal = Math.Clamp(spec.Reveal, 0, 1);
        layer.RevealMode = spec.RevealMode;
        layer.RevealFeather = Math.Clamp(spec.RevealFeather, 0, 0.5);

        if (spec.IsText)
        {
            if (!layer.IsText)
            {
                if (layer.Fbo != 0)
                {
                    _glDeleteFramebuffers(1, ref layer.Fbo);
                    layer.Fbo = 0;
                }
                if (layer.Texture != 0)
                {
                    glDeleteTextures(1, ref layer.Texture);
                    layer.Texture = 0;
                }
                layer.TextImage = null;
                layer.TextDirty = false;
            }

            layer.IsText = true;
            layer.Loaded = true;
            layer.Pending = false;
            layer.PendingSeek = null;

            if (!ReferenceEquals(layer.TextImage, spec.TextImage))
            {
                layer.TextImage = spec.TextImage;
                layer.TextDirty = true;
            }

            RequestRedraw();
            return;
        }

        if (layer.IsText)
        {
            if (layer.Texture != 0)
            {
                glDeleteTextures(1, ref layer.Texture);
                layer.Texture = 0;
            }
            layer.TextImage = null;
            layer.TextDirty = false;
        }

        layer.IsText = false;

        if (!string.Equals(layer.Path, spec.Path, StringComparison.OrdinalIgnoreCase))
        {
            layer.Path = spec.Path;
            layer.Loaded = false;
            layer.PendingSeek = spec.SourceSeconds;
            layer.Token = spec.SeekToken;
            layer.WaitSince = DateTime.UtcNow;
            layer.Player.Load(spec.Path);
            layer.Player.SetPause(true);
        }
        else if (layer.Token != spec.SeekToken)
        {
            layer.Token = spec.SeekToken;
            layer.PendingSeek = spec.SourceSeconds;
            layer.WaitSince = DateTime.UtcNow;
        }

        if (Math.Abs(layer.CachedRate - spec.Rate) > 0.001)
        {
            layer.CachedRate = spec.Rate;
            layer.Player.SetRate(spec.Rate);
        }

        if (Math.Abs(layer.CachedLoopA - spec.LoopA) > 0.001 ||
            Math.Abs(layer.CachedLoopB - spec.LoopB) > 0.001)
        {
            layer.CachedLoopA = spec.LoopA;
            layer.CachedLoopB = spec.LoopB;
            layer.Player.SetLoop(spec.LoopA, spec.LoopB);
        }

        var volume = layer.Pending ? 0 : layer.Volume * 100;
        if (Math.Abs(layer.CachedVolume - volume) > 0.05)
        {
            layer.CachedVolume = volume;
            layer.Player.SetVolume(volume);
        }

        if (layer.CachedVisual != spec.HasVisual)
        {
            layer.CachedVisual = spec.HasVisual;
            layer.Player.SetVideoEnabled(spec.HasVisual);
        }

        var filters = spec.Filters ?? "";
        if (!string.Equals(layer.CachedFilters, filters, StringComparison.Ordinal))
        {
            layer.CachedFilters = filters;
            layer.Player.SetFilters(filters);
        }

        if (wasPending && !spec.Pending && layer.Loaded && _playing)
        {
            // Draw the cached preload frame once before letting mpv start;
            // unpausing before the first new frame can show a black flash.
            layer.StartAfterFrame = true;
            RequestRedraw();
        }
    }

    private void RenderFrame()
    {
        if (!_ready || _disposed || _hwnd == IntPtr.Zero || _glrc == IntPtr.Zero) return;

        GetClientRect(_hwnd, out var client);
        var width = Math.Max(1, client.Right - client.Left);
        var height = Math.Max(1, client.Bottom - client.Top);
        if (width != _pixelW || height != _pixelH) RequestRedraw();

        // While paused, only draw for a short window after a change.
        if (!_playing && DateTime.UtcNow > _redrawUntil) return;

        try
        {
            MakeCurrent();
            ApplyPendingSpecs();
            ProcessPendingSeeks();

            foreach (var layer in _layers)
            {
                if (!layer.Active || !layer.Visual || !layer.Loaded) continue;
                EnsureTarget(layer);
                if (!layer.IsText && layer.Fbo != 0)
                {
                    layer.Player.RenderTo(layer.Fbo, layer.TargetW, layer.TargetH);
                }
            }

            Composite();
            SwapBuffers(_hdc);

            foreach (var layer in _layers)
            {
                if (!layer.StartAfterFrame) continue;
                layer.StartAfterFrame = false;
                layer.Player.SetPause(false);
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            Log("render failed: " + ex.Message);
            _timer.Stop();
            Failed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ProcessPendingSeeks()
    {
        foreach (var layer in _layers)
        {
            if (!layer.Active) continue;

            if (layer.PendingSeek is { } target)
            {
                var ready = layer.Player.TryGetDuration(out var duration) && duration > 0.01;
                if (!ready && layer.Still &&
                    (DateTime.UtcNow - layer.WaitSince).TotalSeconds > 0.25)
                {
                    // Stills do not expose a duration; seek once the file is open.
                    ready = true;
                }

                if (ready)
                {
                    layer.Player.Seek(target);
                    layer.PendingSeek = null;
                    layer.Loaded = true;
                    layer.Player.SetPause(!_playing || layer.Pending);
                    RequestRedraw();
                }
                else if ((DateTime.UtcNow - layer.WaitSince).TotalSeconds > 4)
                {
                    layer.PendingSeek = null;
                }
            }
        }
    }

    // ------------------------------------------------------------- GL composite

    private void Composite()
    {
        _glBindFramebuffer(GL_FRAMEBUFFER, 0);
        glViewport(0, 0, _pixelW, _pixelH);
        glClearColor(0, 0, 0, 1);
        glClear(GL_COLOR_BUFFER_BIT);
        glEnable(GL_BLEND);
        glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
        _glUseProgram(_program);
        _glBindVertexArray(_vao);
        _glBindBuffer(GL_ARRAY_BUFFER, _vbo);
        _glEnableVertexAttribArray(0);
        _glVertexAttribPointer(0, 2, GL_FLOAT, false, 16, IntPtr.Zero);
        _glEnableVertexAttribArray(1);
        _glVertexAttribPointer(1, 2, GL_FLOAT, false, 16, (IntPtr)8);
        _glUniform1i(_uTex, 0);
        _glActiveTexture(GL_TEXTURE0);

        var frame = FitFrame(FrameAspect <= 0.01 ? 16.0 / 9.0 : FrameAspect,
            _pixelW, _pixelH);

        foreach (var layer in _layers)
        {
            if (!layer.Active || layer.Pending || !layer.Visual || !layer.Loaded) continue;
            if (layer.IsText ? layer.Texture == 0 : layer.Fbo == 0) continue;
            if (layer.Opacity <= 0.002) continue;

            var cropWidth = Math.Max(0.05, 1 - layer.CropLeft - layer.CropRight);
            var cropHeight = Math.Max(0.05, 1 - layer.CropTop - layer.CropBottom);
            var croppedAspect = (layer.Aspect <= 0.01 ? 16.0 / 9.0 : layer.Aspect) *
                                cropWidth / cropHeight;
            var fitted = FitInside(frame, croppedAspect);
            var placement = Math.Clamp(layer.Scale, 0.05, 4);
            var placedWidth = fitted.Width * placement;
            var placedHeight = fitted.Height * placement;
            var centerX = fitted.X + fitted.Width / 2.0 + layer.PositionX * frame.Width;
            var centerY = fitted.Y + fitted.Height / 2.0 + layer.PositionY * frame.Height;

            var scaleX = (float)(placedWidth / _pixelW);
            var scaleY = (float)(placedHeight / _pixelH);
            var offsetX = (float)(centerX / _pixelW * 2 - 1);
            var offsetY = (float)(1 - centerY / _pixelH * 2);

            _glUniform2f(_uScale, scaleX, scaleY);
            _glUniform2f(_uOffset, offsetX, offsetY);
            _glUniform1f(_uAlpha, (float)layer.Opacity);
            _glUniform1i(_uChromaKey, layer.ChromaKey ? 1 : 0);
            _glUniform1i(_uSolid, 0);
            _glUniform1i(_uUseTextureAlpha, layer.IsText ? 1 : 0);
            _glUniform1i(_uRevealMode, layer.RevealMode);
            _glUniform1f(_uReveal, (float)layer.Reveal);
            _glUniform1f(_uRevealFeather, (float)layer.RevealFeather);
            if (layer.ChromaKey)
            {
                _glUniform3f(_uKeyColor, (float)layer.KeyRed,
                    (float)layer.KeyGreen, (float)layer.KeyBlue);
                _glUniform1f(_uKeySimilarity, (float)layer.KeySimilarity);
                _glUniform1f(_uKeyBlend, (float)layer.KeyBlend);
            }

            var uvX = Math.Clamp(layer.CropLeft + layer.PanX, 0, 1 - cropWidth);
            var uvY = Math.Clamp(layer.CropTop + layer.PanY, 0, 1 - cropHeight);
            _glUniform2f(_uUvScale, (float)cropWidth, (float)cropHeight);
            _glUniform2f(_uUvOffset, (float)uvX, (float)uvY);

            glBindTexture(GL_TEXTURE_2D, layer.Texture);
            glDrawArrays(GL_TRIANGLES, 0, 6);
        }

        // Flash transition: a white card over the project frame, peaking at the
        // middle of the transition. It is drawn after the layers so it covers
        // both pictures, and it never covers the letterbox bars.
        if (Flash > 0.002 && frame.Width > 0 && frame.Height > 0)
        {
            _glUniform1i(_uSolid, 1);
            _glUniform1i(_uRevealMode, 0);
            _glUniform1i(_uChromaKey, 0);
            _glUniform1f(_uAlpha, (float)Math.Clamp(Flash, 0, 1));
            _glUniform2f(_uScale, (float)(frame.Width / (double)_pixelW),
                (float)(frame.Height / (double)_pixelH));
            _glUniform2f(_uOffset,
                (float)((frame.X + frame.Width / 2.0) / _pixelW * 2 - 1),
                (float)(1 - (frame.Y + frame.Height / 2.0) / _pixelH * 2));
            _glUniform2f(_uUvScale, 1, 1);
            _glUniform2f(_uUvOffset, 0, 0);
            glDrawArrays(GL_TRIANGLES, 0, 6);
            _glUniform1i(_uSolid, 0);
        }
    }

    private static PixelRect FitFrame(double aspect, int width, int height)
    {
        if (width <= 0 || height <= 0) return new PixelRect(0, 0, 1, 1);
        var windowAspect = (double)width / height;
        if (windowAspect > aspect)
        {
            var w = (int)Math.Round(height * aspect);
            return new PixelRect((width - w) / 2, 0, w, height);
        }

        var h = (int)Math.Round(width / aspect);
        return new PixelRect(0, (height - h) / 2, width, h);
    }

    private static PixelRect FitInside(PixelRect outer, double aspect)
    {
        var outerAspect = (double)outer.Width / outer.Height;
        if (outerAspect > aspect)
        {
            var w = (int)Math.Round(outer.Height * aspect);
            return new PixelRect(outer.X + (outer.Width - w) / 2, outer.Y, w, outer.Height);
        }

        var h = (int)Math.Round(outer.Width / aspect);
        return new PixelRect(outer.X, outer.Y + (outer.Height - h) / 2, outer.Width, h);
    }

    private readonly record struct PixelRect(int X, int Y, int Width, int Height);


    // ------------------------------------------------------------- lifecycle

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureHostClass();
        _hwnd = CreateWindowEx(0, HostClassName, "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight),
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("preview window failed");

        _hdc = GetDC(_hwnd);
        var pfd = new PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(),
            nVersion = 1,
            dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER,
            iPixelType = PFD_TYPE_RGBA,
            cColorBits = 32,
            cAlphaBits = 8,
            cDepthBits = 24,
            iLayerType = PFD_MAIN_PLANE
        };
        var format = ChoosePixelFormat(_hdc, ref pfd);
        if (format == 0) throw new InvalidOperationException("no pixel format");
        if (!SetPixelFormat(_hdc, format, ref pfd)) throw new InvalidOperationException("pixel format rejected");
        _glrc = wglCreateContext(_hdc);
        if (_glrc == IntPtr.Zero) throw new InvalidOperationException("no OpenGL context");
        wglMakeCurrent(_hdc, _glrc);

        LoadGl();
        BuildPipeline();
        _ready = true;
        // Create the usual player pool up front so playback never pays mpv
        // initialisation at a cut or at the start of the first clip.
        for (var i = 0; i < 3; i++) CreateLayer();
        RequestRedraw();
        _timer.Start();

        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _disposed = true;
        _timer.Stop();
        try { wglMakeCurrent(_hdc, _glrc); } catch { }

        foreach (var layer in _layers)
            layer.Dispose();
        _layers.Clear();

        if (_program != 0) _glDeleteProgram(_program);
        if (_vbo != 0) _glDeleteBuffers(1, ref _vbo);
        if (_vao != 0) _glDeleteVertexArrays(1, ref _vao);

        try { wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); } catch { }
        if (_glrc != IntPtr.Zero) { try { wglDeleteContext(_glrc); } catch { } _glrc = IntPtr.Zero; }
        if (_hdc != IntPtr.Zero) { try { ReleaseDC(_hwnd, _hdc); } catch { } _hdc = IntPtr.Zero; }
        if (hwnd.Handle != IntPtr.Zero) DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    private void MakeCurrent()
    {
        wglMakeCurrent(_hdc, _glrc);
        GetClientRect(_hwnd, out var r);
        _pixelW = Math.Max(1, r.Right - r.Left);
        _pixelH = Math.Max(1, r.Bottom - r.Top);
    }

    private void EnsureTarget(LayerState layer)
    {
        if (layer.IsText)
        {
            if (layer.Texture != 0 && !layer.TextDirty) return;
            UploadTextTexture(layer);
            return;
        }

        if (layer.Fbo != 0 && Math.Abs(layer.TargetAspect - layer.Aspect) < 0.001) return;

        if (layer.Fbo != 0)
        {
            _glDeleteFramebuffers(1, ref layer.Fbo);
            glDeleteTextures(1, ref layer.Texture);
            layer.Fbo = 0;
            layer.Texture = 0;
        }

        // Preview quality: 540p is plenty for the monitor and keeps playback light.
        var height = 540;
        var width = (int)Math.Round(height * layer.Aspect);
        if (width > 1200) { width = 1200; height = (int)Math.Round(width / layer.Aspect); }
        if (width < 64) width = 64;
        if (height < 64) height = 64;

        layer.TargetW = width;
        layer.TargetH = height;
        layer.TargetAspect = layer.Aspect;

        glGenTextures(1, out layer.Texture);
        glBindTexture(GL_TEXTURE_2D, layer.Texture);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, width, height, 0, GL_RGBA, GL_UNSIGNED_BYTE, IntPtr.Zero);

        _glGenFramebuffers(1, out layer.Fbo);
        _glBindFramebuffer(GL_FRAMEBUFFER, layer.Fbo);
        _glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, layer.Texture, 0);
        if (_glCheckFramebufferStatus(GL_FRAMEBUFFER) != GL_FRAMEBUFFER_COMPLETE)
            throw new InvalidOperationException("preview framebuffer incomplete");
        _glBindFramebuffer(GL_FRAMEBUFFER, 0);
    }

    private void UploadTextTexture(LayerState layer)
    {
        var image = layer.TextImage;
        if (image == null) return;

        BitmapSource source = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        if (width <= 0 || height <= 0) return;

        var stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        if (layer.Texture == 0) glGenTextures(1, out layer.Texture);
        glBindTexture(GL_TEXTURE_2D, layer.Texture);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, width, height, 0,
                GL_BGRA, GL_UNSIGNED_BYTE, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }

        layer.TargetW = width;
        layer.TargetH = height;
        layer.TargetAspect = layer.Aspect;
        layer.TextDirty = false;
        RequestRedraw();
    }

    // ------------------------------------------------------------- shaders

    private void BuildPipeline()
    {
        const string vs = @"#version 110
attribute vec2 aPos;
attribute vec2 aUv;
uniform vec2 uScale;
uniform vec2 uOffset;
uniform vec2 uUvScale;
uniform vec2 uUvOffset;
varying vec2 vUv;
varying vec2 vQuad;
void main()
{
    gl_Position = vec4(aPos * uScale + uOffset, 0.0, 1.0);
    vUv = aUv * uUvScale + uUvOffset;
    vQuad = aUv;
}";
        const string fs = @"#version 110
varying vec2 vUv;
varying vec2 vQuad;
uniform sampler2D uTex;
uniform float uAlpha;
uniform int uChromaKey;
uniform vec3 uKeyColor;
uniform float uKeySimilarity;
uniform float uKeyBlend;
uniform float uReveal;
uniform int uRevealMode;
uniform float uRevealFeather;
uniform int uSolid;
uniform int uUseTextureAlpha;
void main()
{
    if (uSolid == 1)
    {
        gl_FragColor = vec4(1.0, 1.0, 1.0, uAlpha);
        return;
    }

    vec4 c = texture2D(uTex, vUv);
    float alpha = uAlpha;
    if (uUseTextureAlpha == 1)
    {
        alpha *= c.a;
    }
    if (uChromaKey == 1)
    {
        float d = length(c.rgb - uKeyColor);
        float inner = uKeySimilarity * 1.6;
        float outer = inner + uKeyBlend * 1.2 + 0.004;
        alpha *= smoothstep(inner, outer, d);
    }

    if (uRevealMode > 0)
    {
        float f = max(uRevealFeather, 0.001);
        float m;
        if (uRevealMode == 5)
        {
            float d = distance(vQuad, vec2(0.5));
            m = clamp((uReveal * 0.75 + f - d) / (2.0 * f), 0.0, 1.0);
        }
        else
        {
            float coord;
            if (uRevealMode == 1) coord = vQuad.x;
            else if (uRevealMode == 2) coord = 1.0 - vQuad.x;
            else if (uRevealMode == 3) coord = vQuad.y;
            else coord = 1.0 - vQuad.y;
            m = clamp((uReveal + f - coord) / (2.0 * f), 0.0, 1.0);
        }
        alpha *= m;
    }

    gl_FragColor = vec4(c.rgb, alpha);
}";

        var vertex = Compile(GL_VERTEX_SHADER, vs);
        var fragment = Compile(GL_FRAGMENT_SHADER, fs);
        _program = _glCreateProgram();
        _glAttachShader(_program, vertex);
        _glAttachShader(_program, fragment);
        _glBindAttribLocation(_program, 0, "aPos");
        _glBindAttribLocation(_program, 1, "aUv");
        _glLinkProgram(_program);

        _uScale = _glGetUniformLocation(_program, "uScale");
        _uOffset = _glGetUniformLocation(_program, "uOffset");
        _uTex = _glGetUniformLocation(_program, "uTex");
        _uAlpha = _glGetUniformLocation(_program, "uAlpha");
        _uUvScale = _glGetUniformLocation(_program, "uUvScale");
        _uUvOffset = _glGetUniformLocation(_program, "uUvOffset");
        _uChromaKey = _glGetUniformLocation(_program, "uChromaKey");
        _uKeyColor = _glGetUniformLocation(_program, "uKeyColor");
        _uKeySimilarity = _glGetUniformLocation(_program, "uKeySimilarity");
        _uKeyBlend = _glGetUniformLocation(_program, "uKeyBlend");
        _uReveal = _glGetUniformLocation(_program, "uReveal");
        _uRevealMode = _glGetUniformLocation(_program, "uRevealMode");
        _uRevealFeather = _glGetUniformLocation(_program, "uRevealFeather");
        _uSolid = _glGetUniformLocation(_program, "uSolid");
        _uUseTextureAlpha = _glGetUniformLocation(_program, "uUseTextureAlpha");

        var verts = new float[]
        {
            -1, -1, 0, 1,   1, -1, 1, 1,   1, 1, 1, 0,
            -1, -1, 0, 1,   1, 1, 1, 0,   -1, 1, 0, 0
        };
        _glGenVertexArrays(1, out _vao);
        _glBindVertexArray(_vao);
        _glGenBuffers(1, out _vbo);
        _glBindBuffer(GL_ARRAY_BUFFER, _vbo);
        var ptr = Marshal.AllocHGlobal(verts.Length * sizeof(float));
        Marshal.Copy(verts, 0, ptr, verts.Length);
        _glBufferData(GL_ARRAY_BUFFER, verts.Length * sizeof(float), ptr, GL_STATIC_DRAW);
        Marshal.FreeHGlobal(ptr);
    }

    private uint Compile(uint type, string source)
    {
        var shader = _glCreateShader(type);
        _glShaderSource(shader, 1, new[] { source }, IntPtr.Zero);
        _glCompileShader(shader);
        _glGetShaderiv(shader, GL_COMPILE_STATUS, out var ok);
        if (ok == 0) throw new InvalidOperationException("preview shader failed");
        return shader;
    }

    // ------------------------------------------------------------- host class

    private const string HostClassName = "YorteVegasPreviewCompositor";
    private static ushort _hostClassAtom;
    private static readonly WndProcDelegate HostWndProc = DefWndProc;

    private static IntPtr DefWndProc(IntPtr h, uint m, IntPtr w, IntPtr l) => DefWindowProc(h, m, w, l);

    private static void EnsureHostClass()
    {
        if (_hostClassAtom != 0) return;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(HostWndProc),
            hInstance = GetModuleHandle(null),
            hbrBackground = IntPtr.Zero,
            lpszClassName = HostClassName
        };
        _hostClassAtom = RegisterClassEx(ref wc);
    }

    // ------------------------------------------------------------- mpv layer

    private sealed class LayerState : IDisposable
    {
        private readonly PreviewCompositor _owner;

        public readonly MpvPlayer Player = new();
        public Guid Key;
        public string Path = "";
        public bool Active;
        public bool Pending;
        public bool Still;
        public bool StartAfterFrame;
        public bool Visual = true;
        public bool Loaded;
        public double Opacity = 1;
        public double Volume = 1;
        public double Aspect = 16.0 / 9.0;
        public double? PendingSeek;
        public long Token = -1;
        public DateTime WaitSince = DateTime.UtcNow;
        public uint Texture;
        public uint Fbo;
        public int TargetW = 1;
        public int TargetH = 1;
        public double TargetAspect;
        public double CropLeft;
        public double CropRight;
        public double CropTop;
        public double CropBottom;
        public double PanX;
        public double PanY;
        public double Scale = 1;
        public double PositionX;
        public double PositionY;
        public double CachedRate = -1;
        public double CachedLoopA = -2;
        public double CachedLoopB = -2;
        public double CachedVolume = -1;
        public bool CachedVisual = true;
        public string CachedFilters = "\u0000";
        public bool ChromaKey;
        public double KeyRed, KeyGreen, KeyBlue;
        public double KeySimilarity, KeyBlend;
        public double Reveal = 1;
        public int RevealMode;
        public double RevealFeather;
        public bool IsText;
        public BitmapSource TextImage;
        public bool TextDirty;

        public LayerState(PreviewCompositor owner) => _owner = owner;

        public void Dispose()
        {
            Player.Dispose();
            if (Fbo != 0) _owner._glDeleteFramebuffers(1, ref Fbo);
            if (Texture != 0) glDeleteTextures(1, ref Texture);
            Fbo = 0;
            Texture = 0;
        }
    }

    // ------------------------------------------------------------- GL interop

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr GetProcAddressFn(IntPtr ctx, string name);
    private static readonly GetProcAddressFn ProcAddress = ResolveGlProc;
    private static readonly IntPtr ProcAddressPtr = Marshal.GetFunctionPointerForDelegate(ProcAddress);

    private static IntPtr ResolveGlProc(IntPtr ctx, string name)
    {
        var proc = wglGetProcAddress(name);
        if (proc == IntPtr.Zero) proc = NativeGetProcAddress(_opengl32, name);
        return proc;
    }

    private static IntPtr _opengl32 = LoadLibrary("opengl32.dll");

    private delegate void GenFn(int n, out uint id);
    private delegate void DeleteFn(int n, ref uint id);
    private delegate void BindFboFn(uint target, uint fbo);
    private delegate void FboTexFn(uint target, uint attachment, uint texTarget, uint texture, int level);
    private delegate uint CheckFboFn(uint target);
    private delegate uint CreateShaderFn(uint type);
    private delegate void ShaderSourceFn(uint shader, int count, string[] strings, IntPtr lengths);
    private delegate void CompileShaderFn(uint shader);
    private delegate void GetShaderIvFn(uint shader, uint pname, out int value);
    private delegate uint CreateProgramFn();
    private delegate void AttachShaderFn(uint program, uint shader);
    private delegate void BindAttribFn(uint program, uint index, string name);
    private delegate void LinkProgramFn(uint program);
    private delegate void DeleteProgramFn(uint program);
    private delegate void GetProgramIvFn(uint program, uint pname, out int value);
    private delegate int GetUniformFn(uint program, string name);
    private delegate void Uniform1iFn(int loc, int value);
    private delegate void Uniform1fFn(int loc, float value);
    private delegate void Uniform2fFn(int loc, float x, float y);
    private delegate void Uniform3fFn(int loc, float x, float y, float z);
    private delegate void BindVertexArrayFn(uint vao);
    private delegate void BindBufferFn(uint target, uint buffer);
    private delegate void BufferDataFn(uint target, int size, IntPtr data, uint usage);
    private delegate void VertexAttribFn(uint index, int size, uint type, bool normalized, int stride, IntPtr ptr);
    private delegate void EnableAttribFn(uint index);
    private delegate void ActiveTextureFn(uint texture);
    private delegate void UseProgramFn(uint program);

    private GenFn _glGenFramebuffers, _glGenBuffers, _glGenVertexArrays;
    private DeleteFn _glDeleteFramebuffers, _glDeleteBuffers, _glDeleteVertexArrays;
    private BindFboFn _glBindFramebuffer;
    private FboTexFn _glFramebufferTexture2D;
    private CheckFboFn _glCheckFramebufferStatus;
    private CreateShaderFn _glCreateShader;
    private ShaderSourceFn _glShaderSource;
    private CompileShaderFn _glCompileShader;
    private GetShaderIvFn _glGetShaderiv;
    private CreateProgramFn _glCreateProgram;
    private AttachShaderFn _glAttachShader;
    private BindAttribFn _glBindAttribLocation;
    private LinkProgramFn _glLinkProgram;
    private DeleteProgramFn _glDeleteProgram;
    private GetProgramIvFn _glGetProgramiv;
    private GetUniformFn _glGetUniformLocation;
    private Uniform1iFn _glUniform1i;
    private Uniform1fFn _glUniform1f;
    private Uniform2fFn _glUniform2f;
    private Uniform3fFn _glUniform3f;
    private BindVertexArrayFn _glBindVertexArray;
    private BindBufferFn _glBindBuffer;
    private BufferDataFn _glBufferData;
    private VertexAttribFn _glVertexAttribPointer;
    private EnableAttribFn _glEnableVertexAttribArray;
    private ActiveTextureFn _glActiveTexture;
    private UseProgramFn _glUseProgram;

    private void LoadGl()
    {
        _glGenFramebuffers = Gl<GenFn>("glGenFramebuffers");
        _glDeleteFramebuffers = Gl<DeleteFn>("glDeleteFramebuffers");
        _glBindFramebuffer = Gl<BindFboFn>("glBindFramebuffer");
        _glFramebufferTexture2D = Gl<FboTexFn>("glFramebufferTexture2D");
        _glCheckFramebufferStatus = Gl<CheckFboFn>("glCheckFramebufferStatus");
        _glCreateShader = Gl<CreateShaderFn>("glCreateShader");
        _glShaderSource = Gl<ShaderSourceFn>("glShaderSource");
        _glCompileShader = Gl<CompileShaderFn>("glCompileShader");
        _glGetShaderiv = Gl<GetShaderIvFn>("glGetShaderiv");
        _glCreateProgram = Gl<CreateProgramFn>("glCreateProgram");
        _glAttachShader = Gl<AttachShaderFn>("glAttachShader");
        _glBindAttribLocation = Gl<BindAttribFn>("glBindAttribLocation");
        _glLinkProgram = Gl<LinkProgramFn>("glLinkProgram");
        _glDeleteProgram = Gl<DeleteProgramFn>("glDeleteProgram");
        _glGetProgramiv = Gl<GetProgramIvFn>("glGetProgramiv");
        _glGetUniformLocation = Gl<GetUniformFn>("glGetUniformLocation");
        _glUniform1i = Gl<Uniform1iFn>("glUniform1i");
        _glUniform1f = Gl<Uniform1fFn>("glUniform1f");
        _glUniform2f = Gl<Uniform2fFn>("glUniform2f");
        _glUniform3f = Gl<Uniform3fFn>("glUniform3f");
        _glGenVertexArrays = Gl<GenFn>("glGenVertexArrays");
        _glBindVertexArray = Gl<BindVertexArrayFn>("glBindVertexArray");
        _glDeleteVertexArrays = Gl<DeleteFn>("glDeleteVertexArrays");
        _glGenBuffers = Gl<GenFn>("glGenBuffers");
        _glDeleteBuffers = Gl<DeleteFn>("glDeleteBuffers");
        _glBindBuffer = Gl<BindBufferFn>("glBindBuffer");
        _glBufferData = Gl<BufferDataFn>("glBufferData");
        _glVertexAttribPointer = Gl<VertexAttribFn>("glVertexAttribPointer");
        _glEnableVertexAttribArray = Gl<EnableAttribFn>("glEnableVertexAttribArray");
        _glActiveTexture = Gl<ActiveTextureFn>("glActiveTexture");
        _glUseProgram = Gl<UseProgramFn>("glUseProgram");
    }

    private static T Gl<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(ResolveGlProc(IntPtr.Zero, name));

    // ------------------------------------------------------------- constants / p/invoke

    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const uint CS_HREDRAW = 0x0002;
    private const uint CS_VREDRAW = 0x0001;
    private const uint PFD_DRAW_TO_WINDOW = 0x4;
    private const uint PFD_SUPPORT_OPENGL = 0x20;
    private const uint PFD_DOUBLEBUFFER = 0x1;
    private const byte PFD_TYPE_RGBA = 0;
    private const byte PFD_MAIN_PLANE = 0;

    private const uint GL_TEXTURE_2D = 0x0DE1;
    private const uint GL_TEXTURE0 = 0x84C0;
    private const uint GL_TEXTURE_MIN_FILTER = 0x2801;
    private const uint GL_TEXTURE_MAG_FILTER = 0x2800;
    private const uint GL_TEXTURE_WRAP_S = 0x2802;
    private const uint GL_TEXTURE_WRAP_T = 0x2803;
    private const int GL_LINEAR = 0x2601;
    private const int GL_CLAMP_TO_EDGE = 0x812F;
    private const int GL_RGBA = 0x1908;
    private const int GL_RGBA8 = 0x8058;
    private const uint GL_UNSIGNED_BYTE = 0x1401;
    private const uint GL_FRAMEBUFFER = 0x8D40;
    private const uint GL_COLOR_ATTACHMENT0 = 0x8CE0;
    private const uint GL_FRAMEBUFFER_COMPLETE = 0x8CD5;
    private const uint GL_VERTEX_SHADER = 0x8B31;
    private const uint GL_FRAGMENT_SHADER = 0x8B30;
    private const uint GL_COMPILE_STATUS = 0x8B81;
    private const uint GL_ARRAY_BUFFER = 0x8892;
    private const uint GL_STATIC_DRAW = 0x88E4;
    private const uint GL_FLOAT = 0x1406;
    private const uint GL_TRIANGLES = 0x0004;
    private const uint GL_BLEND = 0x0BE2;
    private const uint GL_SRC_ALPHA = 0x0302;
    private const uint GL_ONE_MINUS_SRC_ALPHA = 0x0303;
    private const uint GL_COLOR_BUFFER_BIT = 0x4000;
    private const uint GL_BGRA = 0x80E1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize, nVersion;
        public uint dwFlags;
        public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift;
        public byte cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits;
        public byte cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
        public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("gdi32.dll")]
    private static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")]
    private static extern bool SetPixelFormat(IntPtr hdc, int format, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")]
    private static extern bool SwapBuffers(IntPtr hdc);
    [DllImport("opengl32.dll")]
    private static extern IntPtr wglCreateContext(IntPtr hdc);
    [DllImport("opengl32.dll")]
    private static extern bool wglMakeCurrent(IntPtr hdc, IntPtr context);
    [DllImport("opengl32.dll")]
    private static extern bool wglDeleteContext(IntPtr context);
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr wglGetProcAddress(string name);
    [DllImport("opengl32.dll")]
    private static extern void glViewport(int x, int y, int w, int h);
    [DllImport("opengl32.dll")]
    private static extern void glClearColor(float r, float g, float b, float a);
    [DllImport("opengl32.dll")]
    private static extern void glClear(uint mask);
    [DllImport("opengl32.dll")]
    private static extern void glEnable(uint cap);
    [DllImport("opengl32.dll")]
    private static extern void glBlendFunc(uint source, uint dest);
    [DllImport("opengl32.dll")]
    private static extern void glGenTextures(int n, out uint id);
    [DllImport("opengl32.dll")]
    private static extern void glDeleteTextures(int n, ref uint id);
    [DllImport("opengl32.dll")]
    private static extern void glBindTexture(uint target, uint id);
    [DllImport("opengl32.dll")]
    private static extern void glTexParameteri(uint target, uint pname, int param);
    [DllImport("opengl32.dll")]
    private static extern void glTexImage2D(uint target, int level, int internalFormat, int w, int h,
        int border, uint format, uint type, IntPtr pixels);
    [DllImport("opengl32.dll")]
    private static extern void glDrawArrays(uint mode, int first, int count);
    [DllImport("opengl32.dll")]
    private static extern void glReadPixels(int x, int y, int width, int height, uint format, uint type, IntPtr pixels);
    [DllImport("opengl32.dll")]
    private static extern int glGetError();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr LoadLibrary(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, EntryPoint = "GetProcAddress")]
    private static extern IntPtr NativeGetProcAddress(IntPtr module, string name);
}

/// <summary>Minimal libmpv render-API client: one video/audio source per layer.</summary>
internal sealed class MpvPlayer : IDisposable
{
    private const string Lib = "libmpv-2.dll";

    private IntPtr _mpv;
    private IntPtr _render;
    private bool _initialized;

    private static readonly LibGetProcAddress GetProcDelegate = ResolveProc;
    private static readonly IntPtr GetProcPtr = Marshal.GetFunctionPointerForDelegate(GetProcDelegate);
    private static readonly IntPtr OpenGl32 = LoadLibraryNative("opengl32.dll");

    public void Initialize()
    {
        if (_initialized) return;
        _mpv = mpv_create();
        if (_mpv == IntPtr.Zero) throw new InvalidOperationException("mpv_create failed");

        SetOption("vo", "libmpv");
        SetOption("gpu-api", "opengl");
        SetOption("gpu-context", "win");
        SetOption("hwdec", "no");
        SetOption("terminal", "no");
        SetOption("osc", "no");
        SetOption("input-default-bindings", "no");
        SetOption("input-vo-keyboard", "no");
        SetOption("keep-open", "yes");
        SetOption("image-display-duration", "inf");
        SetOption("hr-seek", "yes");
        SetOption("audio-pitch-correction", "yes");
        SetOption("pause", "yes");

        var err = mpv_initialize(_mpv);
        if (err < 0) throw new InvalidOperationException("mpv_initialize failed: " + err);

        var init = new OpenGLInitParams
        {
            get_proc_address = GetProcPtr,
            get_proc_address_ctx = IntPtr.Zero
        };
        var initPtr = Marshal.AllocHGlobal(Marshal.SizeOf<OpenGLInitParams>());
        Marshal.StructureToPtr(init, initPtr, false);
        var apiPtr = Marshal.StringToHGlobalAnsi("opengl");
        try
        {
            var pars = new[]
            {
                new RenderParam { type = 1, data = apiPtr },
                new RenderParam { type = 2, data = initPtr },
                new RenderParam { type = 0, data = IntPtr.Zero }
            };
            err = mpv_render_context_create(out _render, _mpv, pars);
            if (err < 0) throw new InvalidOperationException("mpv render context failed: " + err);
        }
        finally
        {
            Marshal.FreeHGlobal(initPtr);
            Marshal.FreeHGlobal(apiPtr);
        }

        _initialized = true;
    }

    public void Load(string path)
    {
        if (!_initialized) return;
        // Async: opening a file can take >100 ms and must not block the UI thread.
        CommandAsync("loadfile", path, "replace");
    }

    public void Seek(double seconds)
    {
        if (!_initialized) return;
        Command("seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute+exact");
    }

    public void SetPause(bool pause) => SetProperty("pause", pause ? "yes" : "no");

    public void SetVolume(double volume) =>
        SetProperty("volume", Math.Clamp(volume, 0, 150).ToString("0.###", CultureInfo.InvariantCulture));

    public void SetVideoEnabled(bool enabled) => SetProperty("vid", enabled ? "auto" : "no");

    /// <summary>Replaces the clip's video filter chain (mpv <c>vf</c>).</summary>
    public void SetFilters(string filters)
    {
        if (!_initialized || _mpv == IntPtr.Zero) return;

        var value = string.IsNullOrWhiteSpace(filters) ? "" : filters;
        try
        {
            var error = mpv_set_property_string(_mpv, "vf", value);
            if (error < 0) PreviewCompositor.Log($"vf rejected ({error}): {value}");
        }
        catch (Exception ex)
        {
            PreviewCompositor.Log("vf failed: " + ex.Message);
        }
    }

    public void SetRate(double rate) =>
        SetProperty("speed", Math.Clamp(rate, 0.1, 10).ToString("0.###", CultureInfo.InvariantCulture));

    public void SetLoop(double a, double b)
    {
        if (a >= 0 && b > a)
        {
            SetProperty("ab-loop-a", a.ToString("0.###", CultureInfo.InvariantCulture));
            SetProperty("ab-loop-b", b.ToString("0.###", CultureInfo.InvariantCulture));
        }
        else
        {
            SetProperty("ab-loop-a", "no");
            SetProperty("ab-loop-b", "no");
        }
    }

    public bool TryGetDuration(out double duration) => GetDouble("duration", out duration);

    public int LastRenderResult { get; private set; } = -99;

    public void RenderTo(uint fbo, int width, int height)
    {
        if (_render == IntPtr.Zero) return;
        mpv_render_context_update(_render);
        var target = new RenderFbo { Fbo = (int)fbo, W = width, H = height, InternalFormat = 0 };
        var fboPtr = Marshal.AllocHGlobal(Marshal.SizeOf<RenderFbo>());
        Marshal.StructureToPtr(target, fboPtr, false);
        var flipPtr = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(flipPtr, 0);
        var blockPtr = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(blockPtr, 0);
        try
        {
            var pars = new[]
            {
                new RenderParam { type = 3, data = fboPtr },
                new RenderParam { type = 4, data = flipPtr },
                new RenderParam { type = 12, data = blockPtr },
                new RenderParam { type = 0, data = IntPtr.Zero }
            };
            LastRenderResult = mpv_render_context_render(_render, pars);
            mpv_render_context_report_swap(_render);
        }
        finally
        {
            Marshal.FreeHGlobal(fboPtr);
            Marshal.FreeHGlobal(flipPtr);
            Marshal.FreeHGlobal(blockPtr);
        }
    }

    public void Dispose()
    {
        try
        {
            if (_render != IntPtr.Zero) mpv_render_context_free(_render);
        }
        catch { }
        _render = IntPtr.Zero;
        try
        {
            if (_mpv != IntPtr.Zero) mpv_terminate_destroy(_mpv);
        }
        catch { }
        _mpv = IntPtr.Zero;
        _initialized = false;
    }

    private void SetOption(string name, string value)
    {
        try { mpv_set_option_string(_mpv, name, value); } catch { }
    }

    private void SetProperty(string name, string value)
    {
        if (_mpv == IntPtr.Zero) return;
        try { mpv_set_property_string(_mpv, name, value); } catch { }
    }

    private bool GetDouble(string name, out double value)
    {
        value = 0;
        if (_mpv == IntPtr.Zero) return false;
        try { return mpv_get_property(_mpv, name, 5, out value) >= 0; }
        catch { return false; }
    }

    private void CommandAsync(string command, string arg1, string arg2)
    {
        var args = new[] { command, arg1, arg2 };
        var strings = new IntPtr[args.Length + 1];
        for (var i = 0; i < args.Length; i++)
            strings[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
        var array = Marshal.AllocHGlobal(IntPtr.Size * strings.Length);
        Marshal.Copy(strings, 0, array, strings.Length);
        try { mpv_command_async(_mpv, 0, array); }
        finally
        {
            for (var i = 0; i < args.Length; i++) Marshal.FreeCoTaskMem(strings[i]);
            Marshal.FreeHGlobal(array);
        }
    }

    private void Command(string command, string arg1, string arg2)
    {
        var args = new[] { command, arg1, arg2 };
        var strings = new IntPtr[args.Length + 1];
        for (var i = 0; i < args.Length; i++)
            strings[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
        var array = Marshal.AllocHGlobal(IntPtr.Size * strings.Length);
        Marshal.Copy(strings, 0, array, strings.Length);
        try { mpv_command(_mpv, array); }
        finally
        {
            for (var i = 0; i < args.Length; i++) Marshal.FreeCoTaskMem(strings[i]);
            Marshal.FreeHGlobal(array);
        }
    }

    private static IntPtr ResolveProc(IntPtr ctx, string name)
    {
        var proc = wglGetProcAddress(name);
        if (proc == IntPtr.Zero) proc = GetProcAddressNative(OpenGl32, name);
        return proc;
    }

    private delegate IntPtr LibGetProcAddress(IntPtr ctx, [MarshalAs(UnmanagedType.LPStr)] string name);

    [StructLayout(LayoutKind.Sequential)]
    private struct RenderFbo
    {
        public int Fbo;
        public int W;
        public int H;
        public int InternalFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenGLInitParams
    {
        public IntPtr get_proc_address;
        public IntPtr get_proc_address_ctx;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RenderParam
    {
        public int type;
        public IntPtr data;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_create")]
    private static extern IntPtr mpv_create();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_initialize")]
    private static extern int mpv_initialize(IntPtr handle);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_terminate_destroy")]
    private static extern void mpv_terminate_destroy(IntPtr handle);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_set_option_string")]
    private static extern int mpv_set_option_string(IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_set_property_string")]
    private static extern int mpv_set_property_string(IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")]
    private static extern int mpv_get_property(IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out double value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_command")]
    private static extern int mpv_command(IntPtr handle, IntPtr args);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_command_async")]
    private static extern int mpv_command_async(IntPtr handle, ulong replyUserData, IntPtr args);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_render_context_create")]
    private static extern int mpv_render_context_create(out IntPtr context, IntPtr handle, [In] RenderParam[] parameters);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_render_context_render")]
    private static extern int mpv_render_context_render(IntPtr context, [In] RenderParam[] parameters);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_render_context_update")]
    private static extern ulong mpv_render_context_update(IntPtr context);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_render_context_report_swap")]
    private static extern void mpv_render_context_report_swap(IntPtr context);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_render_context_free")]
    private static extern void mpv_render_context_free(IntPtr context);
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr wglGetProcAddress(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, EntryPoint = "LoadLibraryA")]
    private static extern IntPtr LoadLibraryNative(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, EntryPoint = "GetProcAddress")]
    private static extern IntPtr GetProcAddressNative(IntPtr module, string name);
}
