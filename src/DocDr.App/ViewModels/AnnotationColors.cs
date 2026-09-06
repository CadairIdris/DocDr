using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace DocDr.App.ViewModels;

/// <summary>The fixed highlight palette. Keys are stable strings used in commands and settings.</summary>
public static class AnnotationColors
{
    public const string Default = "Yellow";

    public static IReadOnlyList<string> Keys { get; } = ["Yellow", "Green", "Blue", "Pink", "Orange"];

    public static uint ToArgb(string? key) => key switch
    {
        "Green" => 0xFF81C784,
        "Blue" => 0xFF64B5F6,
        "Pink" => 0xFFF06292,
        "Orange" => 0xFFFFB74D,
        _ => 0xFFFFEB3B,
    };

    public static Color ToColor(string? key)
    {
        uint argb = ToArgb(key);
        return Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    public static Color ToColor(uint argb) =>
        Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>Nearest palette key to an arbitrary stored colour.</summary>
    public static string FromArgb(uint argb)
    {
        string best = Default;
        double bestDist = double.MaxValue;
        foreach (string key in Keys)
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
