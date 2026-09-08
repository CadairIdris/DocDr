using System;
using System.Collections.Generic;
using DocDr.Pdf;

namespace DocDr.App.Services;

/// <summary>Standard page sizes (in PDF points) for the New-document dialog.</summary>
public static class PageSizes
{
    private const double PtPerMm = 72.0 / 25.4;

    /// <summary>Portrait ISO A sizes, name → (width mm, height mm).</summary>
    public static readonly IReadOnlyList<(string Name, double WidthMm, double HeightMm)> Standard =
    [
        ("A4", 210, 297),
        ("A3", 297, 420),
        ("A2", 420, 594),
        ("A1", 594, 841),
    ];

    public static PdfSize FromMm(double widthMm, double heightMm, bool landscape)
    {
        double w = widthMm * PtPerMm;
        double h = heightMm * PtPerMm;
        return landscape && h > w ? new PdfSize(h, w) : new PdfSize(w, h);
    }

    public static (double WidthMm, double HeightMm)? StandardMm(string name)
    {
        foreach ((string n, double w, double h) in Standard)
        {
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            {
                return (w, h);
            }
        }

        return null;
    }
}
