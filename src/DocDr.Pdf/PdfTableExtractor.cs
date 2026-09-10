using System.Text;
using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>A reconstructed table: rows of cell strings (ragged rows are padded on read).</summary>
public sealed class TableGrid
{
    private readonly List<List<string>> _rows;

    public TableGrid(IEnumerable<IEnumerable<string>> rows)
    {
        _rows = rows.Select(r => r.ToList()).ToList();
        ColumnCount = _rows.Count == 0 ? 0 : _rows.Max(r => r.Count);
    }

    public int RowCount => _rows.Count;

    public int ColumnCount { get; }

    public IReadOnlyList<IReadOnlyList<string>> Rows => _rows;

    public string Cell(int row, int column) =>
        row >= 0 && row < _rows.Count && column >= 0 && column < _rows[row].Count ? _rows[row][column] : string.Empty;

    /// <summary>RFC 4180 CSV (comma-separated, CRLF rows, quotes doubled).</summary>
    public string ToCsv() => Serialise(",", "\r\n", quote: true);

    /// <summary>Tab-separated (newlines and tabs inside cells collapsed to spaces).</summary>
    public string ToTsv() => Serialise("\t", "\n", quote: false);

    private string Serialise(string sep, string newline, bool quote)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < _rows.Count; r++)
        {
            for (int c = 0; c < ColumnCount; c++)
            {
                if (c > 0)
                {
                    sb.Append(sep);
                }

                string value = c < _rows[r].Count ? _rows[r][c] : string.Empty;
                if (quote)
                {
                    if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
                    {
                        sb.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
                    }
                    else
                    {
                        sb.Append(value);
                    }
                }
                else
                {
                    sb.Append(value.ReplaceLineEndings(" ").Replace('\t', ' '));
                }
            }

            sb.Append(newline);
        }

        return sb.ToString();
    }
}

/// <summary>Tuning for <see cref="PdfTableExtractor.Extract"/>.</summary>
public sealed record TableExtractOptions
{
    /// <summary>Use thin ruled lines in the region as row / column boundaries when enough are present.</summary>
    public bool UseRulingLines { get; init; } = true;

    /// <summary>Minimum width (points) of a clear vertical band that counts as a column gap.</summary>
    public double MinColumnGap { get; init; } = 3.5;
}

/// <summary>
/// Reconstructs a table from a rectangular region of a page. Rows and columns come from thin ruled
/// lines when the table has them, otherwise from clustering the character boxes by row and finding
/// the vertical whitespace channels between columns. Targets linear code tables (material
/// properties, partial factors, section data); merged cells and multi-row headers are best-effort.
/// The <paramref name="region"/> is in unrotated MediaBox page space, like <see cref="PdfCharBox"/>.
/// </summary>
public static class PdfTableExtractor
{
    private const int PageObjPath = 2;
    private const int SegmentLineTo = 0; // PDFium FPDF_SEGMENT_LINETO

    public static TableGrid Extract(
        PdfDocument document, int pageIndex, PdfRect region, TableExtractOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.ValidatePageIndex(pageIndex);
        options ??= new TableExtractOptions();

        PdfRect area = Normalise(region);
        if (area.Width < 4 || area.Height < 4)
        {
            return new TableGrid([]);
        }

        var chars = PdfTextExtractor.GetCharBoxesWithAngle(document, pageIndex)
            // Keep upright (and, defensively, unknown-angle) glyphs; drop rotated margin text.
            .Where(c => c.Angle < 0 || Math.Abs(Math.Sin(c.Angle)) < 0.35)
            .Select(c => c.Box)
            .Where(c => c.Text.Length > 0 && Intersects(c.Box, area))
            .ToList();
        if (chars.Count == 0)
        {
            return new TableGrid([]);
        }

        (IReadOnlyList<double> hLines, IReadOnlyList<double> vLines) = options.UseRulingLines
            ? ReadRulingLines(document, pageIndex, area)
            : ([], []);

        double medianHeight = Median(chars.Where(c => !IsBlank(c.Text)).Select(c => Math.Max(1, c.Box.Height)).ToList());
        if (medianHeight <= 0)
        {
            medianHeight = 8;
        }

        IReadOnlyList<(double Top, double Bottom)> rows = hLines.Count >= 3
            ? Bands(hLines).Select(b => (Top: b.Hi, Bottom: b.Lo)).ToList()
            : ClusterRows(chars, medianHeight);
        if (rows.Count == 0)
        {
            return new TableGrid([]);
        }

        IReadOnlyList<(double Left, double Right)> columns = vLines.Count >= 3
            ? Bands(vLines).Select(b => (Left: b.Lo, Right: b.Hi)).OrderBy(c => c.Left).ToList()
            : ColumnsFromWhitespace(chars, rows, area, medianHeight, options.MinColumnGap);
        if (columns.Count == 0)
        {
            columns = [(area.Left, area.Right)];
        }

        var cells = new List<List<StringBuilder>>();
        for (int r = 0; r < rows.Count; r++)
        {
            cells.Add(Enumerable.Range(0, columns.Count).Select(_ => new StringBuilder()).ToList());
        }

        // A char that sits well outside every row/column band isn't in the table — most often
        // the rotated "uncontrolled copy" strip up the page margin that a loose selection grabs,
        // or a footnote just past the last rule. Snapping it to the nearest cell invents columns.
        double rowSlop = medianHeight * 1.5;
        double colSlop = medianHeight * 3;

        foreach (PdfCharBox ch in chars.OrderBy(c => c.Index))
        {
            double cy = (ch.Box.Top + ch.Box.Bottom) / 2;
            double cx = (ch.Box.Left + ch.Box.Right) / 2;
            int row = IndexOfBand(rows, cy, static b => (b.Top, b.Bottom), rowSlop);
            int col = IndexOfBand(columns, cx, static b => (b.Right, b.Left), colSlop);
            if (row >= 0 && col >= 0)
            {
                cells[row][col].Append(ch.Text);
            }
        }

        var grid = cells
            .Select(row => row.Select(cell => Collapse(cell.ToString())).ToList())
            .ToList();

        return new TableGrid(TrimEmptyEdges(grid));
    }

