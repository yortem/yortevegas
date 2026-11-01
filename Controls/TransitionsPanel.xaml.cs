using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using YorteVegas.Models;
using YorteVegas.Services;
using YorteVegas.ViewModels;

namespace YorteVegas.Controls;

/// <summary>
/// Vegas-style transition browser. The list on the left chooses the style; the
/// presets and sliders on the right edit the transition on the selected clip
/// edge. Presets can also be dragged onto an overlap or a clip edge.
/// </summary>
public partial class TransitionsPanel : UserControl
{
    private EditProject? _project;
    private TimelineClip? _clip;
    private TransitionKind _selectedKind = TransitionKind.Dissolve;
    private bool _outgoing;
    private bool _building;

    /// <summary>Raised after any change to the clip's transitions or values.</summary>
    public event EventHandler? Changed;

    public TransitionsPanel()
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

        var hasClip = project != null && clip != null;
        ContentGrid.Visibility = hasClip ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasClip ? Visibility.Collapsed : Visibility.Visible;

        if (!hasClip)
        {
            ClipNameText.Text = "";
            TransitionListHost.Children.Clear();
            PresetHost.Children.Clear();
            ParamHost.Children.Clear();
            return;
        }

        ClipNameText.Text = clip!.Name;
        _outgoing = clip.TransitionIn == null && clip.TransitionOut != null;

        var current = GetSlot();
        _selectedKind = current?.Kind ?? TransitionKind.Dissolve;

