using System.Globalization;

namespace DocDr.Pdf;

/// <summary>
/// A drawing scale: how many real-world <see cref="Unit"/>s one PDF point (1/72 in on the sheet)
/// represents. Stored on every measurement (like ISO 32000 §12.9's per-annotation
/// <c>/Measure</c>), so changing the page's scale later never changes earlier measurements.
/// </summary>
/// <param name="UnitsPerPoint">Real-world units per PDF point.</param>
/// <param name="Unit">Real-world unit label ("mm", "m", "ft"…).</param>
/// <param name="Label">How the scale was entered, for display ("1:50", "Calibrated").</param>
/// <param name="Decimals">Decimal places in captions.</param>
public sealed record PdfMeasureScale(double UnitsPerPoint, string Unit, string Label, int Decimals = 2)
{
    /// <summary>Units the scale dialogs offer, smallest first.</summary>
    public static IReadOnlyList<string> Units { get; } = ["mm", "cm", "m", "in", "ft"];

    /// <summary>No scale set: measure the sheet itself, in millimetres.</summary>
    public static PdfMeasureScale Default { get; } = FromRatio(1, "mm", 1, "mm", decimals: 1, label: "1:1");

    /// <summary>Millimetres per <paramref name="unit"/>; NaN for an unknown unit.</summary>
    public static double MillimetresPer(string unit) => unit switch
    {
        "mm" => 1,
        "cm" => 10,
        "m" => 1000,
        "km" => 1_000_000,
        "in" => 25.4,
        "ft" => 304.8,
        "pt" => 25.4 / 72,
        _ => double.NaN,
    };

    /// <summary>
    /// A scale as a drawing states it: <paramref name="paper"/> <paramref name="paperUnit"/> on the
    /// sheet = <paramref name="real"/> <paramref name="realUnit"/> in the world. "1:50" with
    /// metres = <c>FromRatio(1, "mm", 50, "mm")</c> then shown in m, i.e.
    /// <c>FromRatio(1, "mm", 0.05, "m")</c>; the common case is
    /// <see cref="FromDrawingRatio"/>.
    /// </summary>
    public static PdfMeasureScale FromRatio(
        double paper, string paperUnit, double real, string realUnit, int decimals = 2, string? label = null)
    {
        double paperMm = MillimetresPer(paperUnit), realMm = MillimetresPer(realUnit);
        if (!(paper > 0) || !(real > 0) || double.IsNaN(paperMm) || double.IsNaN(realMm))
        {
            throw new ArgumentException("A scale needs positive lengths in known units.");
        }

        // One point on paper is (25.4/72) mm of paper → × (real/paper) in real mm → ÷ realMm units.
        double paperMmPerPoint = 25.4 / 72;
        double realPerPaper = (real * realMm) / (paper * paperMm);
        double unitsPerPoint = paperMmPerPoint * realPerPaper / realMm;
        string text = label ?? $"{Num(paper)} {paperUnit} = {Num(real)} {realUnit}";
        return new PdfMeasureScale(unitsPerPoint, realUnit, text, Math.Clamp(decimals, 0, 6));
    }

    /// <summary>"1 : <paramref name="ratio"/>" (a plan's scale bar), reported in <paramref name="unit"/>.</summary>
    public static PdfMeasureScale FromDrawingRatio(double ratio, string unit, int decimals = 2)
    {
        if (!(ratio > 0))
        {
            throw new ArgumentException("The scale ratio must be positive.", nameof(ratio));
        }

        // 1 mm on the sheet = ratio mm in the world, shown in `unit`.
        double realInUnit = ratio / MillimetresPer(unit);
        return FromRatio(1, "mm", realInUnit, unit, decimals, $"1:{Num(ratio)}");
    }

    /// <summary>
    /// Calibrate from a line drawn on the page: it is <paramref name="pageDistancePoints"/> long on
    /// the sheet and <paramref name="realLength"/> <paramref name="unit"/> in reality.
    /// </summary>
    public static PdfMeasureScale Calibrate(double pageDistancePoints, double realLength, string unit, int decimals = 2)
    {
        if (!(pageDistancePoints > 0) || !(realLength > 0) || string.IsNullOrWhiteSpace(unit))
        {
            throw new ArgumentException("Calibration needs a drawn line and a positive known length.");
        }

        return new PdfMeasureScale(realLength / pageDistancePoints, unit.Trim(), "Calibrated", Math.Clamp(decimals, 0, 6));
    }

