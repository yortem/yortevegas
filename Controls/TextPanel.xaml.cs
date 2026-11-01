using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using YorteVegas.Models;
using YorteVegas.Services;
using YorteVegas.ViewModels;

namespace YorteVegas.Controls;

/// <summary>
/// Text composer and inspector. With a text clip selected it edits that clip
/// live; otherwise it is a composer for a new text event at the playhead.
/// Presets fill the whole style, and every value can still be adjusted.
/// </summary>
public partial class TextPanel : UserControl
{
    private EditProject? _project;
    private TimelineClip? _clip;
    private TextOverlay _draft = TextCatalog.CreateDefault("Your text");
    private bool _building;

    /// <summary>Raised after any live edit to the current text style.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the composer asks for a new text clip.</summary>
    public event Action<TextOverlay>? AddRequested;

    /// <summary>Raised when the selected text clip should be removed.</summary>
    public event Action<TimelineClip>? DeleteRequested;

    public TextPanel()
    {
        InitializeComponent();
    }

    public TimelineClip? EditedClip => _clip;

    public TextOverlay CurrentText => _draft;

    public void Attach(EditProject? project, TimelineClip? clip)
    {
        _project = project;
        var textClip = clip != null && clip.Kind == MediaKind.Text && clip.Text != null
            ? clip
            : null;

        if (ReferenceEquals(textClip, _clip) && textClip != null)
        {
            UpdateHeader();
            return;
        }

        _clip = textClip;
        _draft = textClip?.Text ?? TextCatalog.CreateDefault("Your text");

        _building = true;
        TextEditor.Text = _draft.Text;
        _building = false;
        UpdateInputDirection();

        UpdateHeader();
        RebuildPresets();
        RebuildParams();
    }

    private void UpdateHeader()
    {
        ClipNameText.Text = _clip?.Name ?? "New text";
        ModeText.Text = _clip != null ? "EDITING CLIP" : "NEW TEXT";
        RemoveButton.Visibility = _clip != null ? Visibility.Visible : Visibility.Collapsed;
        AddButton.Content = _clip != null ? "Add another" : "Add to timeline";
        DurationText.Text = _clip != null
            ? $"{_clip.DurationSeconds:0.0} s"
            : "4.0 s default";
    }

    // ------------------------------------------------------------- presets

    private void RebuildPresets()
    {
        PresetHost.Children.Clear();

        foreach (var preset in TextCatalog.All)
        {
            var sample = preset.Style.Clone();
            sample.Text = TextOverlayRenderer.IsRtl(_draft) ? "אב" : "Aa";
            sample.Direction = _draft.Direction;
            sample.Preset = preset.Name;
            // The tile is tiny compared with a 1080p frame; scale the sample
            // text up so the design is readable at thumbnail size.
            sample.FontSize = Math.Min(400, sample.FontSize * 3.5);
            sample.MaxWidth = Math.Min(1, sample.MaxWidth * 1.5);
            var thumbnail = TextOverlayRenderer.Render(sample, 112, 63, true);

            var row = new Border
            {
                Style = (Style)FindResource("PresetRow"),
                Tag = string.Equals(_draft.Preset, preset.Name,
                    StringComparison.OrdinalIgnoreCase) ? "selected" : null,
                ToolTip = preset.Description
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var frame = new Border
            {
                Width = 56,
                Height = 32,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromRgb(0x05, 0x07, 0x0A)),
                ClipToBounds = true,
                Margin = new Thickness(0, 0, 7, 0)
            };
            frame.Child = new Image
            {
                Source = thumbnail,
                Width = 56,
                Height = 32,
                Stretch = Stretch.UniformToFill
            };

            var name = new TextBlock
            {
                Text = preset.Name,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC7, 0xD6, 0xE6)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            Grid.SetColumn(frame, 0);
            Grid.SetColumn(name, 1);
            grid.Children.Add(frame);
            grid.Children.Add(name);
            row.Child = grid;

            var presetName = preset.Name;
            row.MouseLeftButtonDown += (_, e) =>
            {
                ApplyPreset(presetName);
                e.Handled = true;
            };

            // Drag a preset straight onto the timeline to create a text event.
            var start = new Point();
            var dragging = false;
            row.PreviewMouseLeftButtonDown += (_, e) =>
            {
                start = e.GetPosition(row);
                dragging = false;
            };
            row.MouseMove += (_, e) =>
            {
                if (dragging || e.LeftButton != MouseButtonState.Pressed) return;
                var delta = e.GetPosition(row) - start;
                if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;

                dragging = true;
                DragDrop.DoDragDrop(row,
                    new DataObject(TextDragFormat, new TextDropData(presetName, _draft.Text)),
                    DragDropEffects.Copy);
            };
            row.MouseLeftButtonUp += (_, e) =>
            {
                if (dragging)
                {
                    dragging = false;
                    e.Handled = true;
                }
            };

            PresetHost.Children.Add(row);
        }
    }

