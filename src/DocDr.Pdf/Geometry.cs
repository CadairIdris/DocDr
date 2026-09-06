namespace DocDr.Pdf;

/// <summary>Page dimensions in PDF points (1/72 inch).</summary>
public readonly record struct PdfSize(double Width, double Height);

/// <summary>
/// A rectangle in PDFium page space: origin bottom-left, Y grows upward, units are points.
/// As produced by <c>FPDFText_GetRect</c>, <see cref="Top"/> &gt;= <see cref="Bottom"/>.
/// </summary>
public readonly record struct PdfRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;
}

/// <summary>
/// A rectangle in device / DIP space: origin top-left, Y grows downward. This is directly
/// consumable by WPF (<c>System.Windows.Rect</c>) without further flipping.
/// </summary>
public readonly record struct DeviceRect(double X, double Y, double Width, double Height);

/// <summary>
/// Conversions between PDFium page space (points, bottom-left origin) and device space
/// (pixels or DIPs, top-left origin). The single place this flip is expressed.
/// </summary>
public static class PdfCoordinates
{
    /// <summary>Points per inch in the PDF coordinate system.</summary>
    public const double PointsPerInch = 72.0;

    /// <summary>Device-independent pixels per inch in WPF.</summary>
    public const double DipPerInch = 96.0;

    /// <summary>Scale factor to turn PDF points into WPF DIPs at 100% zoom.</summary>
    public const double PointToDip = DipPerInch / PointsPerInch;

    /// <summary>
    /// Map a page-space rectangle to a top-left-origin rectangle at the given uniform scale.
    /// </summary>
    /// <param name="rect">Rectangle in PDFium page space.</param>
    /// <param name="pageHeightPoints">Height of the page in points (for the Y flip).</param>
    /// <param name="scale">Multiplier from points to the target unit (e.g. <see cref="PointToDip"/> * zoom).</param>
    public static DeviceRect PageToDevice(PdfRect rect, double pageHeightPoints, double scale)
    {
        double x = rect.Left * scale;
        double y = (pageHeightPoints - rect.Top) * scale;
        double w = rect.Width * scale;
        double h = rect.Height * scale;
        return new DeviceRect(x, y, w, h);
    }
}
