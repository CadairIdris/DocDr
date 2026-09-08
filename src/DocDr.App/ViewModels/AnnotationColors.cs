using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace DocDr.App.ViewModels;

/// <summary>The fixed highlight palette. Keys are stable strings used in commands and settings.</summary>
public static class AnnotationColors
{
    public const string Default = "Yellow";

    /// <summary>The key for the user-chosen custom colour (its ARGB lives on the tab / settings).</summary>
    public const string Custom = "Custom";

    /// <summary>The app-wide current "Custom" swatch colour (ARGB). Kept in step with the settings /
    /// the active tab so swatch rendering resolves the "Custom" key without extra plumbing.</summary>
    public static uint CustomColorArgb { get; set; } = 0xFF3F51B5;

    /// <summary>The five fixed swatches plus the "Custom" slot.</summary>
    public static IReadOnlyList<string> Keys { get; } = ["Yellow", "Green", "Blue", "Pink", "Orange", Custom];

    /// <summary>The five fixed swatches only (no "Custom").</summary>
    public static IReadOnlyList<string> PresetKeys { get; } = ["Yellow", "Green", "Blue", "Pink", "Orange"];

    public static uint ToArgb(string? key) => key switch
    {
        "Green" => 0xFF81C784,
        "Blue" => 0xFF64B5F6,
        "Pink" => 0xFFF06292,
        "Orange" => 0xFFFFB74D,
        _ => 0xFFFFEB3B,
    };

    /// <summary>Resolve a palette key to an ARGB, using <paramref name="customArgb"/> for the "Custom" slot.</summary>
    public static uint ToArgb(string? key, uint customArgb) =>
        key == Custom ? customArgb : ToArgb(key);

    public static Color ToColor(string? key)
    {
        uint argb = key == Custom ? CustomColorArgb : ToArgb(key);
        return Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    public static Color ToColor(uint argb) =>
        Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>The palette key whose colour equals <paramref name="argb"/> exactly, else "Custom".</summary>
    public static string ExactKey(uint argb)
    {
        foreach (string key in PresetKeys)
        {
            if ((ToArgb(key) & 0xFFFFFF) == (argb & 0xFFFFFF))
            {
                return key;
            }
        }

        return Custom;
    }

    /// <summary>Nearest fixed-swatch key to an arbitrary stored colour (never returns "Custom").</summary>
    public static string FromArgb(uint argb)
    {
        string best = Default;
        double bestDist = double.MaxValue;
        foreach (string key in PresetKeys)
        {
            uint c = ToArgb(key);
            double d = Sq((int)((argb >> 16) & 0xFF) - (int)((c >> 16) & 0xFF))
                       + Sq((int)((argb >> 8) & 0xFF) - (int)((c >> 8) & 0xFF))
                       + Sq((int)(argb & 0xFF) - (int)(c & 0xFF));
            if (d < bestDist)
            {
                bestDist = d;
                best = key;
            }
        }

        return best;

        static double Sq(int v) => v * (double)v;
    }
}