    // --- Rows -------------------------------------------------------------------------------

    private static IReadOnlyList<(double Top, double Bottom)> ClusterRows(
        IReadOnlyList<PdfCharBox> chars, double medianHeight)
    {
        var centers = chars
            .Where(c => !IsBlank(c.Text))
            .Select(c => (c.Box.Top + c.Box.Bottom) / 2)
            .OrderByDescending(y => y)
            .ToList();
        if (centers.Count == 0)
        {
            return [];
        }

        double gap = medianHeight * 0.7;
        var bands = new List<(double Top, double Bottom)>();
        double top = centers[0], bottom = centers[0];
        foreach (double y in centers.Skip(1))
        {
            if (bottom - y > gap)
            {
                bands.Add((top + (medianHeight / 2), bottom - (medianHeight / 2)));
                top = bottom = y;
            }
            else
            {
                bottom = y;
            }
        }

        bands.Add((top + (medianHeight / 2), bottom - (medianHeight / 2)));
        return bands;
    }

    // --- Columns ---------------------------------------------------------------------------

    private static IReadOnlyList<(double Left, double Right)> ColumnsFromWhitespace(
        IReadOnlyList<PdfCharBox> chars,
        IReadOnlyList<(double Top, double Bottom)> rows,
        PdfRect area,
        double medianHeight,
        double minGap)
    {
        // Per row: merge character boxes into whitespace-separated runs (cell contents).
        var runsByRow = new List<List<(double L, double R)>>();
        for (int r = 0; r < rows.Count; r++)
        {
            runsByRow.Add([]);
        }

        var perRow = new List<List<(double L, double R)>>();
        for (int r = 0; r < rows.Count; r++)
        {
            perRow.Add([]);
        }

        foreach (PdfCharBox ch in chars)
        {
            if (IsBlank(ch.Text))
            {
                continue;
            }

            double cy = (ch.Box.Top + ch.Box.Bottom) / 2;
            int row = IndexOfBand(rows, cy, static b => (b.Top, b.Bottom), medianHeight * 1.5);
            if (row >= 0)
            {
                perRow[row].Add((ch.Box.Left, ch.Box.Right));
            }
        }

        for (int r = 0; r < rows.Count; r++)
        {
            if (perRow[r].Count == 0)
            {
                continue;
            }

            var sorted = perRow[r].OrderBy(s => s.L).ToList();
            double lo = sorted[0].L, hi = sorted[0].R;
            foreach ((double l, double rr) in sorted.Skip(1))
            {
                if (l <= hi + medianHeight * 0.6)
                {
                    hi = Math.Max(hi, rr);
                }
                else
                {
                    runsByRow[r].Add((lo, hi));
                    lo = l;
                    hi = rr;
                }
            }

            runsByRow[r].Add((lo, hi));
        }

        // Column boundaries come from the "data" rows — those split into ≥ 2 cells and with no run
        // spanning most of the width (a caption / note / spanning header would hide the gaps).
        var dataRows = runsByRow
            .Where(rr => rr.Count >= 2 && rr.Max(s => s.R - s.L) < area.Width * 0.55)
            .ToList();
        if (dataRows.Count == 0)
        {
            dataRows = runsByRow.Where(rr => rr.Count >= 2).ToList();
        }

        if (dataRows.Count == 0)
        {
            return [(area.Left, area.Right)];
        }

        // Work within the x-span the data rows actually cover, not the whole selection. A loose
        // marquee that catches the page-margin "uncontrolled copy" strip would otherwise turn that
        // strip into a spurious first column.
        double spanLeft = Math.Max(area.Left, dataRows.Min(rr => rr.Min(s => s.L)) - medianHeight);
        double spanRight = Math.Min(area.Right, dataRows.Max(rr => rr.Max(s => s.R)) + medianHeight);
        double spanWidth = Math.Max(1, spanRight - spanLeft);

        int samples = Math.Max(80, (int)(spanWidth / Math.Max(1, medianHeight * 0.2)));
        double step = spanWidth / samples;
        int required = (int)Math.Ceiling(dataRows.Count * 0.6);

        var boundaries = new List<double> { spanLeft, spanRight };
        int runFrom = -1;
        for (int i = 0; i <= samples; i++)
        {
            double x = spanLeft + (i * step);
            // A row votes for a column gap at x only when x sits between two of its cells — no run
            // straddles x and there is still a cell to the right. That ignores the ragged right
            // edge (a short cell overhanging a wide header) which would otherwise split a column.
            int clear = dataRows.Count(rr =>
                rr.All(s => x < s.L - 0.5 || x > s.R + 0.5) && rr.Any(s => s.L > x));
            bool isClear = clear >= required;

            if (isClear && runFrom < 0)
            {
                runFrom = i;
            }
            else if (!isClear && runFrom >= 0)
            {
                TryBoundary(runFrom, i - 1);
                runFrom = -1;
            }
        }

        if (runFrom >= 0)
        {
            TryBoundary(runFrom, samples);
        }

        boundaries = boundaries.Distinct().OrderBy(v => v).ToList();
        var columns = new List<(double Left, double Right)>();
        for (int i = 0; i < boundaries.Count - 1; i++)
        {
            if (boundaries[i + 1] - boundaries[i] > 1)
            {
                columns.Add((boundaries[i], boundaries[i + 1]));
            }
        }

        return columns;

        void TryBoundary(int from, int to)
        {
            double left = spanLeft + (from * step);
            double right = spanLeft + (to * step);
            bool touchesEdge = left <= spanLeft + 0.5 || right >= spanRight - 0.5;

            // A gap in the middle just needs to be a hair wide; one at the edge must be a real
            // (empty) first / last column, so require it to be clearly wider than a margin.
            if (right - left < (touchesEdge ? minGap * 3 : minGap))
            {
                return;
            }

            boundaries.Add(touchesEdge && left <= spanLeft + 0.5 ? right : (left + right) / 2);
            if (touchesEdge && right >= spanRight - 0.5)
            {
                boundaries[^1] = left;
            }
        }
    }

