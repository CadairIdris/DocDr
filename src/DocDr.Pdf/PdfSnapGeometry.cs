using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>What a measuring click snapped to.</summary>
public enum PdfSnapKind
{
    /// <summary>The end of a drawn segment (a corner).</summary>
    Endpoint,

    /// <summary>Where two drawn segments cross.</summary>
    Intersection,

    /// <summary>The middle of a drawn segment.</summary>
    Midpoint,

    /// <summary>The nearest point on a drawn segment.</summary>
    OnLine,
}

/// <summary>A snapped point (page space) and what it snapped to.</summary>
public readonly record struct PdfSnap(PdfPoint Point, PdfSnapKind Kind);

/// <summary>
/// A page's drawn linework as straight segments in unrotated page space (the same space as
/// DocDr's annotations), with a grid index, for snapping measurements to the drawing.
/// <para>
/// <see cref="Extract"/> walks every path object — inside nested Form XObjects too, folding each
/// object's matrix into its parent's — reading move / line / cubic segments (cubics flattened)
/// and closing subpaths. Glyph outlines, images, clipping and optional-content visibility are
/// not interpreted. Extraction stops at <see cref="MaxSegments"/> (reported by
/// <see cref="Truncated"/>) so a pathological page can't stall the UI.
/// </para>
/// </summary>
public sealed class PdfSnapGeometry
{
    /// <summary>Segment cap per page — a dense drawing can have tens of thousands of strokes.</summary>
    public const int MaxSegments = 20_000;

    /// <summary>Nested Form XObject depth cap (a cyclic or absurdly deep form tree stops here).</summary>
    public const int MaxFormDepth = 16;

    /// <summary>Segments considered when looking for intersections near the pointer.</summary>
    private const int MaxIntersectionCandidates = 48;

    private const int PageObjPath = 2;
    private const int PageObjForm = 5;
    private const int SegmentLineTo = 0;
    private const int SegmentBezierTo = 1;
    private const int SegmentMoveTo = 2;
    private const int BezierSteps = 8;
    private const double CellSize = 24;

    private readonly (PdfPoint A, PdfPoint B)[] _segments;
    private readonly Dictionary<(int X, int Y), List<int>> _grid = [];

    private PdfSnapGeometry((PdfPoint A, PdfPoint B)[] segments, bool truncated)
    {
        _segments = segments;
        Truncated = truncated;
        for (int i = 0; i < segments.Length; i++)
        {
            Index(i);
        }
    }

    public static PdfSnapGeometry Empty { get; } = new([], truncated: false);

    public IReadOnlyList<(PdfPoint A, PdfPoint B)> Segments => _segments;

    /// <summary>The page had more linework than <see cref="MaxSegments"/>; the rest was ignored.</summary>
    public bool Truncated { get; }

    /// <summary>Build from known segments (tests, or geometry from elsewhere).</summary>
    public static PdfSnapGeometry FromSegments(IEnumerable<(PdfPoint A, PdfPoint B)> segments)
    {
        var list = new List<(PdfPoint, PdfPoint)>();
        foreach ((PdfPoint a, PdfPoint b) in segments)
        {
            if (list.Count >= MaxSegments)
            {
                return new PdfSnapGeometry([.. list], truncated: true);
            }

            if (PdfMeasure.Distance(a, b) >= 0.1)
            {
                list.Add((a, b));
            }
        }

        return new PdfSnapGeometry([.. list], truncated: false);
    }