    /// <summary>Data format used when a text preset is dragged onto the timeline.</summary>
    public const string TextDragFormat = "YorteVegas.TextPreset";

    private void ApplyPreset(string presetName)
    {
        var style = TextCatalog.Create(presetName, _draft.Text);
        _draft = style;
        if (_clip != null) _clip.Text = style;

        _building = true;
        TextEditor.Text = style.Text;
        _building = false;
        UpdateInputDirection();

        RebuildPresets();
        RebuildParams();
        CommitDraft();
    }

    // ------------------------------------------------------------ parameters

    private void RebuildParams()
    {
        ParamLeft.Children.Clear();
        ParamRight.Children.Clear();
        if (_draft == null) return;

        _building = true;
        try
        {
            AddSection(ParamLeft, "Font");
            AddFontFamily(ParamLeft);
            AddSlider(ParamLeft, "Size", 12, 220, 1, _draft.FontSize, "0",
                value => _draft.FontSize = value);
            AddWeight(ParamLeft);

            AddSection(ParamLeft, "Style");
            AddStyleRow(ParamLeft);
            AddDirection(ParamLeft);
            AddColorSwatches(ParamLeft, "Color", _draft.Color, value => _draft.Color = value);

            AddSection(ParamLeft, "Position");
            AddSlider(ParamLeft, "X", 0, 1, 0.01, _draft.PositionX, "0.00",
                value => _draft.PositionX = value);
            AddSlider(ParamLeft, "Y", 0, 1, 0.01, _draft.PositionY, "0.00",
                value => _draft.PositionY = value);
            AddSlider(ParamLeft, "Width", 0.2, 1, 0.01, _draft.MaxWidth, "0.00",
                value => _draft.MaxWidth = value);

            AddSection(ParamRight, "Clip");
            if (_clip != null)
            {
                AddSlider(ParamRight, "Duration", 0.5, 30, 0.1, _clip.DurationSeconds, "0.0",
                    value =>
                    {
                        _clip.DurationSeconds = Math.Max(0.1, value);
                        DurationText.Text = $"{_clip.DurationSeconds:0.0} s";
                    }, " s");
            }
            else
            {
                AddInfo(ParamRight, "4.0 s when added");
            }

            AddSectionToggle(ParamRight, "Shadow", _draft.Shadow,
                value => _draft.Shadow = value, RebuildParams);
            if (_draft.Shadow)
            {
                AddSlider(ParamRight, "Blur", 0, 40, 0.5, _draft.ShadowBlur, "0.0",
                    value => _draft.ShadowBlur = value);
                AddSlider(ParamRight, "Opacity", 0, 1, 0.01, _draft.ShadowOpacity, "0.00",
                    value => _draft.ShadowOpacity = value);
                AddSlider(ParamRight, "Depth", 0, 30, 0.5, _draft.ShadowDepth, "0.0",
                    value => _draft.ShadowDepth = value);
            }

            AddSectionToggle(ParamRight, "Outline", _draft.Outline,
                value => _draft.Outline = value, RebuildParams);
            if (_draft.Outline)
            {
                AddSlider(ParamRight, "Width", 0.5, 12, 0.1,
                    _draft.OutlineThickness, "0.0", value => _draft.OutlineThickness = value);
                AddColorSwatches(ParamRight, "Color", _draft.OutlineColor,
                    value => _draft.OutlineColor = value);
            }

            AddSectionToggle(ParamRight, "Bar", _draft.Background,
                value => _draft.Background = value, RebuildParams);
            if (_draft.Background)
            {
                AddSlider(ParamRight, "Opacity", 0, 1, 0.01,
                    _draft.BackgroundOpacity, "0.00", value => _draft.BackgroundOpacity = value);
                AddSlider(ParamRight, "Pad", 0, 60, 1,
                    _draft.BackgroundPadding, "0", value => _draft.BackgroundPadding = value);
                AddColorSwatches(ParamRight, "Color", _draft.BackgroundColor,
                    value => _draft.BackgroundColor = value);
            }

            AddSection(ParamRight, "Animation");
            AddAnimation(ParamRight);
            AddSlider(ParamRight, "Time", 0.1, 3, 0.05,
                _draft.AnimationSeconds, "0.00", value => _draft.AnimationSeconds = value, " s");
        }
        finally
        {
            _building = false;
        }
    }

