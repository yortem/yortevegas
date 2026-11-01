using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly DispatcherTimer _previewTimer;

    /// <summary>Raised after any live edit to the current text style.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the composer asks for a new text clip.</summary>
    public event Action<TextOverlay>? AddRequested;

    /// <summary>Raised when the selected text clip should be removed.</summary>
    public event Action<TimelineClip>? DeleteRequested;

    public TextPanel()
    {
        InitializeComponent();

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            RenderPreview();
        };
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
        RenderPreview();
    }

    private void UpdateHeader()
    {
        ClipNameText.Text = _clip?.Name ?? "New text";
        ModeText.Text = _clip != null ? "EDITING CLIP" : "NEW TEXT";
        RemoveButton.Visibility = _clip != null ? Visibility.Visible : Visibility.Collapsed;
        AddButton.Content = _clip != null ? "Add another" : "Add to timeline";
        DurationText.Text = _clip != null
            ? $"{_clip.DurationSeconds:0.0} s on the timeline"
            : "Will be 4.0 s on the timeline";
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
        RenderPreview();
        CommitDraft();
    }

    // ------------------------------------------------------------ parameters

    private void RebuildParams()
    {
        ParamHost.Children.Clear();
        if (_draft == null) return;

        _building = true;
        try
        {
            if (_clip != null)
            {
                AddSlider(ParamHost, "Duration", 0.5, 30, 0.1,
                    _clip.DurationSeconds, "0.0", value =>
                    {
                        _clip.DurationSeconds = Math.Max(0.1, value);
                        DurationText.Text = $"{_clip.DurationSeconds:0.0} s on the timeline";
                    }, " s");
            }

            AddFontFamily();
            AddSlider(ParamHost, "Font size", 12, 220, 1, _draft.FontSize, "0",
                value => _draft.FontSize = value);

            AddChoices(ParamHost, "Weight", new[]
            {
                ("Light", 300d), ("Regular", 400d), ("SemiBold", 600d),
                ("Bold", 700d), ("Black", 900d)
            }, _draft.Weight, value => _draft.Weight = (int)Math.Round(value));

            AddToggle(ParamHost, "Italic", _draft.Italic, value => _draft.Italic = value);

            AddColorSwatches(ParamHost, "Color", _draft.Color,
                value => _draft.Color = value);

            AddChoices(ParamHost, "Alignment", new[]
            {
                ("Left", (double)TextAlign.Left),
                ("Center", (double)TextAlign.Center),
                ("Right", (double)TextAlign.Right)
            }, (double)_draft.Align, value => _draft.Align = (TextAlign)(int)Math.Round(value));

            AddChoices(ParamHost, "Direction", new[]
            {
                ("Auto", (double)TextDirection.Auto),
                ("LTR", (double)TextDirection.Ltr),
                ("RTL", (double)TextDirection.Rtl)
            }, (double)_draft.Direction, value =>
            {
                _draft.Direction = (TextDirection)(int)Math.Round(value);
                UpdateInputDirection();
                RebuildPresets();
            });

            AddSlider(ParamHost, "Position X", 0, 1, 0.01, _draft.PositionX, "0.00",
                value => _draft.PositionX = value);
            AddSlider(ParamHost, "Position Y", 0, 1, 0.01, _draft.PositionY, "0.00",
                value => _draft.PositionY = value);
            AddSlider(ParamHost, "Max width", 0.2, 1, 0.01, _draft.MaxWidth, "0.00",
                value => _draft.MaxWidth = value);

            AddToggle(ParamHost, "Shadow", _draft.Shadow,
                value => _draft.Shadow = value, RebuildParams);
            if (_draft.Shadow)
            {
                AddSlider(ParamHost, "Shadow blur", 0, 40, 0.5, _draft.ShadowBlur, "0.0",
                    value => _draft.ShadowBlur = value);
                AddSlider(ParamHost, "Shadow opacity", 0, 1, 0.01, _draft.ShadowOpacity, "0.00",
                    value => _draft.ShadowOpacity = value);
                AddSlider(ParamHost, "Shadow depth", 0, 30, 0.5, _draft.ShadowDepth, "0.0",
                    value => _draft.ShadowDepth = value);
            }

            AddToggle(ParamHost, "Outline", _draft.Outline,
                value => _draft.Outline = value, RebuildParams);
            if (_draft.Outline)
            {
                AddSlider(ParamHost, "Outline width", 0.5, 12, 0.1,
                    _draft.OutlineThickness, "0.0", value => _draft.OutlineThickness = value);
                AddColorSwatches(ParamHost, "Outline color", _draft.OutlineColor,
                    value => _draft.OutlineColor = value);
            }

            AddToggle(ParamHost, "Background bar", _draft.Background,
                value => _draft.Background = value, RebuildParams);
            if (_draft.Background)
            {
                AddSlider(ParamHost, "Bar opacity", 0, 1, 0.01,
                    _draft.BackgroundOpacity, "0.00", value => _draft.BackgroundOpacity = value);
                AddSlider(ParamHost, "Bar padding", 0, 60, 1,
                    _draft.BackgroundPadding, "0", value => _draft.BackgroundPadding = value);
                AddColorSwatches(ParamHost, "Bar color", _draft.BackgroundColor,
                    value => _draft.BackgroundColor = value);
            }

            AddChoices(ParamHost, "Animation", new[]
            {
                ("None", (double)TextAnimation.None),
                ("Fade", (double)TextAnimation.Fade),
                ("Slide up", (double)TextAnimation.SlideUp),
                ("Slide left", (double)TextAnimation.SlideLeft),
                ("Zoom", (double)TextAnimation.Zoom),
                ("Wipe", (double)TextAnimation.Wipe)
            }, (double)_draft.Animation,
                value => _draft.Animation = (TextAnimation)(int)Math.Round(value));

            if (_draft.Animation != TextAnimation.None)
            {
                AddSlider(ParamHost, "Animation time", 0.1, 3, 0.05,
                    _draft.AnimationSeconds, "0.00", value => _draft.AnimationSeconds = value, " s");
            }
        }
        finally
        {
            _building = false;
        }
    }

    private void AddFontFamily()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("ParamLabel"),
            Text = "Font",
            Margin = new Thickness(0, 0, 0, 4)
        });

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
            Margin = new Thickness(0, 0, 0, 0)
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is not string family) return;
            _draft.FontFamily = family;
            CommitDraft();
            SchedulePreview();
        };

        panel.Children.Add(combo);
        ParamHost.Children.Add(panel);
    }

    private void AddSlider(Panel host, string label, double min, double max, double step,
        double value, string format, Action<double> setter, string suffix = "")
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var valueText = new TextBlock
        {
            Style = (Style)FindResource("ParamValue"),
            Text = value.ToString(format, CultureInfo.InvariantCulture) + suffix
        };
        var labelText = new TextBlock
        {
            Style = (Style)FindResource("ParamLabel"),
            Text = label
        };

        Grid.SetColumn(valueText, 1);
        head.Children.Add(labelText);
        head.Children.Add(valueText);
        panel.Children.Add(head);

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
            Margin = new Thickness(0, 2, 0, 0)
        };

        slider.ValueChanged += (_, e) =>
        {
            if (_building) return;
            var newValue = Math.Round(e.NewValue / step) * step;
            setter(newValue);
            valueText.Text = newValue.ToString(format, CultureInfo.InvariantCulture) + suffix;
            CommitDraft();
            SchedulePreview();
        };

        panel.Children.Add(slider);

        var range = new Grid { Margin = new Thickness(2, 1, 2, 0) };
        range.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        range.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var minText = new TextBlock
        {
            Style = (Style)FindResource("ParamRange"),
            Text = min.ToString(format, CultureInfo.InvariantCulture)
        };
        var maxText = new TextBlock
        {
            Style = (Style)FindResource("ParamRange"),
            Text = max.ToString(format, CultureInfo.InvariantCulture),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(maxText, 1);
        range.Children.Add(minText);
        range.Children.Add(maxText);
        panel.Children.Add(range);

        host.Children.Add(panel);
    }

    private void AddChoices(Panel host, string label,
        IReadOnlyList<(string Label, double Value)> choices,
        double current, Action<double> setter)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
        panel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("ParamLabel"),
            Text = label,
            Margin = new Thickness(0, 0, 0, 4)
        });

        var wrap = new WrapPanel();
        var chips = new List<ToggleButton>();

        foreach (var choice in choices)
        {
            var chip = new ToggleButton
            {
                Style = (Style)FindResource("ChoiceChip"),
                Content = choice.Label,
                IsChecked = Math.Abs(choice.Value - current) < 0.001
            };
            chip.Click += (_, _) =>
            {
                if (_building) return;
                setter(choice.Value);
                foreach (var other in chips)
                    other.IsChecked = ReferenceEquals(other, chip);
                CommitDraft();
                SchedulePreview();
            };
            chips.Add(chip);
            wrap.Children.Add(chip);
        }

        panel.Children.Add(wrap);
        host.Children.Add(panel);
    }

    private void AddToggle(Panel host, string label, bool value,
        Action<bool> setter, Action? rebuild = null)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 9)
        };

        var labelText = new TextBlock
        {
            Style = (Style)FindResource("ParamLabel"),
            Text = label,
            Width = 120
        };

        var toggle = new ToggleButton
        {
            Style = (Style)FindResource("ChoiceChip"),
            Content = value ? "ON" : "OFF",
            IsChecked = value,
            Margin = new Thickness(0, 0, 0, 0)
        };
        toggle.Click += (_, _) =>
        {
            if (_building) return;
            var on = toggle.IsChecked == true;
            toggle.Content = on ? "ON" : "OFF";
            setter(on);
            CommitDraft();
            SchedulePreview();
            rebuild?.Invoke();
        };

        panel.Children.Add(labelText);
        panel.Children.Add(toggle);
        host.Children.Add(panel);
    }

    private void AddColorSwatches(Panel host, string label, string current,
        Action<string> setter)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("ParamLabel"),
            Text = label,
            Margin = new Thickness(0, 0, 0, 5)
        });

        var wrap = new WrapPanel();
        foreach (var hex in TextCatalog.Colors)
        {
            var selected = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
            var swatch = new Border
            {
                Width = 17,
                Height = 17,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 4, 4),
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
                SchedulePreview();
                RebuildParams();
            };
            wrap.Children.Add(swatch);
        }

        panel.Children.Add(wrap);
        host.Children.Add(panel);
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

    // --------------------------------------------------------------- preview

    private void RenderPreview()
    {
        if (_draft == null) return;

        PreviewImage.Source = TextOverlayRenderer.Render(_draft, 320, 180, true);

        var preset = TextCatalog.Get(_draft.Preset);
        if (StyleMatchesPreset(_draft, preset.Style))
        {
            PresetTitle.Text = preset.Name;
            PresetDescription.Text = preset.Description;
        }
        else
        {
            PresetTitle.Text = "Custom";
            PresetDescription.Text = "A preset with your own adjustments.";
        }

        DurationText.Text = _clip != null
            ? $"{_clip.DurationSeconds:0.0} s on the timeline"
            : "Will be 4.0 s on the timeline";
    }

    private void SchedulePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private static bool StyleMatchesPreset(TextOverlay text, TextOverlay preset) =>
        string.Equals(text.FontFamily, preset.FontFamily, StringComparison.OrdinalIgnoreCase) &&
        Math.Abs(text.FontSize - preset.FontSize) < 0.01 &&
        text.Weight == preset.Weight &&
        text.Italic == preset.Italic &&
        string.Equals(text.Color, preset.Color, StringComparison.OrdinalIgnoreCase) &&
        text.Align == preset.Align &&
        Math.Abs(text.PositionX - preset.PositionX) < 0.001 &&
        Math.Abs(text.PositionY - preset.PositionY) < 0.001 &&
        Math.Abs(text.MaxWidth - preset.MaxWidth) < 0.001 &&
        text.Shadow == preset.Shadow &&
        Math.Abs(text.ShadowBlur - preset.ShadowBlur) < 0.01 &&
        Math.Abs(text.ShadowOpacity - preset.ShadowOpacity) < 0.001 &&
        Math.Abs(text.ShadowDepth - preset.ShadowDepth) < 0.01 &&
        text.Outline == preset.Outline &&
        Math.Abs(text.OutlineThickness - preset.OutlineThickness) < 0.01 &&
        string.Equals(text.OutlineColor, preset.OutlineColor, StringComparison.OrdinalIgnoreCase) &&
        text.Background == preset.Background &&
        Math.Abs(text.BackgroundOpacity - preset.BackgroundOpacity) < 0.001 &&
        Math.Abs(text.BackgroundPadding - preset.BackgroundPadding) < 0.01 &&
        string.Equals(text.BackgroundColor, preset.BackgroundColor, StringComparison.OrdinalIgnoreCase) &&
        text.Animation == preset.Animation &&
        Math.Abs(text.AnimationSeconds - preset.AnimationSeconds) < 0.01;

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
        SchedulePreview();
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
