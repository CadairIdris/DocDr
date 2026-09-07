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

/// <summary>A point in PDFium page space (points, bottom-left origin).</summary>
public readonly record struct PdfPoint(double X, double Y);

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

    /// <summary>
    /// Map a page-space rectangle (from text/annotation APIs, which report <b>unrotated</b>
    /// coordinates) to a top-left-origin device rectangle on the page as it is displayed with
    /// <paramref name="rotation"/> applied.
    /// </summary>
    /// <param name="rect">Rectangle in unrotated PDFium page space (MediaBox origin).</param>
    /// <param name="unrotatedPageSize">CropBox size before rotation, in points.</param>
    /// <param name="rotation">The page's clockwise display rotation.</param>
    /// <param name="scale">Points → target unit (e.g. <see cref="PointToDip"/> * zoom).</param>
    /// <param name="cropOrigin">CropBox lower-left in MediaBox space (see
    /// <see cref="PdfDocument.GetCropOrigins"/>); subtracted so the rect lands on the rendered page.</param>
    public static DeviceRect PageToDevice(PdfRect rect, PdfSize unrotatedPageSize, PdfRotation rotation, double scale, PdfPoint cropOrigin = default)
    {
        double w = unrotatedPageSize.Width * scale;
        double h = unrotatedPageSize.Height * scale;

        double left = Math.Min(rect.Left, rect.Right) - cropOrigin.X;
        double right = Math.Max(rect.Left, rect.Right) - cropOrigin.X;
        double bottom = Math.Min(rect.Top, rect.Bottom) - cropOrigin.Y;
        double top = Math.Max(rect.Top, rect.Bottom) - cropOrigin.Y;

        ReadOnlySpan<(double X, double Y)> corners =
        [
            (left, top), (right, top), (right, bottom), (left, bottom),
        ];

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach ((double px, double py) in corners)
        {
            // Unrotated device point (top-left origin, y down).
            double dx = px * scale;
            double dy = h - (py * scale);

            (double rx, double ry) = rotation switch
            {
                PdfRotation.Clockwise90 => (h - dy, dx),
                PdfRotation.Rotate180 => (w - dx, h - dy),
                PdfRotation.CounterClockwise90 => (dy, w - dx),
                _ => (dx, dy),
            };

            minX = Math.Min(minX, rx);
            maxX = Math.Max(maxX, rx);
            minY = Math.Min(minY, ry);
            maxY = Math.Max(maxY, ry);
        }

        return new DeviceRect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Map a single unrotated page-space point to a top-left-origin device point on the page as
    /// displayed with <paramref name="rotation"/> applied. The point analogue of
    /// <see cref="PageToDevice(PdfRect,PdfSize,PdfRotation,double)"/>.
    /// </summary>
    public static (double X, double Y) PageToDevicePoint(
        PdfPoint point, PdfSize unrotatedPageSize, PdfRotation rotation, double scale, PdfPoint cropOrigin = default)
    {
        double w = unrotatedPageSize.Width * scale;
        double h = unrotatedPageSize.Height * scale;

        double dx = (point.X - cropOrigin.X) * scale;
        double dy = h - ((point.Y - cropOrigin.Y) * scale);

        return rotation switch
        {
            PdfRotation.Clockwise90 => (h - dy, dx),
            PdfRotation.Rotate180 => (w - dx, h - dy),
            PdfRotation.CounterClockwise90 => (dy, w - dx),
            _ => (dx, dy),
        };
    }

    /// <summary>
    /// Inverse of the rotation-aware <see cref="PageToDevice(PdfRect,PdfSize,PdfRotation,double)"/>:
    /// map a top-left-origin device point on the displayed (rotated) page back to unrotated
    /// PDFium page space (points, bottom-left origin).
    /// </summary>
    /// <param name="deviceX">X in DIP within the displayed page (0 = left edge).</param>
    /// <param name="deviceY">Y in DIP within the displayed page (0 = top edge).</param>
    public static PdfPoint DeviceToPage(double deviceX, double deviceY, PdfSize unrotatedPageSize, PdfRotation rotation, double scale, PdfPoint cropOrigin = default)
    {
        double w = unrotatedPageSize.Width * scale;
        double h = unrotatedPageSize.Height * scale;

        (double dx, double dy) = rotation switch
        {
            PdfRotation.Clockwise90 => (deviceY, h - deviceX),
            PdfRotation.Rotate180 => (w - deviceX, h - deviceY),
            PdfRotation.CounterClockwise90 => (w - deviceY, deviceX),
            _ => (deviceX, deviceY),
        };

        return new PdfPoint((dx / scale) + cropOrigin.X, unrotatedPageSize.Height - (dy / scale) + cropOrigin.Y);
    }
}
