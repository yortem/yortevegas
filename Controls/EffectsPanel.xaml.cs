using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using YorteVegas.Models;
using YorteVegas.Services;
using YorteVegas.ViewModels;

namespace YorteVegas.Controls;

/// <summary>
/// Vegas-style effects browser: the catalog on the left, preset thumbnails on
/// the right and the selected effect's parameters underneath. It replaces the
/// media library in the same column so the monitor stays visible.
/// </summary>
public partial class EffectsPanel : UserControl
{
    private EditProject? _project;
    private TimelineClip? _clip;
    private EffectKind _selectedKind = EffectKind.ColorBalance;
    private ClipEffect? _selected;
    private bool _building;

    private Border? _tintSwatch;
    private ClipEffect? _tintEffect;

    /// <summary>Raised after any change to the clip's effect stack or values.</summary>
    public event EventHandler? Changed;

    public EffectsPanel()
    {
        InitializeComponent();
        ContentGrid.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
    }

    public TimelineClip? EditedClip => _clip;

    public void Attach(EditProject? project, TimelineClip? clip)
    {
        _project = project;
        _clip = clip;
        _tintSwatch = null;
        _tintEffect = null;

        var hasClip = project != null && clip != null;
        ContentGrid.Visibility = hasClip ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasClip ? Visibility.Collapsed : Visibility.Visible;

        if (!hasClip)
        {
            ClipNameText.Text = "";
            EffectListHost.Children.Clear();
            PresetHost.Children.Clear();
            ParamHost.Children.Clear();
            return;
        }

        ClipNameText.Text = clip!.Name;

        foreach (var effect in clip.Effects)
        {
            if (effect.Kind == EffectKind.Crop && effect.Enabled)
                SyncCropEffect(effect);
        }

        _selected = clip.Effects.FirstOrDefault();
        _selectedKind = _selected?.Kind ?? EffectKind.ColorBalance;

        RebuildList();
        RebuildRight();
    }

    // ------------------------------------------------------------- effect list