    /// <summary>Read a page's linework. Takes the document lock for the duration.</summary>
    public static PdfSnapGeometry Extract(PdfDocument document, int pageIndex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.ValidatePageIndex(pageIndex);

        return document.Locked(() =>
        {
            FpdfPageT? page = fpdfview.FPDF_LoadPage(document.Handle, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                return Empty;
            }

            try
            {
                var sink = new List<(PdfPoint, PdfPoint)>();
                bool truncated = false;
                int count = fpdf_edit.FPDFPageCountObjects(page);
                for (int i = 0; i < count && !truncated; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FpdfPageobjectT obj = fpdf_edit.FPDFPageGetObject(page, i);
                    truncated = Walk(obj, Matrix.Identity, depth: 0, sink);
                }

                return new PdfSnapGeometry([.. sink], truncated);
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        });
    }

    /// <summary>Add <paramref name="obj"/>'s segments under <paramref name="parent"/>; true once the cap is hit.</summary>
    private static bool Walk(FpdfPageobjectT? obj, Matrix parent, int depth, List<(PdfPoint, PdfPoint)> sink)
    {
        if (obj is null || obj.__Instance == IntPtr.Zero)
        {
            return false;
        }

        int type = fpdf_edit.FPDFPageObjGetType(obj);
        if (type is not (PageObjPath or PageObjForm))
        {
            return false;
        }

        Matrix m = parent;
        using (var fm = new FS_MATRIX_())
        {
            if (fpdf_edit.FPDFPageObjGetMatrix(obj, fm) != 0)
            {
                m = new Matrix(fm.A, fm.B, fm.C, fm.D, fm.E, fm.F).Then(parent);
            }
        }

        if (type == PageObjForm)
        {
            if (depth >= MaxFormDepth)
            {
                return false;
            }

            int children = fpdf_edit.FPDFFormObjCountObjects(obj);
            for (int i = 0; i < children; i++)
            {
                if (Walk(fpdf_edit.FPDFFormObjGetObject(obj, (uint)i), m, depth + 1, sink))
                {
                    return true;
                }
            }

            return false;
        }

        return ReadPath(obj, m, sink);
    }

    private static bool ReadPath(FpdfPageobjectT path, Matrix m, List<(PdfPoint, PdfPoint)> sink)
    {
        int segs = fpdf_edit.FPDFPathCountSegments(path);
        PdfPoint current = default, start = default;
        bool haveCurrent = false;
        var bezier = new List<PdfPoint>(3);

        for (int s = 0; s < segs; s++)
        {
            FpdfPathsegmentT? seg = fpdf_edit.FPDFPathGetPathSegment(path, s);
            if (seg is null || seg.__Instance == IntPtr.Zero)
            {
                continue;
            }

            float sx = 0, sy = 0;
            fpdf_edit.FPDFPathSegmentGetPoint(seg, ref sx, ref sy);
            PdfPoint p = m.Apply(sx, sy);
            int kind = fpdf_edit.FPDFPathSegmentGetType(seg);
            bool closes = fpdf_edit.FPDFPathSegmentGetClose(seg) != 0;

            switch (kind)
            {
                case SegmentMoveTo:
                    current = start = p;
                    haveCurrent = true;
                    bezier.Clear();
                    break;

                case SegmentLineTo when haveCurrent:
                    if (Add(sink, current, p))
                    {
                        return true;
                    }

                    current = p;
                    break;

                case SegmentBezierTo when haveCurrent:
                    // PDFium reports a cubic as three BEZIERTO points: control 1, control 2, end.
                    bezier.Add(p);
                    if (bezier.Count == 3)
                    {
                        PdfPoint prev = current;
                        for (int k = 1; k <= BezierSteps; k++)
                        {
                            PdfPoint q = Cubic(current, bezier[0], bezier[1], bezier[2], k / (double)BezierSteps);
                            if (Add(sink, prev, q))
                            {
                                return true;
                            }

                            prev = q;
                        }

                        current = bezier[2];
                        bezier.Clear();
                    }

                    break;

                default:
                    current = start = p;
                    haveCurrent = true;
                    break;
            }

            if (closes && haveCurrent && bezier.Count == 0)
            {
                if (Add(sink, current, start))
                {
                    return true;
                }

                current = start;
            }
        }

        return false;
    }

    private static bool Add(List<(PdfPoint, PdfPoint)> sink, PdfPoint a, PdfPoint b)
    {
        if (PdfMeasure.Distance(a, b) >= 0.1)
        {
            sink.Add((a, b));
        }

        return sink.Count >= MaxSegments;
    }

    private static PdfPoint Cubic(PdfPoint p0, PdfPoint p1, PdfPoint p2, PdfPoint p3, double t)
    {
        double u = 1 - t;
        double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
        return new PdfPoint(
            (a * p0.X) + (b * p1.X) + (c * p2.X) + (d * p3.X),
            (a * p0.Y) + (b * p1.Y) + (c * p2.Y) + (d * p3.Y));
    }

    // --- Snapping ------------------------------------------------------------------------

    /// <summary>
    /// The best snap within <paramref name="tolerance"/> (page points) of <paramref name="p"/>:
    /// a segment end, then a crossing, then a midpoint, then the nearest point on a line — the
    /// stronger target wins even if a weaker one is a little closer, the way CAD snaps feel.
    /// Null when nothing drawn is that close.
    /// </summary>
    public PdfSnap? Snap(PdfPoint p, double tolerance)
    {
        if (_segments.Length == 0 || !(tolerance > 0))
        {
            return null;
        }

        List<int> near = Nearby(p, tolerance);
        if (near.Count == 0)
        {
            return null;
        }

        PdfSnap? best = null;
        double bestDistance = double.MaxValue;

        void Consider(PdfPoint candidate, PdfSnapKind kind)
        {
            double d = PdfMeasure.Distance(p, candidate);
            if (d <= tolerance && d < bestDistance)
            {
                best = new PdfSnap(candidate, kind);
                bestDistance = d;
            }
        }

        foreach (int i in near)
        {
            Consider(_segments[i].A, PdfSnapKind.Endpoint);
            Consider(_segments[i].B, PdfSnapKind.Endpoint);
        }

        if (best is not null)
        {
            return best;
        }

        // Crossings among the segments closest to the pointer.
        List<int> closest = near
            .OrderBy(i => DistanceToSegment(p, _segments[i].A, _segments[i].B))
            .Take(MaxIntersectionCandidates)
            .ToList();
        for (int x = 0; x < closest.Count; x++)
        {
            for (int y = x + 1; y < closest.Count; y++)
            {
                if (Intersect(_segments[closest[x]], _segments[closest[y]]) is { } hit)
                {
                    Consider(hit, PdfSnapKind.Intersection);
                }
            }
        }

        if (best is not null)
        {
            return best;
        }

        foreach (int i in near)
        {
            (PdfPoint a, PdfPoint b) = _segments[i];
            Consider(new PdfPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2), PdfSnapKind.Midpoint);
        }

        if (best is not null)
        {
            return best;
        }

        foreach (int i in near)
        {
            Consider(ClosestOnSegment(p, _segments[i].A, _segments[i].B), PdfSnapKind.OnLine);
        }

        return best;
    }