    private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>Measurement geometry and captions (distance, perimeter, area) in PDF user space.</summary>
public static class PdfMeasure
{
    public static bool IsMeasureKind(PdfAnnotationKind kind) =>
        kind is PdfAnnotationKind.MeasureDistance or PdfAnnotationKind.MeasurePerimeter
            or PdfAnnotationKind.MeasureArea;

    /// <summary>Minimum vertices for a measurement of <paramref name="kind"/>.</summary>
    public static int MinimumPoints(PdfAnnotationKind kind) => kind == PdfAnnotationKind.MeasureArea ? 3 : 2;

    /// <summary>Total length of the open polyline through <paramref name="points"/>, in points.</summary>
    public static double Length(IReadOnlyList<PdfPoint> points)
    {
        double total = 0;
        for (int i = 1; i < points.Count; i++)
        {
            total += Distance(points[i - 1], points[i]);
        }

        return total;
    }

    /// <summary>Closed-polygon perimeter (includes the closing edge), in points.</summary>
    public static double ClosedLength(IReadOnlyList<PdfPoint> points) =>
        points.Count < 2 ? 0 : Length(points) + Distance(points[^1], points[0]);

    /// <summary>Area of the simple polygon through <paramref name="points"/> (shoelace), in points².</summary>
    public static double Area(IReadOnlyList<PdfPoint> points)
    {
        if (points.Count < 3)
        {
            return 0;
        }

        double twice = 0;
        for (int i = 0; i < points.Count; i++)
        {
            PdfPoint a = points[i], b = points[(i + 1) % points.Count];
            twice += (a.X * b.Y) - (b.X * a.Y);
        }

        return Math.Abs(twice) / 2;
    }

    public static double Distance(PdfPoint a, PdfPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>The measured value in real-world units (units² for an area).</summary>
    public static double Value(PdfAnnotationKind kind, IReadOnlyList<PdfPoint> points, PdfMeasureScale scale) => kind switch
    {
        PdfAnnotationKind.MeasureArea => Area(points) * scale.UnitsPerPoint * scale.UnitsPerPoint,
        _ => Length(points) * scale.UnitsPerPoint,
    };

    /// <summary>"12.35 m" / "4.20 m²" — the value with its unit, at the scale's precision.</summary>
    public static string Format(PdfAnnotationKind kind, double value, PdfMeasureScale scale)
    {
        string number = value.ToString("N" + Math.Clamp(scale.Decimals, 0, 6), CultureInfo.CurrentCulture);
        return kind == PdfAnnotationKind.MeasureArea ? $"{number} {scale.Unit}²" : $"{number} {scale.Unit}";
    }

    /// <summary>The caption drawn on a measurement (and written to its <c>/Contents</c>).</summary>
    public static string Caption(PdfAnnotation a)
    {
        PdfMeasureScale scale = a.Scale ?? PdfMeasureScale.Default;
        return Format(a.Kind, Value(a.Kind, a.Vertices, scale), scale);
    }

    /// <summary>The caption with a kind prefix, for a comment list in another viewer.</summary>
    public static string Describe(PdfAnnotation a) => a.Kind switch
    {
        PdfAnnotationKind.MeasureArea => "Area " + Caption(a),
        PdfAnnotationKind.MeasurePerimeter => "Perimeter " + Caption(a),
        _ => "Distance " + Caption(a),
    };

    /// <summary>Where the caption sits: a distance's midpoint, a perimeter's last vertex, an area's
    /// centroid (vertex average if the polygon is degenerate).</summary>
    public static PdfPoint CaptionAnchor(PdfAnnotationKind kind, IReadOnlyList<PdfPoint> points)
    {
        if (points.Count == 0)
        {
            return default;
        }

        switch (kind)
        {
            case PdfAnnotationKind.MeasureDistance when points.Count >= 2:
                return new PdfPoint((points[0].X + points[1].X) / 2, (points[0].Y + points[1].Y) / 2);
            case PdfAnnotationKind.MeasurePerimeter:
                return points[^1];
            case PdfAnnotationKind.MeasureArea when points.Count >= 3:
                double a2 = 0, cx = 0, cy = 0;
                for (int i = 0; i < points.Count; i++)
                {
                    PdfPoint p = points[i], q = points[(i + 1) % points.Count];
                    double cross = (p.X * q.Y) - (q.X * p.Y);
                    a2 += cross;
                    cx += (p.X + q.X) * cross;
                    cy += (p.Y + q.Y) * cross;
                }

                if (Math.Abs(a2) > 1e-9)
                {
                    return new PdfPoint(cx / (3 * a2), cy / (3 * a2));
                }

                break;
        }

        return new PdfPoint(points.Average(p => p.X), points.Average(p => p.Y));
    }

    /// <summary>Snap <paramref name="p"/> so the segment from <paramref name="from"/> runs at a
    /// multiple of 45° (Shift while measuring), keeping its length.</summary>
    public static PdfPoint Constrain45(PdfPoint from, PdfPoint p)
    {
        double dx = p.X - from.X, dy = p.Y - from.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 1e-9)
        {
            return p;
        }

        double step = Math.PI / 4;
        double angle = Math.Round(Math.Atan2(dy, dx) / step) * step;
        return new PdfPoint(from.X + (length * Math.Cos(angle)), from.Y + (length * Math.Sin(angle)));
    }

