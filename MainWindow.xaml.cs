using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using YorteVegas.Controls;
using YorteVegas.Dialogs;
using YorteVegas.Models;
using YorteVegas.Services;
using YorteVegas.ViewModels;

namespace YorteVegas;

public partial class MainWindow : Window
{
    private const string AssetDragFormat = "YorteVegas.AssetId";
    private const string EffectDragFormat = EffectsPanel.EffectDragFormat;

    private static readonly Geometry PlayGeometry = Geometry.Parse("M8 5 L19 12 L8 19 Z");
    private static readonly Geometry PauseGeometry = Geometry.Parse("M7 5 H11 V19 H7 Z M14 5 H18 V19 H14 Z");
    private static readonly Brush ReadyDotBrush = Freeze("#3DDC97");
    private static readonly Brush WarnDotBrush = Freeze("#F2B23A");
    private static readonly Brush ErrorDotBrush = Freeze("#F87171");
    private static readonly Brush LevelOkBrush = Freeze("#3DDC97");
    private static readonly Brush LevelWarnBrush = Freeze("#F2B23A");
    private static readonly Brush LevelPeakBrush = Freeze("#F87171");
    private readonly DispatcherTimer _levelTimer;
    private readonly DispatcherTimer _effectsTimer;

    private readonly AppSettings _settings;
    private readonly TimelinePreview _preview;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _autosaveTimer;
    private DateTime _lastMissingCheck = DateTime.MinValue;
    private readonly string? _startupProject;
    private readonly IReadOnlyList<string>? _startupImports;

    private EditProject _project = null!;
    private string? _currentPath;
    private bool _dirty;
    private bool _importing;
    private bool _forceClose;
    private bool _rememberMaximized;
    private MediaKind? _filter;
    private Point _assetDragStart;
    private MediaAsset? _assetDragItem;
    private bool _libraryDirty = true;
    private Guid? _nudgeClipId;
    private DateTime _nudgeStamp;
    private bool _nudging;
    private readonly HashSet<Guid> _filmstripLoading = new();
    private bool _effectsVisible;
    private bool _effectsAttaching;
    private bool _effectsCaptured;
    private ProjectFile? _effectsSnapshot;

    public MainWindow(AppSettings settings, string? startupProject = null,
        IReadOnlyList<string>? startupImports = null)
    {
        _settings = settings;
        _startupProject = startupProject;
        _startupImports = startupImports;
        InitializeComponent();

        _levelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _levelTimer.Tick += (_, _) => UpdateLevelMeter();
        _levelTimer.Start();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            SetStatus("Ready", ReadyDotBrush);
        };

        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _autosaveTimer.Tick += (_, _) => AutosaveNow();
        _autosaveTimer.Start();

        _effectsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _effectsTimer.Tick += (_, _) =>
        {
            _effectsTimer.Stop();
            _project.NotifyEffectChanged();
        };

        EffectsHost.Changed += (_, _) =>
        {
            if (!_effectsCaptured)
            {
                _effectsCaptured = true;
                if (_effectsSnapshot != null) _project.BeginEdit(_effectsSnapshot);
            }

            _effectsTimer.Stop();
            _effectsTimer.Start();
        };

        var defaults = new ProjectSettings
        {
            Name = "Untitled",
            Width = settings.DefaultWidth,
            Height = settings.DefaultHeight,
            Fps = settings.DefaultFps
        };
        var project = EditProject.Create(defaults);
        project.ImageDurationSeconds = settings.ImageDuration;

        _preview = new TimelinePreview(Preview, project);
        _preview.PlayStateChanged += (_, _) =>
        {
            UpdateTransportUi();
            UpdatePreviewVisibility();
        };
        _preview.MediaStateChanged += (_, _) => UpdatePreviewVisibility();

        // When the window is activated (startup, Alt+Tab, after a dialog),
        // make sure a WPF element owns the keyboard so Space and S work.
        Activated += (_, _) =>
        {
            if (Keyboard.FocusedElement is not TextBoxBase)
                Timeline.Focus();
            RefreshMissingMedia();
        };

        _preview.SetBaseVolume(Math.Clamp(settings.PreviewVolume, 0, 100));
        VolumeSlider.Value = Math.Clamp(settings.PreviewVolume, 0, 100);

        Timeline.ZoomChanged += (_, _) => ZoomText.Text = $"{Timeline.PixelsPerSecond:0} px/s";
        Timeline.RemoveTrackRequested += Timeline_RemoveTrackRequested;
        Timeline.RenameTrackRequested += Timeline_RenameTrackRequested;
        Timeline.ClipLevelChanged += (_, _) => _preview.RefreshClipVolume();
        Timeline.CropPanRequested += CropPanClip;
        Timeline.TrimPreviewRequested += (_, time) => _preview.PreviewAt(time);
        Timeline.TrimPreviewCleared += (_, _) => _preview.ClearPreviewTime();