    // --- Ruling lines --------------------------------------------------------------------

    private static (IReadOnlyList<double> Horizontal, IReadOnlyList<double> Vertical) ReadRulingLines(
        PdfDocument document, int pageIndex, PdfRect area)
    {
        return document.Locked(() =>
        {
            FpdfPageT? page = fpdfview.FPDF_LoadPage(document.Handle, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                return ((IReadOnlyList<double>)[], (IReadOnlyList<double>)[]);
            }

            try
            {
                var horizontal = new List<double>();
                var vertical = new List<double>();
                PdfRect wide = Inflate(area, 3);
                int count = fpdf_edit.FPDFPageCountObjects(page);
                for (int i = 0; i < count; i++)
                {
                    FpdfPageobjectT obj = fpdf_edit.FPDFPageGetObject(page, i);
                    if (obj is null || obj.__Instance == IntPtr.Zero || fpdf_edit.FPDFPageObjGetType(obj) != PageObjPath)
                    {
                        continue;
                    }

                    float l = 0, b = 0, r = 0, t = 0;
                    if (fpdf_edit.FPDFPageObjGetBounds(obj, ref l, ref b, ref r, ref t) == 0
                        || !Intersects(new PdfRect(l, t, r, b), wide))
                    {
                        continue;
                    }

                    // Path points are in the object's own space; fold in its matrix.
                    double ma = 1, mb = 0, mc = 0, md = 1, me = 0, mf = 0;
                    using (var m = new FS_MATRIX_())
                    {
                        if (fpdf_edit.FPDFPageObjGetMatrix(obj, m) != 0)
                        {
                            (ma, mb, mc, md, me, mf) = (m.A, m.B, m.C, m.D, m.E, m.F);
                        }
                    }

                    int segs = fpdf_edit.FPDFPathCountSegments(obj);
                    double px = 0, py = 0;
                    bool have = false;
                    for (int s = 0; s < segs; s++)
                    {
                        FpdfPathsegmentT? seg = fpdf_edit.FPDFPathGetPathSegment(obj, s);
                        if (seg is null || seg.__Instance == IntPtr.Zero)
                        {
                            continue;
                        }

                        float sx = 0, sy = 0;
                        fpdf_edit.FPDFPathSegmentGetPoint(seg, ref sx, ref sy);
                        double x = (ma * sx) + (mc * sy) + me;
                        double y = (mb * sx) + (md * sy) + mf;
                        // PDFium FPDF_SEGMENT_*: LINETO = 0, BEZIERTO = 1, MOVETO = 2.
                        int type = fpdf_edit.FPDFPathSegmentGetType(seg);

                        // A straight edge — either a stroked line or one side of a thin filled
                        // rectangle (how this family of PDFs draws its table rules).
                        if (type == SegmentLineTo && have)
                        {
                            double dx = Math.Abs(x - px), dy = Math.Abs(y - py);
                            // A rule spans (part of) the table; an edge longer than the region is a
                            // page border / background frame, not a cell divider.
                            if (dy <= 1.5 && dx >= 6 && dx <= area.Width + 12
                                && WithinBand(area.Bottom - 3, area.Top + 3, (y + py) / 2))
                            {
                                horizontal.Add((y + py) / 2);
                            }
                            else if (dx <= 1.5 && dy >= 6 && dy <= area.Height + 12
                                     && WithinBand(area.Left - 3, area.Right + 3, (x + px) / 2))
                            {
                                vertical.Add((x + px) / 2);
                            }
                        }

                        // After any segment there is a current point to draw the next edge from.
                        (px, py, have) = (x, y, true);
                    }
                }

                return ((IReadOnlyList<double>)Dedupe(horizontal), (IReadOnlyList<double>)Dedupe(vertical));
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        });
    }

    private static List<double> Dedupe(List<double> values)
    {
        values.Sort();
        var result = new List<double>();
        foreach (double v in values)
        {
            if (result.Count == 0 || Math.Abs(v - result[^1]) > 2)
            {
                result.Add(v);
            }
        }

        return result;
    }

    /// <summary>Consecutive coordinate lines → the bands between them (Hi &gt; Lo).</summary>
    private static List<(double Hi, double Lo)> Bands(IReadOnlyList<double> lines)
    {
        var sorted = lines.OrderByDescending(v => v).ToList();
        var bands = new List<(double Hi, double Lo)>();
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            if (sorted[i] - sorted[i + 1] > 2)
            {
                bands.Add((sorted[i], sorted[i + 1]));
            }
        }

        return bands;
    }