        RebuildList();
        RebuildRight();
    }

    private ClipTransition? GetSlot() =>
        _clip == null ? null : _outgoing ? _clip.TransitionOut : _clip.TransitionIn;

    // --------------------------------------------------------------- list

    private void RebuildList()
    {
        TransitionListHost.Children.Clear();
        if (_clip == null) return;

        foreach (var definition in TransitionCatalog.All)
        {
            var inSlot = _clip.TransitionIn?.Kind == definition.Kind
                ? _clip.TransitionIn
                : null;
            var outSlot = _clip.TransitionOut?.Kind == definition.Kind
                ? _clip.TransitionOut
                : null;
            var applied = inSlot != null || outSlot != null;
            var enabled = inSlot?.Enabled == true || outSlot?.Enabled == true;
            var selected = definition.Kind == _selectedKind;

            var row = new Border
            {
                Style = (Style)FindResource("TransitionRow"),
                Tag = selected ? "selected" : null,
                ToolTip = definition.Name + " — " + definition.Description
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var glyph = new TextBlock
            {
                Text = "tr",
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontStyle = FontStyles.Italic,
                FontWeight = FontWeights.Bold,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(selected
                    ? Color.FromRgb(0xC7, 0xA9, 0xFF)
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

            var dot = new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = new SolidColorBrush(Color.FromRgb(0x9A, 0x5C, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 2, 0),
                Visibility = enabled ? Visibility.Visible : Visibility.Collapsed
            };

            Grid.SetColumn(glyph, 0);
            Grid.SetColumn(name, 1);
            Grid.SetColumn(dot, 2);
            grid.Children.Add(glyph);
            grid.Children.Add(name);
            grid.Children.Add(dot);
            row.Child = grid;

            var kind = definition.Kind;
            row.MouseLeftButtonDown += (_, e) =>
            {
                _selectedKind = kind;
                RebuildList();
                RebuildRight();
                e.Handled = true;
            };

            TransitionListHost.Children.Add(row);
        }
    }

    // ------------------------------------------------------------ presets/UI

    private void RebuildRight()
    {
        PresetHost.Children.Clear();
        ParamHost.Children.Clear();
        if (_clip == null) return;

        var definition = TransitionCatalog.Get(_selectedKind);
        TransitionTitle.Text = definition.Name;
        TransitionDescription.Text = definition.Description;

        _building = true;
        InSlotToggle.IsChecked = !_outgoing;
        OutSlotToggle.IsChecked = _outgoing;
        _building = false;

        var slot = GetSlot();
        var applied = slot != null && slot.Kind == _selectedKind;
        AppliedControls.Visibility = applied ? Visibility.Visible : Visibility.Collapsed;

        if (applied)
        {
            _building = true;
            EnabledToggle.IsChecked = slot!.Enabled;
            EnabledToggle.Content = slot.Enabled ? "ON" : "OFF";
            _building = false;
        }

        RebuildPresets(definition, applied ? slot : null);

        if (!applied)
        {
            ParamHost.Children.Add(new TextBlock
            {
                Text = $"Click a preset above to add this transition to the " +
                       $"{(_outgoing ? "end" : "start")} of the clip.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x6B, 0x7E)),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        var overlapDuration = OverlapDuration();
        if (overlapDuration is { } length)
        {
            ParamHost.Children.Add(new TextBlock
            {
                Text = $"This edge sits on a crossfade; the overlap length " +
                       $"({length:0.00} s) is used for the transition.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0xA0, 0xB4)),
                FontSize = 9.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
        }

        foreach (var parameter in definition.Parameters)
        {
            if (parameter.Kind == EffectParameterKind.Choice)
            {
                ParamHost.Children.Add(BuildChoices(slot!, parameter));
            }
            else
            {
                var isDuration = parameter.Key == "duration";
                var displayValue = isDuration && overlapDuration is { } value
                    ? value
                    : (double?)null;
                ParamHost.Children.Add(BuildSlider(slot!, parameter,
                    enabled: !(isDuration && overlapDuration != null),
                    displayValue));
            }
        }
    }

    private double? OverlapDuration()
    {
        if (_project == null || _clip == null) return null;

        foreach (var track in _project.Tracks)
        {
            foreach (var overlap in _project.GetOverlaps(track))
            {
                if (!_outgoing && ReferenceEquals(overlap.Incoming, _clip))
                    return overlap.EndSeconds - overlap.StartSeconds;
                if (_outgoing && ReferenceEquals(overlap.Outgoing, _clip))
                    return overlap.EndSeconds - overlap.StartSeconds;
            }
        }

        return null;
    }

    private void RebuildPresets(TransitionDefinition definition, ClipTransition? transition)
    {
        foreach (var preset in definition.Presets)
            AddPresetTile(preset.Name, preset.Values, transition, definition);
    }

    private void AddPresetTile(string name, IReadOnlyDictionary<string, double> values,
        ClipTransition? transition, TransitionDefinition definition)
    {
        var selected = transition != null && ValuesMatch(transition, definition, values);
        var thumbnail = TransitionThumbnailService.Render(definition.Kind);

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
            var payload = new TransitionDropData(definition.Kind,
                new Dictionary<string, double>(values));
            DragDrop.DoDragDrop(tile, new DataObject(TransitionDragFormat, payload),
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

    /// <summary>Data format used when a transition preset is dragged onto the timeline.</summary>
    public const string TransitionDragFormat = "YorteVegas.TransitionPreset";

    /// <summary>Selects a transition kind and edge (used after a preset drop).</summary>
    public void SelectTransition(TransitionKind kind, bool outgoing)
    {
        if (_clip == null) return;

        _selectedKind = kind;
        _outgoing = outgoing;
        RebuildList();
        RebuildRight();
    }

    private void ApplyPreset(IReadOnlyDictionary<string, double> values)
    {
        if (_project == null || _clip == null) return;

        var transition = TransitionCatalog.Create(_selectedKind);
        foreach (var pair in values)
            transition.Set(pair.Key, pair.Value);
        transition.Enabled = true;

        if (_outgoing) _clip.TransitionOut = transition;
        else _clip.TransitionIn = transition;

        _project.NotifyTransitionChanged();
        RebuildList();
        RebuildRight();
        RaiseChanged();
    }

    private static bool ValuesMatch(ClipTransition transition, TransitionDefinition definition,
        IReadOnlyDictionary<string, double> values)
    {
        foreach (var parameter in definition.Parameters)
        {
            var expected = values.TryGetValue(parameter.Key, out var value)
                ? value
                : parameter.Default;
            if (Math.Abs(transition.Get(parameter.Key, parameter.Default) - expected) > 0.0005)
                return false;
        }

        return true;
    }

    private FrameworkElement BuildSlider(ClipTransition transition, EffectParameter parameter,
        bool enabled, double? displayValue)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var value = displayValue ?? transition.Get(parameter.Key, parameter.Default);
        var valueText = new TextBlock
        {
            Style = (Style)FindResource("ParamValue"),
            Text = Format(value, parameter) + (parameter.Key == "duration" ? " s" : "")
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
            Value = Math.Clamp(value, parameter.Min, parameter.Max),
            SmallChange = parameter.Step,
            LargeChange = parameter.Step * 10,
            TickFrequency = parameter.Step,
            IsSnapToTickEnabled = true,
            IsEnabled = enabled,
            Margin = new Thickness(0, 2, 0, 0)
        };

        slider.ValueChanged += (_, e) =>
        {
            if (_building || !enabled) return;
            var newValue = Math.Round(e.NewValue / parameter.Step) * parameter.Step;
            transition.Set(parameter.Key, newValue);
            valueText.Text = Format(newValue, parameter) +
                             (parameter.Key == "duration" ? " s" : "");
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

    private FrameworkElement BuildChoices(ClipTransition transition, EffectParameter parameter)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("ParamLabel"),
            Text = parameter.Label,
            Margin = new Thickness(0, 0, 0, 5)
        });

        var wrap = new WrapPanel();
        var current = transition.Get(parameter.Key, parameter.Default);
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
                transition.Set(parameter.Key, choice.Value);
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

    // ------------------------------------------------------------- events

    private void InSlot_Click(object sender, RoutedEventArgs e)
    {
        if (_building) return;
        _outgoing = false;
        if (_clip?.TransitionIn is { } transition) _selectedKind = transition.Kind;
        RebuildList();
        RebuildRight();
    }

    private void OutSlot_Click(object sender, RoutedEventArgs e)
    {
        if (_building) return;
        _outgoing = true;
        if (_clip?.TransitionOut is { } transition) _selectedKind = transition.Kind;
        RebuildList();
        RebuildRight();
    }

    private void EnabledToggle_Click(object sender, RoutedEventArgs e)
    {
        var slot = GetSlot();
        if (slot == null) return;

        slot.Enabled = EnabledToggle.IsChecked == true;
        EnabledToggle.Content = slot.Enabled ? "ON" : "OFF";
        _project?.NotifyTransitionChanged();
        RebuildList();
        RaiseChanged();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || _clip == null) return;
        if (GetSlot() == null) return;

        _project.RemoveTransition(_clip, _outgoing);
        RebuildList();
        RebuildRight();
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static string Format(double value, EffectParameter parameter) =>
        value.ToString(parameter.Format, CultureInfo.InvariantCulture);
}
