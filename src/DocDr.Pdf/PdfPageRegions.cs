using System.Text.RegularExpressions;
using PDFiumCore;

namespace DocDr.Pdf;

internal enum PageRegionKind
{
    Equation,
    Table,
    Figure,
}

/// <summary>
/// A part of a page that the HTML export shows as a picture rather than as flowing text: a numbered
/// display equation, or a table / figure found from its caption.
/// </summary>
/// <param name="Bounds">Unrotated page space (MediaBox), like the char boxes.</param>
/// <param name="Label">"(8.94)", "Table 11.1", "Figure 3.2".</param>
/// <param name="Caption">The caption line as printed (tables / figures), or the label (equations).</param>
internal sealed record PageRegion(PageRegionKind Kind, PdfRect Bounds, string Label, string Caption)
{
    /// <summary>Index into <see cref="ChunkPage.Lines"/> of the caption line, or -1.</summary>
    public int CaptionLine { get; init; } = -1;

    /// <summary>Lines continuing a wrapped caption (part of the caption, not the flow).</summary>
    public IReadOnlyList<int> CaptionContinuation { get; init; } = [];

    /// <summary>The located equation, for <see cref="PageRegionKind.Equation"/>.</summary>
    public ChunkEquation? Equation { get; init; }
}

/// <summary>
/// Finds the equations, tables and figures on a page for the HTML export.
/// <list type="bullet">
/// <item>Equations come from <see cref="PdfChunkText"/>'s located display equations.</item>
/// <item>A table or figure starts from its caption line ("Table 11.1 — …", "Figure 3.2: …") and takes
/// the cluster of drawing graphics (ruling lines, cell fills, vector art, images —
/// <see cref="ReadGraphics"/>) touching it: below the caption for a table, above it for a figure
/// (each falls back to the other side), never past the next caption. A table with no rules at all
/// falls back to the block of closely spaced lines under its caption.</item>
/// <item>An equation inside a table or figure belongs to it.</item>
/// </list>
/// </summary>
internal static partial class PdfPageRegions
{
    private const int PageObjText = 1;

    // "Table 11.1 — Values …", "Table D6.1. Shear …", "Figure 3.2: Stress-strain …" — a capital must
    // follow, so prose that merely starts with "Table 3.1 gives …" isn't a caption.
    [GeneratedRegex(@"^(Table|Figure)\s+([A-Z]{0,2}\.?\d{1,3}(?:\.\d{1,3})*[a-z]?)\.?(?:\s*[—–:\-]\s*|\s+)(?=\p{Lu}|\()")]
    private static partial Regex CaptionStart();