        AttachProject(project);
        SetDirty(false);
        RestoreWindowBounds();
        SetStatus("Ready", ReadyDotBrush);
    }

    // ================================================================ project

    private void AttachProject(EditProject project)
    {
        _effectsTimer.Stop();
        _effectsSnapshot = null;
        _effectsCaptured = false;

        if (_project != null)
        {
            _project.TimelineChanged -= Project_TimelineChanged;
            _project.SelectionChanged -= Project_SelectionChanged;
            _project.PlayheadChanged -= Project_PlayheadChanged;
            _project.RangeChanged -= Project_RangeChanged;
            _project.UndoStateChanged -= Project_UndoStateChanged;
            _project.PropertyChanged -= Project_PropertyChanged;
        }

        _project = project;
        _project.TimelineChanged += Project_TimelineChanged;
        _project.SelectionChanged += Project_SelectionChanged;
        _project.PlayheadChanged += Project_PlayheadChanged;
        _project.RangeChanged += Project_RangeChanged;
        _project.UndoStateChanged += Project_UndoStateChanged;
        _project.PropertyChanged += Project_PropertyChanged;
        _project.ImageDurationSeconds = _settings.ImageDuration;

        Timeline.Project = _project;
        _preview.AttachProject(_project);

        LoadThumbnails();
        _libraryDirty = true;
        RefreshLibrary();
        UpdateProjectUi();
        UpdateSelectionUi();
        UpdateUndoButtons();
        UpdateTimecode();
        UpdateTransportUi();
        UpdatePreviewVisibility();
        _preview.Refresh();

        if (_effectsVisible)
        {
            var clip = _project.SelectedClip;
            AttachEffects(clip != null && clip.Kind != MediaKind.Audio ? clip : null);
        }
    }

    private void LoadThumbnails()
    {
        foreach (var asset in _project.Assets)
        {
            if (asset.Kind == MediaKind.Image && asset.Thumbnail == null && !asset.IsMissing)
                asset.Thumbnail = MediaProbeService.LoadThumbnail(asset.FilePath);
        }
    }

    private void Project_TimelineChanged(object? sender, EventArgs e)
    {
        if (!_nudging)
        {
            _nudgeClipId = null;
            _nudgeStamp = default;
        }
        SetDirty(true);
        UpdateProjectUi();
        UpdateSelectionUi();
        RefreshLibrary();
        UpdatePreviewVisibility();
        _preview.OnProjectChanged();
    }

    private void Project_SelectionChanged(object? sender, EventArgs e) => UpdateSelectionUi();

    private void Project_RangeChanged(object? sender, EventArgs e) => SetDirty(true);

    private void Project_PlayheadChanged(object? sender, EventArgs e)
    {
        UpdateTimecode();
        UpdatePreviewVisibility();
        _preview.OnPlayheadChanged();
    }

    private void Project_UndoStateChanged(object? sender, EventArgs e) => UpdateUndoButtons();

    private void Project_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditProject.Name) or nameof(EditProject.Width) or
            nameof(EditProject.Height) or nameof(EditProject.Fps) or
            nameof(EditProject.ResolutionLabel) or nameof(EditProject.FpsLabel))
        {
            UpdateProjectUi();
        }
    }

    private void UpdateProjectUi()
    {
        ProjectNameText.Text = _project.Name;
        ProjectMetaText.Text = $"{_project.ResolutionLabel} · {_project.FpsLabel}";
        MonitorResolutionText.Text = _project.ResolutionLabel;
        MonitorFpsText.Text = _project.FpsLabel;
        MonitorAspectText.Text = AspectLabel(_project.Width, _project.Height);
        Title = $"{_project.Name}{(_dirty ? "*" : "")} — YorteVegas";
        UpdateTimecode();
    }

    private void UpdateTimecode()
    {
        CurrentTimeText.Text = Timecode.Format(_project.Playhead, _project.Fps);
        TotalTimeText.Text = Timecode.Format(_project.ContentDuration, _project.Fps);
    }

    private void UpdateSelectionUi()
    {
        var count = _project.SelectionCount;
        var clip = _project.SelectedClip;

        SelectionText.Text = count switch
        {
            0 => "No clip selected",
            1 when clip != null => $"{clip.Name}  ·  {MediaAsset.FormatDuration(clip.DurationSeconds)}",
            _ => $"{count} clips selected"
        };

        DeleteButton.IsEnabled = count > 0;

        if (_effectsVisible)
            UpdateEffectsForSelection();
    }

    /// <summary>Keeps the effects inspector pointed at the selected visual clip.</summary>
    private void UpdateEffectsForSelection()
    {
        if (_effectsAttaching) return;

        var clip = _project.SelectedClip;
        var target = clip != null && clip.Kind != MediaKind.Audio ? clip : null;
        if (ReferenceEquals(target, EffectsHost.EditedClip)) return;

        _effectsAttaching = true;
        try
        {
            FlushEffects();
            AttachEffects(target);
        }
        finally
        {
            _effectsAttaching = false;
        }
    }

    private void AttachEffects(TimelineClip? clip)
    {
        _effectsCaptured = false;
        _effectsSnapshot = clip != null ? ProjectService.Capture(_project) : null;
        EffectsHost.Attach(_project, clip);
    }

    private void FlushEffects()
    {
        if (!_effectsTimer.IsEnabled) return;
        _effectsTimer.Stop();
        _project.NotifyEffectChanged();
    }

    private void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = _project.Undo.CanUndo;
        RedoButton.IsEnabled = _project.Undo.CanRedo;
    }

    private void UpdateTransportUi()
    {
        PlayIcon.Data = _preview.IsPlaying ? PauseGeometry : PlayGeometry;
        PlayButton.ToolTip = _preview.IsPlaying ? "Pause (Space)" : "Play (Space)";
    }

    private void UpdatePreviewVisibility()
    {
        var engineError = _preview.CompositorError;
        if (!string.IsNullOrEmpty(engineError))
        {
            Preview.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewPlaceholderContent.Visibility = Visibility.Visible;
            PreviewRelinkButton.Visibility = Visibility.Collapsed;
            PreviewPlaceholderText.Text = "Preview engine unavailable";
            PreviewPlaceholderHint.Text = engineError;
            return;
        }

        var hasMedia = _preview.HasMedia;
        var showVideo = hasMedia && _preview.HasVisual;
        Preview.Visibility = showVideo ? Visibility.Visible : Visibility.Collapsed;
        PreviewPlaceholder.Visibility = showVideo ? Visibility.Collapsed : Visibility.Visible;

        if (showVideo) return;

        var clip = _project.GetPreviewClipAt(_project.Playhead);
        PreviewRelinkButton.Visibility = Visibility.Collapsed;

        // Playing over a gap: keep the monitor black, like a real player.
        PreviewPlaceholderContent.Visibility =
            clip == null && _preview.IsPlaying ? Visibility.Collapsed : Visibility.Visible;

        if (hasMedia)
        {
            PreviewPlaceholderText.Text = "Audio only";
            PreviewPlaceholderHint.Text = "This clip has no video track — playback continues in the background.";
            return;
        }

        if (clip == null)
        {
            PreviewPlaceholderText.Text = "No clip at playhead";
            PreviewPlaceholderHint.Text = "Add media to the timeline, then move the playhead over it.";
            return;
        }

        var asset = _project.FindAsset(clip.AssetId);
        if (asset == null || asset.IsMissing)
        {
            PreviewPlaceholderText.Text = "Media file is missing";
            PreviewPlaceholderHint.Text = asset?.FilePath ?? "Locate the file to repair this clip.";
            if (asset != null) PreviewRelinkButton.Visibility = Visibility.Visible;
            return;
        }

        PreviewPlaceholderText.Text = "Preview unavailable";
        PreviewPlaceholderHint.Text = "mpv could not open this file.";
    }

    private void RailMedia_Click(object sender, RoutedEventArgs e) => ShowLeftPanel(false);

    private void RailEffects_Click(object sender, RoutedEventArgs e) => ShowLeftPanel(true);

    /// <summary>
    /// Swaps the media library for the effects inspector in the same column, so
    /// the monitor stays visible while an effect is adjusted.
    /// </summary>
    private void ShowLeftPanel(bool effects)
    {
        _effectsVisible = effects;
        RailMedia.IsChecked = !effects;
        RailEffects.IsChecked = effects;

        MediaLibraryPanel.Visibility = effects ? Visibility.Collapsed : Visibility.Visible;
        EffectsPanelBorder.Visibility = effects ? Visibility.Visible : Visibility.Collapsed;

        if (effects)
        {
            _preview.Pause();
            var clip = _project.SelectedClip;
            var target = clip != null && clip.Kind != MediaKind.Audio ? clip : null;

            _effectsAttaching = true;
            try
            {
                FlushEffects();
                AttachEffects(target);
            }
            finally
            {
                _effectsAttaching = false;
            }
        }
        else
        {
            FlushEffects();
            Timeline.Focus();
        }
    }

    private async void CropPanClip(TimelineClip clip)
    {
        _preview.Pause();

        var time = Math.Clamp(
            clip.StartSeconds + Math.Min(0.5, clip.DurationSeconds / 2),
            clip.StartSeconds,
            Math.Max(clip.StartSeconds, clip.EndSeconds - 0.01));
        if (Math.Abs(_project.Playhead - time) > 0.02)
            _project.Playhead = time;
        _preview.Refresh();

        BitmapSource? still = null;
        for (var attempt = 0; attempt < 14 && still == null; attempt++)
        {
            await Task.Delay(120);
            if (Preview.TryCaptureLayer(clip.Id, out var image))
                still = image;
        }

        if (still == null)
        {
            FlashStatus("Could not capture the clip frame", error: true);
            return;
        }

        var projectAspect = _project.Height > 0
            ? (double)_project.Width / _project.Height
            : 16.0 / 9.0;
        var dialog = new CropPanDialog(clip, still, projectAspect) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _project.BeginEdit();
        clip.CropLeft = dialog.CropLeft;
        clip.CropRight = dialog.CropRight;
        clip.CropTop = dialog.CropTop;
        clip.CropBottom = dialog.CropBottom;

        if (clip.CropLeft + clip.CropRight > 0.9)
            clip.CropRight = Math.Max(0, 0.9 - clip.CropLeft);
        if (clip.CropTop + clip.CropBottom > 0.9)
            clip.CropBottom = Math.Max(0, 0.9 - clip.CropTop);

        clip.PanX = dialog.PanX;
        clip.PanY = dialog.PanY;
        clip.Scale = dialog.Scale;
        clip.PositionX = dialog.PositionX;
        clip.PositionY = dialog.PositionY;
        _project.NotifyTimelineChanged();
    }

    private void UpdateLevelMeter()
    {
        var level = Math.Clamp(_preview.AudioLevel, 0, 1);
        LevelMeterFill.Width = 46 * level;
        LevelMeterFill.Fill = level >= 0.92
            ? LevelPeakBrush
            : level >= 0.72 ? LevelWarnBrush : LevelOkBrush;
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        ProjectDirtyText.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        Title = $"{_project.Name}{(dirty ? "*" : "")} — YorteVegas";
    }

    private void SetStatus(string message, Brush dot)
    {
        StatusText.Text = message.ToUpperInvariant();
        StatusDot.Fill = dot;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void FlashStatus(string message, bool error = false)
    {
        if (error) SetStatus(message, ErrorDotBrush);
        else SetStatus(message, WarnDotBrush);
    }

    // ================================================================ library

    /// <summary>
    /// Rebuilds the library list. Timeline edits do not change the asset set, so
    /// they only refresh the count; rebuilding on every edit would reset the
    /// selection and scroll position while arranging a long sequence.
    /// </summary>
    private void RefreshLibrary(bool force = false)
    {
        if (_project == null) return;

        EnsureWaveforms();

        if (!force && !_libraryDirty)
        {
            LibraryCountText.Text = _project.Assets.Count.ToString();
            return;
        }
        _libraryDirty = false;

        // Undo/redo rebuilds assets, which drops decoded thumbnails; restore them.
        foreach (var asset in _project.Assets)
        {
            if (asset.Kind == MediaKind.Image && asset.Thumbnail == null && !asset.IsMissing)
                asset.Thumbnail = MediaProbeService.LoadThumbnail(asset.FilePath);
            else if (asset.Kind == MediaKind.Video && !asset.IsMissing &&
                     asset.DurationSeconds >= 0.15 &&
                     asset.Filmstrip.Count == 0 && !asset.FilmstripUnavailable)
                _ = LoadFilmstripAsync(asset);
        }

        IEnumerable<MediaAsset> assets = _project.Assets;
        if (_filter is { } kind) assets = assets.Where(a => a.Kind == kind);

        var search = SearchBox.Text.Trim();
        if (search.Length > 0)
            assets = assets.Where(a => a.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

        var selectedId = (MediaList.SelectedItem as MediaAsset)?.Id;
        var scrollOffset = LibraryScroll?.VerticalOffset ?? 0;

        var list = assets.ToList();
        MediaList.ItemsSource = list;

        if (selectedId is { } id)
        {
            var restored = list.FirstOrDefault(a => a.Id == id);
            if (restored != null) MediaList.SelectedItem = restored;
        }

        if (scrollOffset > 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => LibraryScroll?.ScrollToVerticalOffset(scrollOffset)));
        }

        LibraryCountText.Text = _project.Assets.Count.ToString();
    }

    private ScrollViewer? _libraryScroll;

    private ScrollViewer? LibraryScroll =>
        _libraryScroll ??= FindDescendant<ScrollViewer>(MediaList);

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match) return match;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    // ---------------------------------------------------------------- relink

    private void RelinkSelectedAsset_Click(object sender, RoutedEventArgs e)
    {
        if (MediaList.SelectedItem is MediaAsset asset) _ = RelinkAssetAsync(asset);
    }

    private void PreviewRelink_Click(object sender, RoutedEventArgs e)
    {
        var clip = _project.GetPreviewClipAt(_project.Playhead);
        var asset = clip == null ? null : _project.FindAsset(clip.AssetId);
        if (asset != null) _ = RelinkAssetAsync(asset);
    }

    /// <summary>
    /// Points an existing library asset at a new file. Clips reference the
    /// asset by id, so this repairs a project whose media moved without
    /// touching the edit.
    /// </summary>
    private async Task RelinkAssetAsync(MediaAsset asset)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Relink media",
            Filter = MediaProbeService.OpenFileFilter,
            CheckFileExists = true,
            FileName = Path.GetFileName(asset.FilePath)
        };

        var folder = string.IsNullOrWhiteSpace(asset.FilePath)
            ? null
            : Path.GetDirectoryName(asset.FilePath);
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            dialog.InitialDirectory = folder;

        if (dialog.ShowDialog(this) != true) return;

        var path = dialog.FileName;
        var kind = MediaProbeService.DetectKind(path);
        if (kind != asset.Kind)
        {
            MessageDialog.ShowInfo(this, "Relink media",
                $"“{asset.Name}” is a {asset.Kind.ToLabel().ToLowerInvariant()} clip.\n\n" +
                $"Choose a {asset.Kind.ToLabel().ToLowerInvariant()} file.");
            return;
        }

        MediaInfo info;
        try
        {
            info = await MediaProbeService.ProbeAsync(path);
        }
        catch
        {
            info = new MediaInfo(0, 0, 0, kind == MediaKind.Audio);
        }

        var previousName = asset.Name;

        _project.BeginEdit();
        asset.FilePath = path;
        asset.Name = Path.GetFileName(path);
        asset.DurationSeconds = info.Duration;
        asset.Width = info.Width;
        asset.Height = info.Height;
        asset.HasAudio = info.HasAudio;
        asset.IsMissing = false;
        asset.Peaks = null;
        asset.Thumbnail = null;
        asset.SetFilmstrip(Array.Empty<ImageSource>());
        asset.FilmstripUnavailable = false;

        // Keep timeline labels in step when they still carried the old file name.
        foreach (var track in _project.Tracks)
            foreach (var clip in track.Clips.Where(c => c.AssetId == asset.Id && c.Name == previousName))
                clip.Name = asset.Name;

        _libraryDirty = true;
        _project.NotifyTimelineChanged();
        RefreshLibrary(force: true);
        EnsureWaveforms();
        Timeline.InvalidateVisual();
        _preview.Refresh();
        SetStatus($"Relinked {asset.Name}", ReadyDotBrush);
    }

    private readonly HashSet<string> _waveformLoading = new(StringComparer.OrdinalIgnoreCase);

    private void EnsureWaveforms()
    {
        if (_project == null) return;

        foreach (var asset in _project.Assets)
        {
            if (asset.Peaks != null || !asset.HasAudio || asset.IsMissing) continue;
            if (string.IsNullOrWhiteSpace(asset.FilePath) || !File.Exists(asset.FilePath)) continue;
            if (!_waveformLoading.Add(asset.FilePath)) continue;
            _ = LoadWaveformAsync(asset);
        }
    }

    private async Task LoadWaveformAsync(MediaAsset asset)
    {
        try
        {
            var peaks = await WaveformService.GetAsync(asset.FilePath);
            if (peaks != null && _project.Assets.Contains(asset))
            {
                asset.Peaks = peaks;
                Timeline.InvalidateVisual();
            }
        }
        catch
        {
            // The timeline simply keeps no waveform for unreadable audio.
        }
        finally
        {
            _waveformLoading.Remove(asset.FilePath);
        }
    }

    private async Task LoadFilmstripAsync(MediaAsset asset)
    {
        if (!_filmstripLoading.Add(asset.Id)) return;
        try
        {
            var thumb = await MediaProbeService.LoadShellThumbnailAsync(asset.FilePath);
            if (!_project.Assets.Contains(asset)) return;

            if (thumb == null)
            {
                asset.SetFilmstrip(Array.Empty<ImageSource>());
                return;
            }

            asset.SetFilmstrip(new[] { thumb });
            if (asset.Thumbnail == null) asset.Thumbnail = thumb;
            Timeline.InvalidateVisual();
        }
        finally
        {
            _filmstripLoading.Remove(asset.Id);
        }
    }

    private void ApplyFilter(MediaKind? kind)
    {
        _filter = kind;
        ChipAll.IsChecked = kind == null;
        ChipVideo.IsChecked = kind == MediaKind.Video;
        ChipAudio.IsChecked = kind == MediaKind.Audio;
        ChipImage.IsChecked = kind == MediaKind.Image;
        RefreshLibrary(force: true);
    }

    private static MediaKind? ParseFilterTag(string? tag) => tag switch
    {
        "video" => MediaKind.Video,
        "audio" => MediaKind.Audio,
        "image" => MediaKind.Image,
        _ => null
    };

    private void ChipFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle)
            ApplyFilter(ParseFilterTag(toggle.Tag as string));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_project != null) RefreshLibrary(force: true);
    }

    private void MediaList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void MediaList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-clicking the empty area below the items must not re-add the
        // previously selected asset.
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) == null) return;
        if ((_assetDragItem ?? MediaList.SelectedItem as MediaAsset) is not { } asset) return;

        if (asset.IsMissing)
        {
            _ = RelinkAssetAsync(asset);
            return;
        }

        AddAssetToTimeline(asset, _project.Playhead);
    }

    private void MediaList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _assetDragStart = e.GetPosition(MediaList);
        _assetDragItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext
            as MediaAsset;
    }

    private void MediaList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-clicking an unselected item should target that item, not the
        // previous selection.
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is MediaAsset asset && !MediaList.SelectedItems.Contains(asset))
            MediaList.SelectedItem = asset;
    }

    private void MediaList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(MediaList);
        if (Math.Abs(point.X - _assetDragStart.X) < 6 && Math.Abs(point.Y - _assetDragStart.Y) < 6)
            return;
        if (_assetDragItem is not { } asset) return;
        if (!MediaList.SelectedItems.Contains(asset)) MediaList.SelectedItem = asset;

        DragDrop.DoDragDrop(MediaList,
            new DataObject(AssetDragFormat, asset.Id.ToString()), DragDropEffects.Copy);
    }

    private void AddSelectedAsset_Click(object sender, RoutedEventArgs e) =>
        AddSelectedAssetsToTimeline();

    /// <summary>
    /// Adds every selected library item in list order. Clips on the same layer
    /// land back to back, so selecting ten files and pressing Enter assembles
    /// the whole sequence in one go.
    /// </summary>
    private void AddSelectedAssetsToTimeline()
    {
        var assets = MediaList.SelectedItems.Cast<MediaAsset>().ToList();
        if (assets.Count == 0)
        {
            FlashStatus("Select media in the library first");
            return;
        }

        if (assets.Count == 1)
        {
            if (assets[0].IsMissing) FlashStatus("Media file is missing", error: true);
            else AddAssetToTimeline(assets[0], _project.Playhead);
            return;
        }

        if (assets.All(a => a.IsMissing))
        {
            FlashStatus("Media files are missing", error: true);
            return;
        }

        _project.BeginEdit();
        var placed = 0;
        var missing = 0;
        double? firstStart = null;
        TimelineClip? last = null;

        foreach (var asset in assets)
        {
            if (asset.IsMissing)
            {
                missing++;
                continue;
            }

            var track = _project.EnsureTrack(asset.Kind);
            if (track.IsLocked) continue;

            var clip = _project.AddClip(asset, _project.Playhead, track, notify: false);
            if (clip == null) continue;

            firstStart ??= clip.StartSeconds;
            last = clip;
            placed++;
        }

        if (placed == 0)
        {
            FlashStatus("No unlocked track for this media", error: true);
            return;
        }

        _project.Playhead = firstStart ?? _project.Playhead;
        if (last != null) _project.SelectedClipId = last.Id;
        _project.NotifyTimelineChanged();
        Timeline.ScrollToPlayhead();

        var message = $"Added {placed} clip{(placed == 1 ? "" : "s")} to the timeline";
        if (missing > 0) FlashStatus($"{message} — {missing} missing");
        else SetStatus(message, ReadyDotBrush);
    }

    private void RemoveSelectedAsset_Click(object sender, RoutedEventArgs e)
    {
        var assets = MediaList.SelectedItems.Cast<MediaAsset>().ToList();
        if (assets.Count == 0) return;

        var message = assets.Count == 1
            ? $"Remove “{assets[0].Name}” and its clips from the project?"
            : $"Remove {assets.Count} items and their clips from the project?";
        var result = MessageDialog.Show(this, "Remove media", message, "Remove", null, "Cancel");
        if (result != MessageResult.Primary) return;

        _project.BeginEdit();
        _libraryDirty = true;
        foreach (var asset in assets) _project.RemoveAsset(asset);
        _preview.Refresh();
        SetStatus(assets.Count == 1 ? "Removed from library" : $"Removed {assets.Count} items", ReadyDotBrush);
    }

    private void AddAssetToTimeline(MediaAsset asset, double at, TimelineTrack? preferredTrack = null,
        bool captureUndo = true)
    {
        if (asset.IsMissing)
        {
            FlashStatus("Media file is missing", error: true);
            return;
        }

        var track = preferredTrack != null && preferredTrack.Accepts(asset.Kind) && !preferredTrack.IsLocked
            ? preferredTrack
            : _project.EnsureTrack(asset.Kind);

        if (track == null || track.IsLocked)
        {
            FlashStatus("No unlocked track for this clip", error: true);
            return;
        }

        if (captureUndo) _project.BeginEdit();
        var clip = _project.AddClip(asset, at, track);
        if (clip == null)
        {
            FlashStatus("Could not add this media", error: true);
            return;
        }

        _project.Playhead = clip.StartSeconds;
        Timeline.ScrollToPlayhead();
        SetStatus($"Added {asset.Name}", ReadyDotBrush);
    }

    private void MediaList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            _ = ImportFilesAsync(files);
            e.Handled = true;
        }
    }

    private void Timeline_DragOver(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(Timeline);
        e.Effects = DragDropEffects.None;

        if (e.Data.GetDataPresent(EffectDragFormat))
        {
            Timeline.ClearDropPlaceholder();
            if (Timeline.ClipAtPoint(point) != null)
                e.Effects = DragDropEffects.Copy;
        }
        else if (e.Data.GetDataPresent(AssetDragFormat) &&
            Guid.TryParse(e.Data.GetData(AssetDragFormat) as string, out var id))
        {
            var asset = _project.FindAsset(id);
            if (asset != null)
            {
                Timeline.ShowDropPlaceholder(point, asset.Name, asset.Kind,
                    asset.Kind == MediaKind.Image
                        ? _project.ImageDurationSeconds
                        : asset.DurationSeconds > 0 ? asset.DurationSeconds : 4);
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                Timeline.ClearDropPlaceholder();
            }
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                 e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var supported = files.Where(MediaProbeService.IsSupported).ToList();
            if (supported.Count > 0)
            {
                var label = supported.Count == 1
                    ? Path.GetFileName(supported[0])
                    : $"{supported.Count} files";
                Timeline.ShowDropPlaceholder(point, label,
                    MediaProbeService.DetectKind(supported[0]), 4);
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                Timeline.ClearDropPlaceholder();
            }
        }
        else
        {
            Timeline.ClearDropPlaceholder();
        }

        e.Handled = true;
    }

    private async void Timeline_Drop(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(Timeline);
        Timeline.ClearDropPlaceholder();

        if (e.Data.GetDataPresent(EffectDragFormat) &&
            e.Data.GetData(EffectDragFormat) is EffectDropData effectDrop)
        {
            e.Handled = true;
            var target = Timeline.ClipAtPoint(point);
            if (target == null || target.Kind == MediaKind.Audio)
            {
                FlashStatus("Drop the preset on a video or image clip", error: true);
                return;
            }

            _project.BeginEdit();
            var effect = target.Effects.FirstOrDefault(e => e.Kind == effectDrop.Kind);
            if (effect == null)
                effect = _project.AddEffect(target, effectDrop.Kind);

            foreach (var pair in effectDrop.Values)
                effect.Set(pair.Key, pair.Value);
            effect.Enabled = true;
            _project.NotifyEffectChanged();

            _project.SelectedClipId = target.Id;
            ShowLeftPanel(true);
            EffectsHost.SelectEffect(effect);
            FlashStatus($"{EffectCatalog.Get(effect.Kind).Name} applied to {target.Name}");
            return;
        }

        if (e.Data.GetDataPresent(AssetDragFormat))
        {
            var idText = e.Data.GetData(AssetDragFormat) as string;
            if (Guid.TryParse(idText, out var id))
            {
                var asset = _project.FindAsset(id);
                if (asset != null)
                {
                    var at = Timeline.DropTimeAtPoint(point);
                    var track = Timeline.FindDropTrack(point, asset.Kind);
                    if (track == null)
                        FlashStatus("No unlocked track for this drop", error: true);
                    else
                        AddAssetToTimeline(asset, at, track);
                }
            }
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            e.Handled = true;
            var supported = files.Where(MediaProbeService.IsSupported).ToList();
            if (supported.Count == 0)
            {
                FlashStatus("Unsupported file type", error: true);
                return;
            }

            var at = Timeline.DropTimeAtPoint(point);
            var track = Timeline.FindDropTrack(point, MediaProbeService.DetectKind(supported[0]));
            if (track == null)
                FlashStatus("No unlocked track — media added to library", error: true);
            await ImportFilesAsync(supported, track == null ? null : at, track);
        }
    }

    // ================================================================ importing

    private async void ImportMedia_Click(object sender, RoutedEventArgs e)
    {
        if (_importing) return;
        var dialog = new OpenFileDialog
        {
            Title = "Import media",
            Filter = MediaProbeService.OpenFileFilter,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        await ImportFilesAsync(dialog.FileNames);
    }

    private async Task ImportFilesAsync(IEnumerable<string> paths, double? dropTime = null,
        TimelineTrack? dropTrack = null)
    {
        if (_importing) return;

        var requested = paths
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p) && MediaProbeService.IsSupported(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requested.Count == 0)
        {
            SetStatus("No supported media in this drop", ReadyDotBrush);
            return;
        }

        var known = new Dictionary<string, MediaAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in _project.Assets) known[asset.FilePath] = asset;

        var newPaths = requested.Where(p => !known.ContainsKey(p)).ToList();
        if (newPaths.Count == 0 && dropTime == null)
        {
            SetStatus("Already in the library", ReadyDotBrush);
            return;
        }

        _importing = true;
        int imported = 0;
        bool captured = false;

        try
        {
            if (newPaths.Count > 0)
            {
                SetStatus($"Importing {newPaths.Count} file{(newPaths.Count == 1 ? "" : "s")}…", WarnDotBrush);

                foreach (var path in newPaths)
                {
                    MediaInfo info;
                    try
                    {
                        info = await MediaProbeService.ProbeAsync(path);
                    }
                    catch
                    {
                        info = new MediaInfo(0, 0, 0, MediaProbeService.DetectKind(path) == MediaKind.Audio);
                    }

                    if (!captured)
                    {
                        _project.BeginEdit();
                        captured = true;
                    }

                    var kind = MediaProbeService.DetectKind(path);
                    var asset = _project.AddAsset(path, kind, info.Duration, info.Width, info.Height, info.HasAudio);
                    if (kind == MediaKind.Image)
                        asset.Thumbnail = MediaProbeService.LoadThumbnail(path);
                    known[path] = asset;
                    _libraryDirty = true;
                    imported++;
                }

                _project.NotifyTimelineChanged();
                RefreshLibrary();
            }

            // Dropping onto the timeline (from Explorer or the library) should
            // insert clips, including files already present in the library.
            int placed = 0;
            double? firstStart = null;
            if (dropTime is { } at)
            {
                foreach (var path in requested)
                {
                    if (!known.TryGetValue(path, out var asset)) continue;

                    var track = dropTrack != null && dropTrack.Accepts(asset.Kind) && !dropTrack.IsLocked
                        ? dropTrack
                        : _project.Tracks.FirstOrDefault(t => t.Accepts(asset.Kind) && !t.IsLocked);
                    if (track == null) continue;

                    if (!captured)
                    {
                        _project.BeginEdit();
                        captured = true;
                    }

                    var clip = _project.AddClip(asset, at, track);
                    if (clip == null) continue;
                    placed++;
                    firstStart ??= clip.StartSeconds;
                }

                if (firstStart is { } start)
                {
                    _project.Playhead = start;
                    Timeline.ScrollToPlayhead();
                }
            }

            if (placed > 0)
            {
                SetStatus(imported > 0
                    ? $"Imported {imported} and added {placed} to the timeline"
                    : $"Added {placed} to the timeline", ReadyDotBrush);
            }
            else if (imported > 0)
            {
                SetStatus($"Imported {imported} file{(imported == 1 ? "" : "s")}", ReadyDotBrush);
            }
            else
            {
                FlashStatus("No unlocked track for this drop", error: true);
            }
        }
        catch (Exception ex)
        {
            FlashStatus($"Import failed: {ex.Message}", error: true);
        }
        finally
        {
            _importing = false;
        }
    }

    // ================================================================ project I/O

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges()) return;

        var dialog = new NewProjectDialog(_settings.DefaultWidth, _settings.DefaultHeight,
            _settings.DefaultFps) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var project = EditProject.Create(dialog.Settings);
        project.ImageDurationSeconds = _settings.ImageDuration;

        AttachProject(project);
        _currentPath = null;
        SetDirty(false);
        ResetTimelineView();
        _preview.Refresh();
        SetStatus("New project created", ReadyDotBrush);
        Timeline.Focus();
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges()) return;

        var dialog = new OpenFileDialog
        {
            Title = "Open project",
            Filter = ProjectService.FileFilter,
            DefaultExt = ProjectService.Extension,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        TryOpenProject(dialog.FileName);
    }

    private void TryOpenProject(string path)
    {
        try
        {
            var project = new EditProject();
            ProjectService.LoadInto(path, project);
            project.ImageDurationSeconds = _settings.ImageDuration;

            AttachProject(project);
            _currentPath = path;
            SetDirty(false);
            ResetTimelineView();
            _preview.Refresh();

            _settings.LastProjectPath = path;
            SetStatus($"Opened {Path.GetFileName(path)}", ReadyDotBrush);
            Timeline.Focus();
        }
        catch (Exception ex)
        {
            MessageDialog.ShowInfo(this, "Open project",
                $"YorteVegas could not open this project.\n\n{ex.Message}");
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveProject();

    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveProject(saveAs: true);

    private bool SaveProject(bool saveAs = false)
    {
        var path = _currentPath;
        if (saveAs || string.IsNullOrEmpty(path))
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save project",
                Filter = ProjectService.FileFilter,
                DefaultExt = ProjectService.Extension,
                AddExtension = true,
                FileName = _project.Name
            };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }

        try
        {
            if (string.IsNullOrEmpty(_currentPath) &&
                (string.IsNullOrWhiteSpace(_project.Name) || _project.Name == "Untitled"))
            {
                _project.Name = Path.GetFileNameWithoutExtension(path);
            }

            ProjectService.Save(_project, path);
            _currentPath = path;
            _settings.LastProjectPath = path;
            AutosaveService.Clear();
            SetDirty(false);
            UpdateProjectUi();
            SetStatus($"Saved {Path.GetFileName(path)}", ReadyDotBrush);
            return true;
        }
        catch (Exception ex)
        {
            MessageDialog.ShowInfo(this, "Save project",
                $"YorteVegas could not save this project.\n\n{ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------- autosave

    private void AutosaveNow()
    {
        if (!_dirty || _project == null) return;
        AutosaveService.Save(_project, _currentPath);
    }

    /// <summary>Picks up media that appeared (or vanished) since the project opened.</summary>
    private void RefreshMissingMedia()
    {
        if (_project == null) return;

        var now = DateTime.UtcNow;
        if ((now - _lastMissingCheck).TotalSeconds < 2) return;
        _lastMissingCheck = now;

        var changed = false;
        foreach (var asset in _project.Assets)
        {
            var missing = string.IsNullOrWhiteSpace(asset.FilePath) || !File.Exists(asset.FilePath);
            if (missing == asset.IsMissing) continue;
            asset.IsMissing = missing;
            changed = true;
        }

        if (!changed) return;

        _libraryDirty = true;
        RefreshLibrary(force: true);
        Timeline.InvalidateVisual();
        _preview.Refresh();
    }

    /// <summary>
    /// Offers unsaved work from a previous session. Returns true when a project
    /// was recovered, so startup skips opening the last project.
    /// </summary>
    private bool TryRecoverAutosave()
    {
        if (!AutosaveService.HasRecovery) return false;

        AutosaveService.LoadOriginalPath();
        var original = AutosaveService.OriginalPath;
        var written = AutosaveService.WrittenUtc;

        // A project saved after the autosave is already up to date.
        if (written is { } time && !string.IsNullOrWhiteSpace(original) &&
            File.Exists(original) && File.GetLastWriteTimeUtc(original) >= time)
        {
            AutosaveService.Clear();
            return false;
        }

        var result = MessageDialog.Show(this, "Recover project",
            "YorteVegas found unsaved changes from a previous session.\n\nRecover them?",
            "Recover", "Discard", null);

        if (result == MessageResult.Secondary)
        {
            AutosaveService.Clear();
            return false;
        }

        // Escape / closed dialog keeps the autosave for the next launch.
        if (result != MessageResult.Primary) return false;

        try
        {
            var project = new EditProject();
            ProjectService.LoadInto(AutosaveService.ProjectPath, project);
            project.ImageDurationSeconds = _settings.ImageDuration;

            AttachProject(project);
            _currentPath = AutosaveService.OriginalPath;
            SetDirty(true);
            ResetTimelineView();
            _preview.Refresh();
            SetStatus("Recovered unsaved changes", WarnDotBrush);
            return true;
        }
        catch (Exception ex)
        {
            MessageDialog.ShowInfo(this, "Recover project",
                $"YorteVegas could not recover the autosave.\n\n{ex.Message}");
            AutosaveService.Clear();
            return false;
        }
    }

    /// <summary>
    /// Tri-state save prompt: true = safe to continue, false/null = cancel.
    /// </summary>
    private bool? AskSaveChanges(string action)
    {
        var result = MessageDialog.Show(this, "Unsaved changes",
            $"Save changes to “{_project.Name}” before {action}?",
            "Save", "Don't save", "Cancel");

        return result switch
        {
            MessageResult.Primary => SaveProject(),
            MessageResult.Secondary => true,
            _ => null
        };
    }

    private bool ConfirmDiscardChanges()
    {
        if (!_dirty) return true;
        return AskSaveChanges("continuing") == true;
    }

    // ================================================================ edit actions

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (!_project.Undo.Undo(_project)) return;
        _libraryDirty = true;
        RefreshLibrary(force: true);
        _preview.Refresh();
        Timeline.ScrollToPlayhead();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (!_project.Undo.Redo(_project)) return;
        _libraryDirty = true;
        RefreshLibrary(force: true);
        _preview.Refresh();
        Timeline.ScrollToPlayhead();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var clips = _project.SelectedClips();
        if (clips.Count == 0)
        {
            FlashStatus("Select a clip first");
            return;
        }

        if (clips.All(c => _project.TrackOf(c)?.IsLocked == true))
        {
            FlashStatus("This track is locked", error: true);
            return;
        }

        _project.BeginEdit();
        var deleted = _project.DeleteSelectedClips();
        if (deleted == 0)
        {
            FlashStatus("This track is locked", error: true);
            return;
        }

        _preview.Refresh();
        SetStatus(deleted == 1 ? "Clip deleted" : $"{deleted} clips deleted", ReadyDotBrush);
    }

    private void DeleteAndCloseGap()
    {
        var clips = _project.SelectedClips();
        if (clips.Count == 0)
        {
            FlashStatus("Select a clip first");
            return;
        }

        if (clips.All(c => _project.TrackOf(c)?.IsLocked == true))
        {
            FlashStatus("This track is locked", error: true);
            return;
        }

        _project.BeginEdit();
        var deleted = _project.DeleteSelectedClipsAndCloseGaps();
        if (deleted == 0)
        {
            FlashStatus("This track is locked", error: true);
            return;
        }

        _preview.Refresh();
        SetStatus(deleted == 1
            ? "Clip deleted and gap closed"
            : $"{deleted} clips deleted and gaps closed", ReadyDotBrush);
    }

    /// <summary>Moves the selected clip by frames (Alt+arrows) or a second (with Shift).</summary>
    private void NudgeSelectedClip(int direction, bool big)
    {
        var clips = _project.SelectedClips()
            .Where(c => _project.TrackOf(c) is { IsLocked: false })
            .ToList();
        if (clips.Count == 0)
        {
            FlashStatus("Select an unlocked clip first");
            return;
        }

        // Holding the key repeats: keep one undo entry for the whole nudge.
        var now = DateTime.UtcNow;
        var sameGesture = _nudging ||
                          (_nudgeClipId == _project.SelectedClipId &&
                           (now - _nudgeStamp).TotalMilliseconds < 900);
        if (!sameGesture) _project.BeginEdit();
        _nudgeClipId = _project.SelectedClipId;
        _nudgeStamp = now;

        var step = big ? 1.0 : _project.FrameDuration;
        var delta = direction * step;

        _nudging = true;
        try
        {
            _project.NudgeSelectedClips(delta);
        }
        finally
        {
            _nudging = false;
        }

        _preview.Refresh();
        Timeline.ScrollToPlayhead();
    }

    /// <summary>Jumps the playhead to the previous/next clip edge on any layer.</summary>
    private void JumpToEdit(int direction)
    {
        if (_project.FindEditPoint(_project.Playhead, direction) is not { } time) return;
        _project.Playhead = _project.SnapToFrame(time);
        Timeline.ScrollToPlayhead();
    }

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        var target = FindSplitTarget();
        if (target == null)
        {
            FlashStatus("Place the playhead inside a clip", error: true);
            return;
        }

        var track = _project.TrackOf(target);
        if (track?.IsLocked == true)
        {
            FlashStatus("This track is locked", error: true);
            return;
        }

        _project.BeginEdit();
        _project.SplitClip(target, _project.Playhead);
        _preview.Refresh();
        SetStatus("Clip split", ReadyDotBrush);
    }

    private TimelineClip? FindSplitTarget()
    {
        double min = _project.FrameDuration * 0.5;
        var selected = _project.SelectedClip;
        if (selected != null &&
            _project.Playhead > selected.StartSeconds + min &&
            _project.Playhead < selected.EndSeconds - min)
            return selected;

        var preview = _project.GetPreviewClipAt(_project.Playhead);
        if (preview != null &&
            _project.Playhead > preview.StartSeconds + min &&
            _project.Playhead < preview.EndSeconds - min)
            return preview;

        return null;
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var clip = _project.SelectedClip;
        if (clip == null)
        {
            FlashStatus("Select a clip first");
            return;
        }

        var track = _project.TrackOf(clip);
        if (track?.IsLocked == true)
        {
            FlashStatus("This track is locked", error: true);
            return;
        }

        _project.BeginEdit();
        _project.DuplicateClip(clip);
        _preview.Refresh();
        SetStatus("Clip duplicated", ReadyDotBrush);
    }

    private void CopyClips()
    {
        var count = _project.CopySelectedClips();
        if (count == 0)
        {
            FlashStatus("Select a clip first");
            return;
        }

        SetStatus(count == 1 ? "Clip copied" : $"{count} clips copied", ReadyDotBrush);
    }

    private void PasteClips()
    {
        if (!_project.CanPasteClips)
        {
            FlashStatus("Nothing to paste");
            return;
        }

        _project.BeginEdit();
        var count = _project.PasteClips(_project.Playhead);
        if (count == 0)
        {
            FlashStatus("Could not paste here", error: true);
            return;
        }

        _preview.Refresh();
        Timeline.ScrollToPlayhead();
        SetStatus(count == 1 ? "Clip pasted" : $"{count} clips pasted", ReadyDotBrush);
    }

    private void SplitAllAtPlayhead()
    {
        if (!_project.HasClipAtPlayhead())
        {
            FlashStatus("Place the playhead inside a clip", error: true);
            return;
        }

        _project.BeginEdit();
        var count = _project.SplitAllAtPlayhead();
        _preview.Refresh();
        SetStatus(count == 1 ? "Clip split" : $"{count} clips split", ReadyDotBrush);
    }

    private void ZoomSelection()
    {
        Timeline.ZoomToSelection();
        ZoomText.Text = $"{Timeline.PixelsPerSecond:0} px/s";
    }

    private void CopyClips_Click(object sender, RoutedEventArgs e) => CopyClips();

    private void PasteClips_Click(object sender, RoutedEventArgs e) => PasteClips();

    private void SplitAll_Click(object sender, RoutedEventArgs e) => SplitAllAtPlayhead();

    private void ZoomSelection_Click(object sender, RoutedEventArgs e) => ZoomSelection();

    private void AddVideoTrack_Click(object sender, RoutedEventArgs e)
    {
        _project.BeginEdit();
        var track = _project.AddTrack(TrackKind.Video);
        SetStatus($"Added {track.Name}", ReadyDotBrush);
    }

    private void AddAudioTrack_Click(object sender, RoutedEventArgs e)
    {
        _project.BeginEdit();
        var track = _project.AddTrack(TrackKind.Audio);
        SetStatus($"Added {track.Name}", ReadyDotBrush);
    }

    private void Timeline_RemoveTrackRequested(object? sender, TimelineTrack track)
    {
        if (!_project.Tracks.Contains(track)) return;

        if (track.Clips.Count > 0)
        {
            var result = MessageDialog.Show(this, "Remove layer",
                $"Remove “{track.Name}” and the {track.Clips.Count} clip{(track.Clips.Count == 1 ? "" : "s")} on it?\n\nYou can undo this.",
                "Remove", null, "Cancel");
            if (result != MessageResult.Primary) return;
        }

        _project.BeginEdit();
        _project.RemoveTrack(track);
        _preview.Refresh();
        SetStatus($"Removed {track.Name}", ReadyDotBrush);
    }

    private void Timeline_RenameTrackRequested(object? sender, TimelineTrack track)
    {
        if (!_project.Tracks.Contains(track)) return;

        var dialog = new RenameTrackDialog(track.Name) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var name = dialog.TrackName.Trim();
        if (name.Length == 0 || name == track.Name) return;

        _project.BeginEdit();
        track.Name = name;
        _project.NotifyTimelineChanged();
        SetStatus($"Renamed layer to {name}", ReadyDotBrush);
    }

    private void RazorToggle_Click(object sender, RoutedEventArgs e)
    {
        Timeline.RazorMode = RazorToggle.IsChecked == true;
    }

    private void SnapToggle_Click(object sender, RoutedEventArgs e)
    {
        Timeline.SnapEnabled = SnapToggle.IsChecked == true;
    }

    private void SnapMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item)
        {
            SnapToggle.IsChecked = item.IsChecked;
            Timeline.SnapEnabled = item.IsChecked;
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        Timeline.ZoomIn();
        ZoomText.Text = $"{Timeline.PixelsPerSecond:0} px/s";
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        Timeline.ZoomOut();
        ZoomText.Text = $"{Timeline.PixelsPerSecond:0} px/s";
    }

    private void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        if (_project.ContentDuration > 0.2) Timeline.FitToView();
        else Timeline.ResetZoom();
        ZoomText.Text = $"{Timeline.PixelsPerSecond:0} px/s";
    }

    private void ResetTimelineView()
    {
        if (_project.ContentDuration > 0.2) Timeline.FitToView();
        else Timeline.ResetZoom();
        ZoomText.Text = $"{Timeline.PixelsPerSecond:0} px/s";
    }

    // ================================================================ transport

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        _preview.TogglePlay();
        Timeline.ScrollToPlayhead();
    }

    private void SeekStart_Click(object sender, RoutedEventArgs e)
    {
        _preview.SeekToStart();
        Timeline.ScrollToPlayhead();
    }

    private void SeekEnd_Click(object sender, RoutedEventArgs e)
    {
        _preview.SeekToEnd();
        Timeline.ScrollToPlayhead();
    }

    private void PrevFrame_Click(object sender, RoutedEventArgs e)
    {
        _preview.StepFrames(-1);
        Timeline.ScrollToPlayhead();
    }

    private void NextFrame_Click(object sender, RoutedEventArgs e)
    {
        _preview.StepFrames(1);
        Timeline.ScrollToPlayhead();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_preview != null) _preview.SetBaseVolume(e.NewValue);
        if (_settings != null) _settings.PreviewVolume = e.NewValue;
    }

    // ================================================================ chrome/menus

    private void OpenMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu == null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    private void TitleBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (IsInsideButton(source)) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        try { DragMove(); } catch { }
    }

    private static bool IsInsideButton(DependencyObject node)
    {
        var current = node;
        while (current != null)
        {
            if (current is ButtonBase) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;    private void WindowMaximizeRestore_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            _rememberMaximized = true;
            MaxRestoreGlyph.Text = "\uE923";
        }
        else if (WindowState == WindowState.Normal)
        {
            _rememberMaximized = false;
            MaxRestoreGlyph.Text = "\uE922";
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _project.ImageDurationSeconds = _settings.ImageDuration;
        _preview.SetBaseVolume(_settings.PreviewVolume);
        VolumeSlider.Value = _settings.PreviewVolume;
        SetStatus("Settings saved", ReadyDotBrush);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_project.ContentDuration < 0.05)
        {
            FlashStatus("Add clips before exporting", error: true);
            return;
        }

        _preview.Pause();
        var dialog = new ExportDialog(_project, _settings) { Owner = this };
        dialog.ShowDialog();

        if (dialog.Exported && !string.IsNullOrWhiteSpace(dialog.ExportedPath))
            SetStatus($"Exported {Path.GetFileName(dialog.ExportedPath)}", ReadyDotBrush);
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e)
    {
        MessageDialog.ShowInfo(this, "Keyboard shortcuts",
            "Timeline\n" +
            "  Space            Play / pause\n" +
            "  ← / →            Step one frame (Shift: one second)\n" +
            "  Home / End       Go to start / end\n" +
            "  S                Split at playhead\n" +
            "  Shift+S          Split every layer at the playhead\n" +
            "  Ctrl+A           Select all clips\n" +
            "  Ctrl+C / Ctrl+V  Copy / paste clips at the playhead\n" +
            "  Ctrl+D           Duplicate selected clip\n" +
            "  Delete           Delete selected clip\n" +
            "  Shift+Delete     Delete and close the gap\n" +
            "  Z                Zoom to the selection\n" +
            "  Alt+← / →        Nudge selected clip (Shift: one second)\n" +
            "  , / .            Jump to previous / next cut\n" +
            "  Drag empty space Select the export range\n" +
            "  Double-click clip Set the export range to that clip\n" +
            "  Wheel            Zoom timeline at the pointer\n" +
            "  Shift+wheel      Scroll timeline\n" +
            "  Alt+wheel        Scroll layers\n" +
            "  Right-click layer  Rename / remove layer\n" +
            "  Drag right edge  Trim; past the source end loops it\n" +
            "  Drag top corner  Set the fade in / fade out length\n" +
            "  Ctrl+click       Add / remove a clip from the selection\n" +
            "  Shift+click      Select a run of clips on one layer\n" +
            "  Ctrl+drag clip   Duplicate while dragging\n" +
            "  Ctrl+drag right  Time-stretch (slow / fast motion)\n\n" +
            "Project\n" +
            "  Ctrl+N           New project\n" +
            "  Ctrl+O           Open project\n" +
            "  Ctrl+S           Save\n" +
            "  Ctrl+Shift+S     Save as\n" +
            "  Ctrl+I           Import media\n" +
            "  Ctrl+E           Export video\n" +
            "  Ctrl+Z / Ctrl+Y  Undo / redo");
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageDialog.ShowInfo(this, "About YorteVegas",
            "YorteVegas 0.1\n\nA focused desktop video editor for Windows.\n" +
            "Timeline editing with mpv-powered preview.\n\n" +
            "Export and effects are planned for later versions.");
    }

    // ================================================================ window events

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WindowProc);
    }

    /// <summary>
    /// A borderless window maximizes over the taskbar. Clamp the maximized
    /// rectangle to the work area of the monitor the window is on.
    /// </summary>
    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg != WM_GETMINMAXINFO || lParam == IntPtr.Zero) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
        Marshal.StructureToPtr(mmi, lParam, true);
        return IntPtr.Zero;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Timeline.Focus();
        ZoomText.Text = $"{Timeline.PixelsPerSecond:0} px/s";

        if (!string.IsNullOrWhiteSpace(_startupProject) && File.Exists(_startupProject))
        {
            TryOpenProject(_startupProject);
        }
        else if (!TryRecoverAutosave())
        {
            if (_settings.OpenLastProject &&
                !string.IsNullOrWhiteSpace(_settings.LastProjectPath) &&
                File.Exists(_settings.LastProjectPath))
            {
                TryOpenProject(_settings.LastProjectPath);
            }
            else if (_settings.OpenLastProject && !string.IsNullOrWhiteSpace(_settings.LastProjectPath))
            {
                SetStatus("Last project not found — started a new one", WarnDotBrush);
            }
        }

        if (_startupImports is { Count: > 0 })
            _ = ImportFilesAsync(_startupImports);
        else
            Timeline.Focus();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_forceClose && _dirty)
        {
            if (AskSaveChanges("closing") != true)
            {
                e.Cancel = true;
                return;
            }
        }

        _forceClose = true;
        AutosaveService.Clear();
        _preview.Dispose();

        _settings.PreviewVolume = VolumeSlider.Value;
        _settings.LastProjectPath = _currentPath ?? _settings.LastProjectPath;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }
        _settings.WindowMaximized = _rememberMaximized;
        _settings.Save();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var textFocused = Keyboard.FocusedElement is TextBoxBase;

        if (textFocused && !(ctrl && key is Key.S or Key.O or Key.N or Key.I or Key.E))
            return;

        if (key == Key.Enter && MediaList.IsKeyboardFocusWithin)
        {
            AddSelectedAssetsToTimeline();
            e.Handled = true;
            return;
        }

        if (ctrl)
        {
            switch (key)
            {
                case Key.N: NewProject_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.O: OpenProject_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.S:
                    if (shift) SaveAs_Click(this, new RoutedEventArgs());
                    else SaveProject();
                    e.Handled = true;
                    return;
                case Key.I: ImportMedia_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.E: Export_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.Z:
                    if (shift) Redo_Click(this, new RoutedEventArgs());
                    else Undo_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.Y: Redo_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.A:
                    if (MediaList.IsKeyboardFocusWithin) MediaList.SelectAll();
                    else _project.SelectAllClips();
                    e.Handled = true;
                    return;
                case Key.C: CopyClips(); e.Handled = true; return;
                case Key.V: PasteClips(); e.Handled = true; return;
                case Key.D: Duplicate_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.K: Split_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.OemPlus:
                case Key.Add:
                    ZoomIn_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.OemMinus:
                case Key.Subtract:
                    ZoomOut_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
            }
            return;
        }

        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (alt && key is Key.Left or Key.Right)
        {
            NudgeSelectedClip(key == Key.Left ? -1 : 1, shift);
            e.Handled = true;
            return;
        }

        switch (key)
        {
            case Key.Space:
                _preview.TogglePlay();
                Timeline.ScrollToPlayhead();
                e.Handled = true;
                break;
            case Key.Delete:
                if (shift) DeleteAndCloseGap();
                else Delete_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.OemComma:
                JumpToEdit(-1);
                e.Handled = true;
                break;
            case Key.OemPeriod:
                JumpToEdit(1);
                e.Handled = true;
                break;
            case Key.S:
                if (shift) SplitAllAtPlayhead();
                else Split_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Z:
                ZoomSelection();
                e.Handled = true;
                break;
            case Key.Left:
                _preview.StepFrames(shift ? -(int)Math.Round(_project.Fps) : -1);
                Timeline.ScrollToPlayhead();
                e.Handled = true;
                break;
            case Key.Right:
                _preview.StepFrames(shift ? (int)Math.Round(_project.Fps) : 1);
                Timeline.ScrollToPlayhead();
                e.Handled = true;
                break;
            case Key.Home:
                _preview.SeekToStart();
                Timeline.ScrollToPlayhead();
                e.Handled = true;
                break;
            case Key.End:
                _preview.SeekToEnd();
                Timeline.ScrollToPlayhead();
                e.Handled = true;
                break;
            case Key.F:
                ZoomFit_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Escape:
                _preview.Pause();
                _project.SelectedClipId = null;
                e.Handled = true;
                break;
        }
    }

    private void RestoreWindowBounds()
    {
        if (_settings.WindowWidth is > 500 and var width && _settings.WindowHeight is > 400 and var height)
        {
            Width = width;
            Height = height;
        }

        if (_settings.WindowLeft is double left && _settings.WindowTop is double top)
        {
            Left = left;
            Top = top;
        }

        var work = SystemParameters.WorkArea;
        if (Left < work.Left - 20 || Top < work.Top - 20 ||
            Left + Width > work.Right + 40 || Top + Height > work.Bottom + 40)
        {
            Left = work.Left + (work.Width - Width) / 2;
            Top = work.Top + (work.Height - Height) / 2;
        }

        _rememberMaximized = _settings.WindowMaximized;
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    // ================================================================ helpers

    private static string AspectLabel(int width, int height)
    {
        if (width <= 0 || height <= 0) return "—";
        int a = width, b = height;
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }
        return $"{width / a}:{height / a}";
    }

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    // ------------------------------------------------------------ work area hook

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
