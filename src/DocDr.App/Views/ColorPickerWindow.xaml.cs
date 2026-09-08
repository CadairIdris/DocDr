using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DocDr.App.Views;

/// <summary>A small RGB colour picker (sliders + hex + presets). Modal; returns an opaque ARGB.</summary>
public partial class ColorPickerWindow : Window
{
    private static readonly uint[] PresetArgb =
    [
        0xFFF44336, 0xFFE91E63, 0xFF9C27B0, 0xFF673AB7, 0xFF3F51B5, 0xFF2196F3,
        0xFF03A9F4, 0xFF00BCD4, 0xFF009688, 0xFF4CAF50, 0xFF8BC34A, 0xFFCDDC39,
        0xFFFFEB3B, 0xFFFFC107, 0xFFFF9800, 0xFFFF5722, 0xFF795548, 0xFF607D8B,
        0xFF000000, 0xFF9E9E9E, 0xFFFFFFFF,
    ];

    private bool _updating;

    public ColorPickerWindow(uint initialArgb)
    {
        InitializeComponent();
        Presets.ItemsSource = PresetArgb.Select(BrushFor).ToList();
        SetChannels(initialArgb);
    }

    /// <summary>The chosen colour once the dialog closes with OK; otherwise null.</summary>
    public uint? SelectedColor { get; private set; }

    /// <summary>Show the picker modally; returns the chosen opaque ARGB, or null if cancelled.</summary>
    public static uint? Pick(uint initialArgb, Window? owner)
    {
        var window = new ColorPickerWindow(initialArgb) { Owner = owner };
        return window.ShowDialog() == true ? window.SelectedColor : null;
    }

    private static SolidColorBrush BrushFor(uint argb) => new(Color.FromRgb(
        (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    private uint CurrentArgb => 0xFF000000
        | ((uint)(byte)R.Value << 16) | ((uint)(byte)G.Value << 8) | (byte)B.Value;

    private void SetChannels(uint argb)
    {
        _updating = true;
        R.Value = (byte)(argb >> 16);
        G.Value = (byte)(argb >> 8);
        B.Value = (byte)argb;
        _updating = false;
        Refresh();
    }

    private void Refresh()
    {
        uint argb = CurrentArgb;
        Preview.Background = BrushFor(argb);
        if (!Hex.IsKeyboardFocused)
        {
            Hex.Text = $"#{argb & 0xFFFFFF:X6}";
        }
    }

    private void Channel_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updating)
        {
            Refresh();
        }
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SolidColorBrush brush })
        {
            Color c = brush.Color;
            SetChannels(0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B);
        }
    }

    private void Hex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitHex();
        }
    }

    private void Hex_Commit(object sender, RoutedEventArgs e) => CommitHex();

    private void CommitHex()
    {
        string text = Hex.Text.Trim().TrimStart('#');
        if (text.Length == 6 && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            SetChannels(0xFF000000u | rgb);
        }
        else
        {
            Refresh();
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedColor = CurrentArgb;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