    private void RebuildList()
    {
        EffectListHost.Children.Clear();
        if (_clip == null) return;

        foreach (var definition in EffectCatalog.All)
        {
            var instance = _clip.Effects.FirstOrDefault(e => e.Kind == definition.Kind);
            var applied = instance != null;
            var enabled = instance?.Enabled == true;
            var selected = definition.Kind == _selectedKind;

            var row = new Border
            {
                Style = (Style)FindResource("EffectRow"),
                Tag = selected ? "selected" : null,
                ToolTip = definition.Name + " — " + definition.Description
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var glyph = new TextBlock
            {
                Text = "fx",
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontStyle = FontStyles.Italic,
                FontWeight = FontWeights.Bold,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(selected
                    ? Color.FromRgb(0x7F, 0xC4, 0xFF)
                    : Color.FromRgb(0x4E, 0x60, 0x75)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0)
            };

            var name = new TextBlock
            {
                Text = definition.Name,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(applied && !enabled
                    ? Color.FromRgb(0x6E, 0x80, 0x95)
                    : selected
                        ? Color.FromRgb(0xE6, 0xEE, 0xF6)
                        : Color.FromRgb(0xB4, 0xC4, 0xD6)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            // The dot means "active on this clip"; a bypassed effect shows none.
            var dot = new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = new SolidColorBrush(Color.FromRgb(0x2E, 0x9B, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 2, 0),
                Visibility = enabled ? Visibility.Visible : Visibility.Collapsed
            };

            var remove = new Button
            {
                Content = "\u2715",
                Style = (Style)FindResource("RowRemoveButton"),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = applied ? Visibility.Visible : Visibility.Hidden,
                ToolTip = "Remove this effect from the clip"
            };

            if (instance != null)
            {
                var target = instance;
                remove.PreviewMouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    RemoveInstance(target);
                };
            }

            Grid.SetColumn(glyph, 0);
            Grid.SetColumn(name, 1);
            Grid.SetColumn(dot, 2);
            Grid.SetColumn(remove, 3);
            grid.Children.Add(glyph);
            grid.Children.Add(name);
            grid.Children.Add(dot);
            grid.Children.Add(remove);
            row.Child = grid;

            var kind = definition.Kind;
            row.MouseLeftButtonDown += (_, e) =>
            {
                _selectedKind = kind;
                _selected = _clip?.Effects.FirstOrDefault(x => x.Kind == kind);
                RebuildList();
                RebuildRight();
                e.Handled = true;
            };

            EffectListHost.Children.Add(row);
        }
    }

    // ------------------------------------------------------------ presets + UI

    private void RebuildRight()
    {
        PresetHost.Children.Clear();
        ParamHost.Children.Clear();
        _tintSwatch = null;
        _tintEffect = null;

        if (_clip == null) return;

        var definition = EffectCatalog.Get(_selectedKind);
        EffectTitle.Text = definition.Name;
        EffectDescription.Text = definition.Description;

        var applied = _selected != null && _selected.Kind == _selectedKind;
        AppliedControls.Visibility = applied ? Visibility.Visible : Visibility.Collapsed;
        OrderBadge.Visibility = applied ? Visibility.Visible : Visibility.Collapsed;

        if (applied)
        {
            _building = true;
            EnabledToggle.IsChecked = _selected!.Enabled;
            EnabledToggle.Content = _selected.Enabled ? "ON" : "OFF";
            _building = false;

            var index = _clip.Effects.IndexOf(_selected);
            MoveUpButton.IsEnabled = index > 0;
            MoveDownButton.IsEnabled = index >= 0 && index < _clip.Effects.Count - 1;
            OrderText.Text = $"{index + 1} / {_clip.Effects.Count}";
        }

        RebuildPresets(definition, applied ? _selected : null);

        if (!applied)
        {
            ParamHost.Children.Add(new TextBlock
            {
                Text = "Click a preset above to add this effect to the clip, " +
                       "or pick another effect on the left.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x6B, 0x7E)),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        var effect = _selected!;
        if (effect.Kind == EffectKind.ColorBalance)
            ParamHost.Children.Add(BuildTintPreview(effect));

        foreach (var parameter in definition.Parameters)
        {
            ParamHost.Children.Add(parameter.Kind == EffectParameterKind.Choice
                ? BuildChoices(effect, parameter)
                : BuildSlider(effect, parameter));
        }
    }

    private void RebuildPresets(EffectDefinition definition, ClipEffect? effect)
    {
        var defaults = definition.Parameters.ToDictionary(p => p.Key, p => p.Default);
        var hasDefaultPreset = definition.Presets.Any(p =>
            ValuesMatch(defaults, definition, p.Values));

        if (!hasDefaultPreset)
            AddPresetTile("(Default)", defaults, effect, definition);

        foreach (var preset in definition.Presets)
            AddPresetTile(preset.Name, preset.Values, effect, definition);
    }

    private void AddPresetTile(string name, IReadOnlyDictionary<string, double> values,
        ClipEffect? effect, EffectDefinition definition)
    {
        var selected = effect != null && ValuesMatch(effect, definition, values);
        var thumbnail = EffectThumbnailService.Render(definition.Kind, values);

        var tile = new Border
        {
            Style = (Style)FindResource("PresetTile"),
            Tag = selected ? "selected" : null,
            ToolTip = "Apply " + name
        };

        var panel = new StackPanel();
        var frame = new Border
        {
            Width = 124,
            Height = 70,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromRgb(0x05, 0x07, 0x0A)),
            ClipToBounds = true
        };
        frame.Child = new Image
        {
            Source = thumbnail,
            Stretch = Stretch.UniformToFill,
            Width = 124,
            Height = 70
        };
        panel.Children.Add(frame);
        panel.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 9.5,
            Foreground = new SolidColorBrush(selected
                ? Color.FromRgb(0xE6, 0xEE, 0xF6)
                : Color.FromRgb(0x8E, 0xA0, 0xB4)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
            MaxWidth = 122,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        tile.Child = panel;

        var start = new Point();
        var dragging = false;

        tile.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(tile);
            dragging = false;
        };

        tile.MouseMove += (_, e) =>
        {
            if (dragging || e.LeftButton != MouseButtonState.Pressed) return;

            var delta = e.GetPosition(tile) - start;
            if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;

            dragging = true;
            var payload = new EffectDropData(definition.Kind,
                new Dictionary<string, double>(values));
            DragDrop.DoDragDrop(tile, new DataObject(EffectDragFormat, payload),
                DragDropEffects.Copy);
        };

        tile.MouseLeftButtonUp += (_, e) =>
        {
            if (dragging)
            {
                dragging = false;
                e.Handled = true;
                return;
            }

            ApplyPreset(values);
            e.Handled = true;
        };

        PresetHost.Children.Add(tile);
    }

    /// <summary>Data format used when a preset is dragged onto a timeline clip.</summary>
    public const string EffectDragFormat = "YorteVegas.EffectPreset";

    /// <summary>Selects an applied effect (used after a preset drop).</summary>
    public void SelectEffect(ClipEffect effect)
    {
        if (_clip == null || !_clip.Effects.Contains(effect)) return;

        _selectedKind = effect.Kind;
        _selected = effect;
        RebuildList();
        RebuildRight();
    }

    private void ApplyPreset(IReadOnlyDictionary<string, double> values)
    {
        if (_project == null || _clip == null) return;

        if (_selected == null || _selected.Kind != _selectedKind)
            _selected = _project.AddEffect(_clip, _selectedKind);

        foreach (var pair in values)
            _selected.Set(pair.Key, pair.Value);
        _selected.Enabled = true;

        if (_selected.Kind == EffectKind.Crop)
            ApplyCropToClip(_selected);

        RebuildList();
        RebuildRight();
        RaiseChanged();
    }

    /// <summary>
    /// The Crop effect is a friendly front-end for the clip's Crop/Pan fields,
    /// so the existing preview and export paths render it without extra work.
    /// </summary>
    private void ApplyCropToClip(ClipEffect effect)
    {
        if (_clip == null) return;

        var x = Math.Clamp(effect.Get("x", 0), 0, 0.95);
        var y = Math.Clamp(effect.Get("y", 0), 0, 0.95);
        var width = Math.Clamp(effect.Get("width", 1), 0.05, 1 - x);
        var height = Math.Clamp(effect.Get("height", 1), 0.05, 1 - y);

        effect.Set("x", x);
        effect.Set("y", y);
        effect.Set("width", width);
        effect.Set("height", height);

        _clip.CropLeft = x;
        _clip.CropTop = y;
        _clip.CropRight = Math.Max(0, 1 - x - width);
        _clip.CropBottom = Math.Max(0, 1 - y - height);
        _clip.PanX = 0;
        _clip.PanY = 0;
    }

    private void SyncCropEffect(ClipEffect effect)
    {
        if (_clip == null) return;

        effect.Set("x", _clip.CropLeft);
        effect.Set("y", _clip.CropTop);
        effect.Set("width", Math.Max(0.05, 1 - _clip.CropLeft - _clip.CropRight));
        effect.Set("height", Math.Max(0.05, 1 - _clip.CropTop - _clip.CropBottom));
    }

    private static bool ValuesMatch(ClipEffect effect, EffectDefinition definition,
        IReadOnlyDictionary<string, double> values)
    {
        foreach (var parameter in definition.Parameters)
        {
            var expected = values.TryGetValue(parameter.Key, out var value)
                ? value
                : parameter.Default;
            if (Math.Abs(effect.Get(parameter.Key, parameter.Default) - expected) > 0.0005)
                return false;
        }

        return true;
    }

    private static bool ValuesMatch(IReadOnlyDictionary<string, double> left,
        EffectDefinition definition, IReadOnlyDictionary<string, double> right)
    {
        foreach (var parameter in definition.Parameters)
        {
            var a = left.TryGetValue(parameter.Key, out var value) ? value : parameter.Default;
            var b = right.TryGetValue(parameter.Key, out var other) ? other : parameter.Default;
            if (Math.Abs(a - b) > 0.0005) return false;
        }

        return true;
    }

    private FrameworkElement BuildSlider(ClipEffect effect, EffectParameter parameter)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var valueText = new TextBlock
        {
            Style = (Style)FindResource("ParamValue"),
            Text = Format(effect.Get(parameter.Key, parameter.Default), parameter)
        };
        var label = new TextBlock
        {
            Style = (Style)FindResource("ParamLabel"),
            Text = parameter.Label
        };

        Grid.SetColumn(valueText, 1);
        head.Children.Add(label);
        head.Children.Add(valueText);
        panel.Children.Add(head);

        var slider = new Slider
        {
            Style = (Style)FindResource("GradientSlider"),
            Minimum = parameter.Min,
            Maximum = parameter.Max,
            Value = Math.Clamp(effect.Get(parameter.Key, parameter.Default),
                parameter.Min, parameter.Max),
            SmallChange = parameter.Step,
            LargeChange = parameter.Step * 10,
            TickFrequency = parameter.Step,
            IsSnapToTickEnabled = true,
            Background = GradientFor(parameter.Gradient),
            Margin = new Thickness(0, 2, 0, 0)
        };

        slider.ValueChanged += (_, e) =>
        {
            if (_building) return;
            var value = Math.Round(e.NewValue / parameter.Step) * parameter.Step;
            effect.Set(parameter.Key, value);
            valueText.Text = Format(value, parameter);
            if (effect.Kind == EffectKind.Crop) ApplyCropToClip(effect);
            UpdateTint();
            RaiseChanged();
        };

        panel.Children.Add(slider);

        var range = new Grid { Margin = new Thickness(2, 1, 2, 0) };
        range.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        range.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var min = new TextBlock
        {
            Style = (Style)FindResource("ParamRange"),
            Text = Format(parameter.Min, parameter)
        };
        var max = new TextBlock
        {
            Style = (Style)FindResource("ParamRange"),
            Text = Format(parameter.Max, parameter),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(max, 1);
        range.Children.Add(min);
        range.Children.Add(max);
        panel.Children.Add(range);

        return panel;
    }

    private FrameworkElement BuildChoices(ClipEffect effect, EffectParameter parameter)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("ParamLabel"),
            Text = parameter.Label,
            Margin = new Thickness(0, 0, 0, 5)
        });

        var wrap = new WrapPanel();
        var current = effect.Get(parameter.Key, parameter.Default);
        var chips = new List<ToggleButton>();

        foreach (var choice in parameter.Choices ?? Array.Empty<EffectChoice>())
        {
            var chip = new ToggleButton
            {
                Style = (Style)FindResource("ChoiceChip"),
                Content = choice.Label,
                IsChecked = Math.Abs(choice.Value - current) < 0.001
            };

            chip.Click += (_, _) =>
            {
                effect.Set(parameter.Key, choice.Value);
                foreach (var other in chips)
                    other.IsChecked = ReferenceEquals(other, chip);
                RaiseChanged();
            };

            chips.Add(chip);
            wrap.Children.Add(chip);
        }

        panel.Children.Add(wrap);
        return panel;
    }

    private FrameworkElement BuildTintPreview(ClipEffect effect)
    {
        _tintEffect = effect;

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("ParamLabel"),
            Text = "Tint preview"
        });
        head.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("ParamValue"),
            Text = "neutral",
            HorizontalAlignment = HorizontalAlignment.Right
        });
        panel.Children.Add(head);

        var swatch = new Border
        {
            Height = 18,
            CornerRadius = new CornerRadius(4),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x32, 0x48)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 4, 0, 0)
        };
        _tintSwatch = swatch;
        panel.Children.Add(swatch);
        UpdateTint();
        return panel;
    }

    private void UpdateTint()
    {
        if (_tintSwatch == null || _tintEffect == null) return;

        static byte Channel(double value) =>
            (byte)Math.Clamp((int)Math.Round(value * 127.5), 0, 255);

        var tint = Color.FromRgb(
            Channel(_tintEffect.Get("red", 1)),
            Channel(_tintEffect.Get("green", 1)),
            Channel(_tintEffect.Get("blue", 1)));

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x05, 0x07, 0x0A), 0));
        brush.GradientStops.Add(new GradientStop(tint, 0.5));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xF2, 0xF7, 0xFC), 1));
        brush.Freeze();
        _tintSwatch.Background = brush;
        _tintSwatch.ToolTip =
            $"R {_tintEffect.Get("red", 1):0.00}  G {_tintEffect.Get("green", 1):0.00}  " +
            $"B {_tintEffect.Get("blue", 1):0.00}";
    }

    // ----------------------------------------------------------------- actions

    private void EnabledToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_building || _clip == null || _selected == null) return;

        _selected.Enabled = EnabledToggle.IsChecked == true;
        EnabledToggle.Content = _selected.Enabled ? "ON" : "OFF";
        RebuildList();
        RebuildRight();
        RaiseChanged();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || _clip == null || _selected == null) return;
        if (!_project.MoveEffect(_clip, _selected, -1)) return;

        RebuildList();
        RebuildRight();
        RaiseChanged();
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || _clip == null || _selected == null) return;
        if (!_project.MoveEffect(_clip, _selected, 1)) return;

        RebuildList();
        RebuildRight();
        RaiseChanged();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        RemoveInstance(_selected);
    }

    private void RemoveInstance(ClipEffect effect)
    {
        if (_project == null || _clip == null) return;

        _project.RemoveEffect(_clip, effect);
        if (ReferenceEquals(_selected, effect)) _selected = null;

        RebuildList();
        RebuildRight();
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    // ----------------------------------------------------------------- helpers

    private static string Format(double value, EffectParameter parameter) =>
        value.ToString(parameter.Format, CultureInfo.InvariantCulture);

    private static Brush GradientFor(string? key)
    {
        static Color Hex(string value) => (Color)ColorConverter.ConvertFromString(value);

        static LinearGradientBrush Two(string from, string to)
        {
            var brush = new LinearGradientBrush(Hex(from), Hex(to), 0);
            brush.Freeze();
            return brush;
        }

        switch (key)
        {
            case "red":
                return Two("#3A0A0E", "#FF4D4D");
            case "green":
                return Two("#0B2A16", "#3DDC84");
            case "blue":
                return Two("#0A1B3A", "#4D9BFF");
            case "saturation":
            {
                var brush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5),
                    EndPoint = new Point(1, 0.5)
                };
                brush.GradientStops.Add(new GradientStop(Hex("#6B7280"), 0));
                brush.GradientStops.Add(new GradientStop(Hex("#4D9BFF"), 0.28));
                brush.GradientStops.Add(new GradientStop(Hex("#3DDC84"), 0.5));
                brush.GradientStops.Add(new GradientStop(Hex("#FFD34D"), 0.74));
                brush.GradientStops.Add(new GradientStop(Hex("#FF5C5C"), 1));
                brush.Freeze();
                return brush;
            }
            case "brightness":
                return Two("#05070A", "#F2F7FC");
            case "contrast":
                return Two("#2A2F38", "#FFFFFF");
            default:
                return Two("#16202D", "#2E9BFF");
        }
    }
}