    private List<int> Nearby(PdfPoint p, double tolerance)
    {
        var found = new HashSet<int>();
        int x0 = Cell(p.X - tolerance), x1 = Cell(p.X + tolerance);
        int y0 = Cell(p.Y - tolerance), y1 = Cell(p.Y + tolerance);
        for (int cx = x0; cx <= x1; cx++)
        {
            for (int cy = y0; cy <= y1; cy++)
            {
                if (_grid.TryGetValue((cx, cy), out List<int>? ids))
                {
                    foreach (int id in ids)
                    {
                        (PdfPoint a, PdfPoint b) = _segments[id];
                        if (DistanceToSegment(p, a, b) <= tolerance)
                        {
                            found.Add(id);
                        }
                    }
                }
            }
        }

        return [.. found];
    }

    /// <summary>Register segment <paramref name="i"/> in every grid cell it passes through
    /// (sampled at half-cell steps, so a long rule doesn't flood its whole bounding box).</summary>
    private void Index(int i)
    {
        (PdfPoint a, PdfPoint b) = _segments[i];
        double length = PdfMeasure.Distance(a, b);
        int steps = Math.Max(1, (int)Math.Ceiling(length / (CellSize / 2)));
        (int, int)? last = null;
        for (int k = 0; k <= steps; k++)
        {
            double t = k / (double)steps;
            (int, int) key = (Cell(a.X + ((b.X - a.X) * t)), Cell(a.Y + ((b.Y - a.Y) * t)));
            if (key == last)
            {
                continue;
            }

            last = key;
            if (!_grid.TryGetValue(key, out List<int>? ids))
            {
                ids = [];
                _grid[key] = ids;
            }

            if (ids.Count == 0 || ids[^1] != i)
            {
                ids.Add(i);
            }
        }
    }

    private static int Cell(double v) => (int)Math.Floor(v / CellSize);

    internal static double DistanceToSegment(PdfPoint p, PdfPoint a, PdfPoint b) =>
        PdfMeasure.Distance(p, ClosestOnSegment(p, a, b));

    private static PdfPoint ClosestOnSegment(PdfPoint p, PdfPoint a, PdfPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = (dx * dx) + (dy * dy);
        if (len2 < 1e-12)
        {
            return a;
        }

        double t = Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / len2, 0, 1);
        return new PdfPoint(a.X + (t * dx), a.Y + (t * dy));
    }

    private static PdfPoint? Intersect((PdfPoint A, PdfPoint B) s, (PdfPoint A, PdfPoint B) t)
    {
        double rx = s.B.X - s.A.X, ry = s.B.Y - s.A.Y;
        double qx = t.B.X - t.A.X, qy = t.B.Y - t.A.Y;
        double denom = (rx * qy) - (ry * qx);
        if (Math.Abs(denom) < 1e-9)
        {
            return null; // parallel / collinear
        }

        double wx = t.A.X - s.A.X, wy = t.A.Y - s.A.Y;
        double u = ((wx * qy) - (wy * qx)) / denom;
        double v = ((wx * ry) - (wy * rx)) / denom;
        const double eps = 1e-6;
        return u is >= -eps and <= 1 + eps && v is >= -eps and <= 1 + eps
            ? new PdfPoint(s.A.X + (u * rx), s.A.Y + (u * ry))
            : null;
    }

    /// <summary>A PDF affine matrix [a b c d e f] (row-vector convention: p' = p × M).</summary>
    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static Matrix Identity { get; } = new(1, 0, 0, 1, 0, 0);

        public PdfPoint Apply(double x, double y) => new((A * x) + (C * y) + E, (B * x) + (D * y) + F);

        /// <summary>This transform followed by <paramref name="next"/> (this × next).</summary>
        public Matrix Then(Matrix next) => new(
            (A * next.A) + (B * next.C),
            (A * next.B) + (B * next.D),
            (C * next.A) + (D * next.C),
            (C * next.B) + (D * next.D),
            (E * next.A) + (F * next.C) + next.E,
            (E * next.B) + (F * next.D) + next.F);
    }
}