    // --- Helpers -------------------------------------------------------------------------

    /// <summary>Index of the band containing <paramref name="value"/>, else the nearest band if it
    /// is within <paramref name="maxSlop"/>, else -1 (the value is outside the table).</summary>
    private static int IndexOfBand<T>(
        IReadOnlyList<T> bands, double value, Func<T, (double Hi, double Lo)> range, double maxSlop)
    {
        int best = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < bands.Count; i++)
        {
            (double hi, double lo) = range(bands[i]);
            if (value <= hi && value >= lo)
            {
                return i;
            }

            double dist = value > hi ? value - hi : lo - value;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }

        return bestDist <= maxSlop ? best : -1;
    }

    private static List<List<string>> TrimEmptyEdges(List<List<string>> grid)
    {
        bool ColumnEmpty(int c) => grid.All(row => c >= row.Count || row[c].Length == 0);
        bool RowEmpty(List<string> row) => row.All(s => s.Length == 0);

        grid = grid.Where(r => !RowEmpty(r)).ToList();
        if (grid.Count == 0)
        {
            return grid;
        }

        int cols = grid.Max(r => r.Count);
        int first = 0, last = cols - 1;
        while (first < cols && ColumnEmpty(first))
        {
            first++;
        }

        while (last >= first && ColumnEmpty(last))
        {
            last--;
        }

        return grid
            .Select(r => Enumerable.Range(first, Math.Max(0, last - first + 1))
                .Select(c => c < r.Count ? r[c] : string.Empty).ToList())
            .ToList();
    }

    private static PdfRect Normalise(PdfRect r) => new(
        Math.Min(r.Left, r.Right), Math.Max(r.Top, r.Bottom),
        Math.Max(r.Left, r.Right), Math.Min(r.Top, r.Bottom));

    private static PdfRect Inflate(PdfRect r, double by) =>
        new(r.Left - by, r.Top + by, r.Right + by, r.Bottom - by);

    private static bool Intersects(PdfRect a, PdfRect b) =>
        a.Left < b.Right && a.Right > b.Left && a.Bottom < b.Top && a.Top > b.Bottom;

    private static bool WithinBand(double lo, double hi, double v) => v >= lo && v <= hi;

    private static bool IsBlank(string s) => string.IsNullOrWhiteSpace(s);

    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = true;
                continue;
            }

            if (space && sb.Length > 0)
            {
                sb.Append(' ');
            }

            space = false;
            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }
}