    /// <summary>Bounds of every non-text page object (paths, images, shadings, forms), in page space.</summary>
    public static IReadOnlyList<PdfRect> ReadGraphics(PdfDocument document, int pageIndex) =>
        document.Locked(() =>
        {
            FpdfPageT? page = fpdfview.FPDF_LoadPage(document.Handle, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                return (IReadOnlyList<PdfRect>)[];
            }

            try
            {
                var result = new List<PdfRect>();
                int count = fpdf_edit.FPDFPageCountObjects(page);
                for (int i = 0; i < count; i++)
                {
                    FpdfPageobjectT obj = fpdf_edit.FPDFPageGetObject(page, i);
                    if (obj is null || obj.__Instance == IntPtr.Zero || fpdf_edit.FPDFPageObjGetType(obj) == PageObjText)
                    {
                        continue;
                    }

                    float l = 0, b = 0, r = 0, t = 0;
                    if (fpdf_edit.FPDFPageObjGetBounds(obj, ref l, ref b, ref r, ref t) != 0)
                    {
                        result.Add(new PdfRect(l, t, r, b));
                    }
                }

                return result;
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        });

    public static IReadOnlyList<PageRegion> Detect(ChunkPage page, IReadOnlyList<PdfRect> graphics)
    {
        PdfRect box = page.PageBox;
        double pageArea = Math.Max(1, (box.Right - box.Left) * (box.Top - box.Bottom));
        double band = (box.Top - box.Bottom) * PdfChunkText.MarginBand;
        // Drop page frames / backgrounds and header / footer rules.
        var usable = graphics.Where(g => g.Width >= 0 && g.Height >= 0 && (g.Width > 0 || g.Height > 0)
            && g.Width * g.Height < pageArea * 0.5
            && !(g.Bottom >= box.Top - band) && !(g.Top <= box.Bottom + band)).ToList();

        var captions = new List<(int Line, PageRegionKind Kind, string Label, string Caption, PdfRect Bounds, List<int> More)>();
        for (int li = 0; li < page.Lines.Count; li++)
        {
            ChunkLine line = page.Lines[li];
            string text = line.Text.Trim();
            if (line.InMargin || line.Bounds.Width <= 0 || text.Length > 200
                || CaptionStart().Match(text) is not { Success: true } m)
            {
                continue;
            }

            var kind = m.Groups[1].Value == "Table" ? PageRegionKind.Table : PageRegionKind.Figure;
            // A wrapped caption carries on mid-phrase on the line straight below it: lower-case start,
            // within half a line.
            string full = text;
            PdfRect bounds = line.Bounds;
            var more = new List<int>();
            for (int k = li + 1; k < page.Lines.Count; k++)
            {
                ChunkLine next = page.Lines[k];
                string nt = next.Text.Trim();
                if (nt.Length == 0 || next.Bounds.Width <= 0 || !char.IsLower(nt[0])
                    || bounds.Bottom - next.Bounds.Top > bounds.Height * 0.6 || next.Bounds.Top > bounds.Bottom + 1)
                {
                    break;
                }

                full += " " + nt;
                bounds = Union(bounds, next.Bounds);
                more.Add(k);
            }

            captions.Add((li, kind, $"{m.Groups[1].Value} {m.Groups[2].Value}", System.Text.RegularExpressions.Regex.Replace(full, @"\s+", " "), bounds, more));
        }

        var regions = new List<PageRegion>();
        foreach (var cap in captions)
        {
            // Never reach past the neighbouring captions.
            double lowerLimit = captions.Where(c => c.Bounds.Top < cap.Bounds.Bottom).Select(c => c.Bounds.Top)
                .DefaultIfEmpty(box.Bottom).Max();
            double upperLimit = captions.Where(c => c.Bounds.Bottom > cap.Bounds.Top).Select(c => c.Bounds.Bottom)
                .DefaultIfEmpty(box.Top).Min();

            PdfRect? found = cap.Kind == PageRegionKind.Table
                ? Below(cap.Bounds, lowerLimit, usable) ?? Above(cap.Bounds, upperLimit, usable) ?? TextBlockBelow(page, cap.Line, lowerLimit)
                : Above(cap.Bounds, upperLimit, usable) ?? AboveKey(page, cap.Line, upperLimit, usable)
                    ?? Below(cap.Bounds, lowerLimit, usable);
            if (found is not { } r)
            {
                continue;
            }

            // Take in text that pokes just outside the rules (a header row above the top rule, labels
            // round a drawing) when its centre is within a few points.
            PdfRect grown = r;
            foreach (ChunkLine line in page.Lines)
            {
                // A figure's key / legend sits between the drawing and its caption: take it in.
                bool keyLine = cap.Kind == PageRegionKind.Figure && r.Bottom >= cap.Bounds.Top - 2
                    && line.Bounds.Bottom >= cap.Bounds.Top - 1 && line.Bounds.Top <= r.Bottom + 1;
                if (line.InMargin)
                {
                    continue;
                }

                if (keyLine && line.Bounds.Width > 0 && !Overlaps(line.Bounds, cap.Bounds))
                {
                    grown = Union(grown, line.Bounds);
                    continue;
                }

                if (line.Bounds.Width > 0 && !string.IsNullOrWhiteSpace(line.Text)
                    && Overlaps(Inflate(r, 2), line.Bounds) && line.Bounds.Top <= upperLimit && line.Bounds.Bottom >= lowerLimit
                    && !Overlaps(line.Bounds, cap.Bounds))
                {
                    grown = Union(grown, line.Bounds);
                }
            }

            regions.Add(new PageRegion(cap.Kind, grown, cap.Label, cap.Caption) { CaptionLine = cap.Line, CaptionContinuation = cap.More });
        }

        // Equations: crop the whole display block — the band between the text around it, across the
        // text column — not the char boxes, which miss radicals, fraction bars and big brackets drawn
        // as vector lines or as glyphs with no usable box.
        var equations = page.Equations
            .Where(e => !regions.Any(r => Contains(r.Bounds, Centre(e.Bounds))))
            .OrderByDescending(e => Centre(e.LabelBounds).Y).ToList();
        (double colLeft, double colRight) = TextColumn(page);
        for (int i = 0; i < equations.Count; i++)
        {
            ChunkEquation e = equations[i];
            PdfRect label = e.LabelBounds.Width > 0 ? e.LabelBounds : e.Bounds;
            double h = Math.Max(4, label.Height);
            double cy = Centre(label).Y;

            // Up to the nearest prose line above (or 100 pt), and no further than the blank gap that
            // separates this equation from the one above it; likewise downwards.
            // Never into the header / footer band (page number, licence stamp).
            double top = Math.Min(Math.Min(cy + 100, box.Top - band), BoundaryAbove(page, label, h) ?? double.MaxValue);
            double bottom = Math.Max(Math.Max(cy - 100, box.Bottom + band), BoundaryBelow(page, label, h) ?? double.MinValue);
            if (i > 0)
            {
                top = Math.Min(top, Separator(page, usable, equations[i - 1].LabelBounds, label));
            }

            if (i + 1 < equations.Count)
            {
                bottom = Math.Max(bottom, Separator(page, usable, label, equations[i + 1].LabelBounds));
            }

            // Never smaller than what the text layer already saw.
            top = Math.Max(top, e.Bounds.Top);
            bottom = Math.Min(bottom, e.Bounds.Bottom);
            var block = new PdfRect(Math.Min(colLeft, e.Bounds.Left), top, Math.Max(colRight, e.Bounds.Right), bottom);
            regions.Add(new PageRegion(PageRegionKind.Equation, block, e.Label, e.Label) { Equation = e });
        }

        return regions;
    }

    // A line that is running text, not part of a display formula: two or more real words, or one of
    // the words that introduce / qualify a formula ("where", "with", "for"…), a paragraph number, a
    // heading or a caption — or anything in the header / footer band.
    [GeneratedRegex(@"^(?:where|with|for|in which|and|or|if|but|see|NOTE)\b|^\(\d{1,3}\)\s|^\d+(?:\.\d+)+\.?\s+\p{Lu}")]
    private static partial Regex TextLineStart();

    private static bool IsTextLine(ChunkLine line) =>
        line.Bounds.Width > 0 && !string.IsNullOrWhiteSpace(line.Text)
        && (line.InMargin || PdfChunkText.IsProse(line.Text) || TextLineStart().IsMatch(line.Text.Trim())
            || CaptionStart().IsMatch(line.Text.Trim()));

    /// <summary>Just under (1.5 pt clear of) the nearest text line above the label.</summary>
    private static double? BoundaryAbove(ChunkPage page, PdfRect label, double h)
    {
        var above = page.Lines.Where(l => IsTextLine(l) && l.Bounds.Bottom >= label.Top + (h * 0.25)).ToList();
        return above.Count == 0 ? null : above.Min(l => l.Bounds.Bottom) - 1.5;
    }

    private static double? BoundaryBelow(ChunkPage page, PdfRect label, double h)
    {
        var below = page.Lines.Where(l => IsTextLine(l) && l.Bounds.Top <= label.Bottom - (h * 0.25)).ToList();
        return below.Count == 0 ? null : below.Max(l => l.Bounds.Top) + 1.5;
    }

    /// <summary>Where to split two stacked display equations: the middle of the widest blank gap in
    /// the ink (glyphs and graphics) between their labels — so a tall fraction's numerator stays with
    /// its own equation — or halfway between the labels when nothing is blank.</summary>
    private static double Separator(ChunkPage page, List<PdfRect> graphics, PdfRect upperLabel, PdfRect lowerLabel)
    {
        double hi = Centre(upperLabel).Y, lo = Centre(lowerLabel).Y;
        var ink = page.Lines.Where(l => l.Bounds.Width > 0 && !l.InMargin).Select(l => (l.Bounds.Bottom, l.Bounds.Top))
            .Concat(graphics.Select(g => (g.Bottom, g.Top)))
            .Where(r => r.Top > lo && r.Bottom < hi)
            .Select(r => (Lo: Math.Max(lo, r.Bottom), Hi: Math.Min(hi, r.Top)))
            .OrderByDescending(r => r.Hi).ToList();

        double bestGap = 0, best = (hi + lo) / 2, cursor = hi;
        foreach ((double rlo, double rhi) in ink)
        {
            if (cursor - rhi > bestGap)
            {
                bestGap = cursor - rhi;
                best = (cursor + rhi) / 2;
            }

            cursor = Math.Min(cursor, rlo);
        }

        if (cursor - lo > bestGap)
        {
            best = (cursor + lo) / 2;
        }

        return best;
    }

    /// <summary>The page's text column: the 10th / 90th percentile of the left / right edges of its
    /// longer body lines (so a rotated margin watermark doesn't widen it).</summary>
    private static (double Left, double Right) TextColumn(ChunkPage page)
    {
        var lines = page.Lines.Where(l => !l.InMargin && l.Bounds.Width > 0 && l.Text.Trim().Length >= 15
            && l.Bounds.Width > l.Bounds.Height).ToList();
        if (lines.Count == 0)
        {
            return (page.PageBox.Left + 50, page.PageBox.Right - 50);
        }

        var lefts = lines.Select(l => l.Bounds.Left).Order().ToList();
        var rights = lines.Select(l => l.Bounds.Right).Order().ToList();
        return (lefts[(int)(lefts.Count * 0.1)], rights[Math.Min(rights.Count - 1, (int)(rights.Count * 0.9))]);
    }

    private static PdfPoint Centre(PdfRect r) => new((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);

    private static bool Contains(PdfRect r, PdfPoint p) => p.X >= r.Left && p.X <= r.Right && p.Y >= r.Bottom && p.Y <= r.Top;

    /// <summary>The cluster of graphics starting just under the caption (within 45 pt) and every
    /// graphic touching it, down to <paramref name="lowerLimit"/>.</summary>
    private static PdfRect? Below(PdfRect cap, double lowerLimit, List<PdfRect> graphics) =>
        Cluster(graphics.Where(g => g.Top <= cap.Bottom + 2 && g.Bottom >= lowerLimit - 1).ToList(),
            seed: g => g.Top >= cap.Bottom - 45);

    private static PdfRect? Above(PdfRect cap, double upperLimit, List<PdfRect> graphics) =>
        Cluster(graphics.Where(g => g.Bottom >= cap.Top - 2 && g.Top <= upperLimit + 1).ToList(),
            seed: g => g.Bottom <= cap.Top + 45);

    /// <summary>A figure whose key / legend sits between the drawing and the caption: the nearest
    /// graphics up to 300 pt above, provided every line in between is key-like — no sentence prose
    /// (8+ words ending in "." or ":") and no other caption.</summary>
    private static PdfRect? AboveKey(ChunkPage page, int captionLine, double upperLimit, List<PdfRect> graphics)
    {
        PdfRect cap = page.Lines[captionLine].Bounds;
        var above = graphics.Where(g => g.Bottom >= cap.Top - 2 && g.Bottom <= cap.Top + 300 && g.Top <= upperLimit + 1).ToList();
        if (above.Count == 0)
        {
            return null;
        }

        double nearest = above.Min(g => g.Bottom);
        foreach (ChunkLine line in page.Lines)
        {
            if (line.InMargin || line.Bounds.Width <= 0 || line.Bounds.Bottom < cap.Top - 1 || line.Bounds.Top > nearest + 1)
            {
                continue;
            }

            string t = line.Text.Trim();
            int words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if ((words >= 8 && (t.EndsWith('.') || t.EndsWith(':'))) || CaptionStart().IsMatch(t))
            {
                return null;
            }
        }

        return Cluster(above, seed: g => g.Bottom <= nearest + 45);
    }

    private static PdfRect? Cluster(List<PdfRect> candidates, Func<PdfRect, bool> seed)
    {
        var seeds = candidates.Where(seed).ToList();
        if (seeds.Count == 0)
        {
            return null;
        }

        PdfRect bounds = seeds.Aggregate(Union);
        var taken = new HashSet<int>(candidates.Select((g, i) => (g, i)).Where(x => seed(x.g)).Select(x => x.i));
        bool grew = true;
        while (grew)
        {
            grew = false;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!taken.Contains(i) && Overlaps(Inflate(bounds, 4), candidates[i]))
                {
                    taken.Add(i);
                    bounds = Union(bounds, candidates[i]);
                    grew = true;
                }
            }
        }

        return bounds.Width >= 20 && bounds.Height >= 8 ? bounds : null;
    }