    /// <summary>Caption font size in points (it scales with the page, like the measured lines).</summary>
    public const double CaptionFontSize = 9;

    /// <summary>Padding around the caption's background box, in points.</summary>
    public const double CaptionPadding = 2;

    /// <summary>The caption's background box in page space, centred on its anchor.</summary>
    public static PdfRect CaptionRect(PdfAnnotation a)
    {
        string caption = Caption(a);
        PdfPoint anchor = CaptionAnchor(a.Kind, a.Vertices);
        double w = PdfTextWrap.MeasureHelvetica(caption, CaptionFontSize) + (2 * CaptionPadding);
        double h = CaptionFontSize + (2 * CaptionPadding);
        return new PdfRect(anchor.X - (w / 2), anchor.Y + (h / 2), anchor.X + (w / 2), anchor.Y - (h / 2));
    }

    /// <summary>The page-space rectangle the saved appearance must cover: the vertices (plus the
    /// end ticks and stroke) and the caption box.</summary>
    public static PdfRect AppearanceBounds(PdfAnnotation a)
    {
        PdfRect b = a.Bounds;
        PdfRect c = CaptionRect(a);
        double pad = EndTickHalfLength + a.EffectiveLineWidth;
        return new PdfRect(
            Math.Min(b.Left - pad, c.Left), Math.Max(b.Top + pad, c.Top),
            Math.Max(b.Right + pad, c.Right), Math.Min(b.Bottom - pad, c.Bottom));
    }

    /// <summary>Half-length of the perpendicular bar drawn at each end of a distance, in points.</summary>
    public const double EndTickHalfLength = 4;

    /// <summary>The two short bars across a distance's ends (each as a [from, to] pair).</summary>
    public static IReadOnlyList<(PdfPoint From, PdfPoint To)> EndTicks(IReadOnlyList<PdfPoint> points)
    {
        if (points.Count < 2)
        {
            return [];
        }

        PdfPoint a = points[0], b = points[1];
        double len = Distance(a, b);
        if (len < 1e-9)
        {
            return [];
        }

        // Unit normal to the segment.
        double nx = -(b.Y - a.Y) / len * EndTickHalfLength, ny = (b.X - a.X) / len * EndTickHalfLength;
        return
        [
            (new PdfPoint(a.X - nx, a.Y - ny), new PdfPoint(a.X + nx, a.Y + ny)),
            (new PdfPoint(b.X - nx, b.Y - ny), new PdfPoint(b.X + nx, b.Y + ny)),
        ];
    }
}