    private void AddSection(Panel host, string title)
    {
        host.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            FontSize = 8.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x6B, 0x7E)),
            Margin = new Thickness(0, 8, 0, 5)
        });
    }

    private void AddSectionToggle(Panel host, string title, bool value,
        Action<bool> setter, Action? rebuild = null)
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = title.ToUpperInvariant(),
            FontSize = 8.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x6B, 0x7E)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var toggle = new ToggleButton
        {
            Style = (Style)FindResource("ChoiceChip"),
            Content = value ? "ON" : "OFF",
            FontSize = 8.5,
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(0),
            IsChecked = value
        };
        toggle.Click += (_, _) =>
        {
            if (_building) return;
            var on = toggle.IsChecked == true;
            toggle.Content = on ? "ON" : "OFF";
            setter(on);
            CommitDraft();
            rebuild?.Invoke();
        };

        Grid.SetColumn(toggle, 1);
        row.Children.Add(label);
        row.Children.Add(toggle);
        host.Children.Add(row);
    }

    private void AddInfo(Panel host, string text)
    {
        host.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 9.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x6B, 0x7E)),
            Margin = new Thickness(66, 0, 0, 6)
        });
    }

    private void AddFontFamily(Panel host)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Style = (Style)FindResource("ParamLabel"),
            Text = "Font",
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        };

        var fonts = TextCatalog.FontFamilies.ToList();
        if (!fonts.Any(f => string.Equals(f, _draft.FontFamily,
                StringComparison.OrdinalIgnoreCase)))
        {
            fonts.Insert(0, _draft.FontFamily);
        }

        var combo = new ComboBox
        {
            ItemsSource = fonts,
            SelectedItem = _draft.FontFamily,
            FontSize = 10,
            Height = 24,
            Padding = new Thickness(4, 0, 0, 0),
            Margin = new Thickness(0)
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is not string family) return;
            _draft.FontFamily = family;
            CommitDraft();
        };

        Grid.SetColumn(combo, 1);
        row.Children.Add(label);
        row.Children.Add(combo);
        host.Children.Add(row);
    }

    private void AddWeight(Panel host)
    {
        var row = CreateLabelRow("Weight", out var label);
        var labels = new List<string> { "Light", "Regular", "SemiBold", "Bold", "Black" };
        var values = new[] { 300, 400, 600, 700, 900 };

        var selected = Array.IndexOf(values, _draft.Weight);
        if (selected < 0) selected = 1;

        var combo = new ComboBox
        {
            ItemsSource = labels,
            SelectedIndex = selected,
            FontSize = 10,
            Height = 24,
            Padding = new Thickness(4, 0, 0, 0),
            Margin = new Thickness(0)
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            var index = combo.SelectedIndex;
            if (index < 0 || index >= values.Length) return;
            _draft.Weight = values[index];
            CommitDraft();
        };

        Grid.SetColumn(combo, 1);
        row.Children.Add(combo);
        host.Children.Add(row);
    }

    private Grid CreateLabelRow(string text, out TextBlock label)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        label = new TextBlock
        {
            Style = (Style)FindResource("ParamLabel"),
            Text = text,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(label);
        return row;
    }

    private void AddSlider(Panel host, string label, double min, double max, double step,
        double value, string format, Action<double> setter, string suffix = "")
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });

        var labelText = new TextBlock
        {
            Style = (Style)FindResource("ParamLabel"),
            Text = label,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var slider = new Slider
        {
            Style = (Style)FindResource("GradientSlider"),
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            SmallChange = step,
            LargeChange = step * 10,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            Height = 20,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var valueText = new TextBlock
        {
            Style = (Style)FindResource("ParamValue"),
            Text = value.ToString(format, CultureInfo.InvariantCulture) + suffix,
            FontSize = 9.5,
            Width = 46,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        slider.ValueChanged += (_, e) =>
        {
            if (_building) return;
            var newValue = Math.Round(e.NewValue / step) * step;
            setter(newValue);
            valueText.Text = newValue.ToString(format, CultureInfo.InvariantCulture) + suffix;
            CommitDraft();
        };

        Grid.SetColumn(slider, 1);
        Grid.SetColumn(valueText, 2);
        row.Children.Add(labelText);
        row.Children.Add(slider);
        row.Children.Add(valueText);
        host.Children.Add(row);
    }

    private void AddStyleRow(Panel host)
    {
        var row = CreateLabelRow("Style", out _);

        var wrap = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var italic = new ToggleButton
        {
            Style = (Style)FindResource("ChoiceChip"),
            Content = "I",
            FontStyle = FontStyles.Italic,
            FontWeight = FontWeights.SemiBold,
            FontSize = 10,
            Width = 26,
            Height = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 6, 0),
            IsChecked = _draft.Italic,
            ToolTip = "Italic"
        };
        italic.Click += (_, _) =>
        {
            if (_building) return;
            _draft.Italic = italic.IsChecked == true;
            CommitDraft();
        };
        wrap.Children.Add(italic);

        var combo = new ComboBox
        {
            ItemsSource = new List<string> { "Left", "Center", "Right" },
            SelectedIndex = (int)_draft.Align,
            FontSize = 9.5,
            Height = 24,
            Width = 112,
            Padding = new Thickness(4, 0, 0, 0),
            Margin = new Thickness(0)
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            var index = combo.SelectedIndex;
            if (index < 0 || index > 2) return;
            _draft.Align = (TextAlign)index;
            CommitDraft();
        };
        wrap.Children.Add(combo);

        Grid.SetColumn(wrap, 1);
        row.Children.Add(wrap);
        host.Children.Add(row);
    }

    private void AddDirection(Panel host)
    {
        var row = CreateLabelRow("Dir", out _);
        var labels = new List<string> { "Auto", "LTR", "RTL" };
        var values = new[] { TextDirection.Auto, TextDirection.Ltr, TextDirection.Rtl };
        var selected = Array.IndexOf(values, _draft.Direction);
        if (selected < 0) selected = 0;

        var combo = new ComboBox
        {
            ItemsSource = labels,
            SelectedIndex = selected,
            FontSize = 9.5,
            Height = 24,
            Padding = new Thickness(4, 0, 0, 0),
            Margin = new Thickness(0)
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            var index = combo.SelectedIndex;
            if (index < 0 || index >= values.Length) return;
            _draft.Direction = values[index];
            UpdateInputDirection();
            RebuildPresets();
            CommitDraft();
        };

        Grid.SetColumn(combo, 1);
        row.Children.Add(combo);
        host.Children.Add(row);
    }

    private void AddAnimation(Panel host)
    {
        var row = CreateLabelRow("Anim", out _);
        var labels = new List<string>
        {
            "None", "Fade", "Slide up", "Slide left", "Zoom", "Wipe"
        };
        var values = new[]
        {
            TextAnimation.None, TextAnimation.Fade, TextAnimation.SlideUp,
            TextAnimation.SlideLeft, TextAnimation.Zoom, TextAnimation.Wipe
        };
        var selected = Array.IndexOf(values, _draft.Animation);
        if (selected < 0) selected = 0;

        var combo = new ComboBox
        {
            ItemsSource = labels,
            SelectedIndex = selected,
            FontSize = 9.5,
            Height = 24,
            Padding = new Thickness(4, 0, 0, 0),
            Margin = new Thickness(0)
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            var index = combo.SelectedIndex;
            if (index < 0 || index >= values.Length) return;
            _draft.Animation = values[index];
            CommitDraft();
        };

        Grid.SetColumn(combo, 1);
        row.Children.Add(combo);
        host.Children.Add(row);
    }

    private void AddColorSwatches(Panel host, string label, string current,
        Action<string> setter)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelText = new TextBlock
        {
            Style = (Style)FindResource("ParamLabel"),
            Text = label,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        };

        var wrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var hex in TextCatalog.Colors)
        {
            var selected = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
            var swatch = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 0, 4, 3),
                Background = BrushFromHex(hex),
                BorderBrush = selected
                    ? (Brush)FindResource("AccentBrush")
                    : BrushFromHex("#33465E"),
                BorderThickness = new Thickness(selected ? 2 : 1),
                Cursor = Cursors.Hand,
                ToolTip = hex
            };
            var color = hex;
            swatch.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                setter(color);
                CommitDraft();
                RebuildParams();
            };
            wrap.Children.Add(swatch);
        }

        Grid.SetColumn(wrap, 1);
        row.Children.Add(labelText);
        row.Children.Add(wrap);
        host.Children.Add(row);
    }
    private static Brush BrushFromHex(string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.White;
        }
    }

    // ---------------------------------------------------------------- events

    private void TextEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_building) return;

        var wasRtl = TextOverlayRenderer.IsRtl(_draft);
        _draft.Text = TextEditor.Text;
        var isRtl = TextOverlayRenderer.IsRtl(_draft);
        if (wasRtl != isRtl) RebuildPresets();

        UpdateInputDirection();
        CommitDraft();
    }

    private void UpdateInputDirection()
    {
        if (_draft == null) return;
        var rtl = TextOverlayRenderer.IsRtl(_draft);
        TextEditor.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        AddRequested?.Invoke(_draft.Clone());
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_clip == null) return;
        DeleteRequested?.Invoke(_clip);
    }

    private void CommitDraft()
    {
        if (_clip == null) return;

        _clip.Text = _draft;
        _clip.Name = _draft.DisplayName;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