    /// <summary>An unruled table: the lines under the caption while they stay closely spaced
    /// (gap ≤ 2.2 × the line height), stopping at a heading-like or prose line, an equation or
    /// another caption.</summary>
    private static PdfRect? TextBlockBelow(ChunkPage page, int captionLine, double lowerLimit)
    {
        PdfRect cap = page.Lines[captionLine].Bounds;
        var below = page.Lines.Where((l, k) => k != captionLine && l.Bounds.Width > 0 && !string.IsNullOrWhiteSpace(l.Text)
                && l.Bounds.Top <= cap.Bottom + 1 && l.Bounds.Bottom >= lowerLimit && !l.InMargin)
            .OrderByDescending(l => l.Bounds.Top).ToList();
        double h = Math.Max(4, cap.Height);
        double previousBottom = cap.Bottom;
        var taken = new List<PdfRect>();
        foreach (ChunkLine line in below)
        {
            if (previousBottom - line.Bounds.Top > 2.2 * h
                || CaptionStart().IsMatch(line.Text.Trim())
                || page.Equations.Any(e => Overlaps(e.Bounds, line.Bounds))
                || System.Text.RegularExpressions.Regex.Count(line.Text, @"\b\p{Ll}{4,}\b") >= 6)
            {
                break;
            }

            taken.Add(line.Bounds);
            previousBottom = Math.Min(previousBottom, line.Bounds.Bottom);
        }

        return taken.Count >= 2 ? taken.Aggregate(Union) : null;
    }

    private static PdfRect Inflate(PdfRect r, double d) => new(r.Left - d, r.Top + d, r.Right + d, r.Bottom - d);

    private static PdfRect Union(PdfRect a, PdfRect b) =>
        new(Math.Min(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));

    private static bool Overlaps(PdfRect a, PdfRect b) =>
        a.Left <= b.Right && b.Left <= a.Right && a.Bottom <= b.Top && b.Bottom <= a.Top;
}
