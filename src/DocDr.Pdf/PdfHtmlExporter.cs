using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DocDr.Pdf;

/// <summary>Tuning for <see cref="PdfHtmlExporter.Export"/>.</summary>
public sealed record HtmlExportOptions
{
    /// <summary>Resolution of the equation / table / figure images. Default 200 DPI.</summary>
    public double ImageDpi { get; init; } = 200;

    /// <summary>Embed images as data URIs in one self-contained file instead of a sidecar
    /// "{name}_files" folder. Default false (a large standard's HTML stays readable).</summary>
    public bool EmbedImages { get; init; }

    /// <summary>Under each table image, add the grid <see cref="PdfTableExtractor"/> reads from the
    /// text layer (marked unverified). Default true.</summary>
    public bool IncludeExtractedTables { get; init; } = true;
}

/// <summary>What <see cref="PdfHtmlExporter.Export"/> wrote.</summary>
public sealed record HtmlExportResult(
    string HtmlPath,
    string? ImageFolder,
    int Headings,
    int Paragraphs,
    int SuspectParagraphs,
    int Equations,
    int EquationsFlagged,
    int Tables,
    int Figures,
    int Images,
    IReadOnlyList<int> PagesWithoutText);

/// <summary>
/// Exports a text-layer PDF as structured HTML for an agent to read: headings, paragraphs and notes
/// as text — cleaned exactly like the chunk export (running headers / footers and licence stamps
/// removed, doubled glyphs dropped, Symbol-font pieces mapped, contents pages skipped) — and every
/// numbered equation, table and figure as an <b>image cropped from the page</b>, which is the
/// authoritative rendering, with the text layer alongside for searching (and, for tables, the grid
/// <see cref="PdfTableExtractor"/> reads, marked unverified). Page anchors carry the 1-based PDF page
/// and the printed page label.
/// </summary>
public static partial class PdfHtmlExporter
{
    [GeneratedRegex(@"^NOTE\b")]
    private static partial Regex NoteStart();

    [GeneratedRegex(@"[^A-Za-z0-9.\-]+")]
    private static partial Regex NotIdChar();

    public static HtmlExportResult Export(
        PdfDocument document, string htmlPath, HtmlExportOptions? options = null,
        IProgress<int>? pagesDone = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(htmlPath);
        options ??= new HtmlExportOptions();

        PdfRagChunker.DocumentAnalysis a = PdfRagChunker.Analyse(document, cancellationToken);
        List<PdfRagChunker.FlowItem> flow = PdfRagChunker.BuildFlow(a, (p, page) =>
        {
            pagesDone?.Report(p + 1);
            return PdfPageRegions.Detect(page, PdfPageRegions.ReadGraphics(document, p));
        }, cancellationToken);

        string stem = Path.GetFileNameWithoutExtension(htmlPath);
        string? imageFolder = options.EmbedImages ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(htmlPath))!, stem + "_files");
        if (imageFolder is not null)
        {
            Directory.CreateDirectory(imageFolder);
        }

        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        string UniqueId(string id)
        {
            id = NotIdChar().Replace(id, "-").Trim('-');
            if (id.Length == 0)
            {
                id = "x";
            }

            int n = ids.GetValueOrDefault(id) + 1;
            ids[id] = n;
            return n == 1 ? id : $"{id}-{n}";
        }

        int headings = 0, paragraphs = 0, suspectParagraphs = 0, equations = 0, flagged = 0, tables = 0, figures = 0, images = 0;
        var body = new StringBuilder();
        var toc = new StringBuilder();
        int lastPage = -1;

        string? ImageSrc(int page, PageRegion region, string id)
        {
            byte[]? png;
            try
            {
                png = RenderRegionPng(document, page, region.Bounds, options.ImageDpi, cancellationToken);
            }
            catch (PdfException)
            {
                return null;
            }

            if (png is null)
            {
                return null;
            }

            images++;
            if (imageFolder is null)
            {
                return "data:image/png;base64," + Convert.ToBase64String(png);
            }

            string name = $"p{page + 1:D4}-{id}.png";
            File.WriteAllBytes(Path.Combine(imageFolder, name), png);
            return $"{Uri.EscapeDataString(stem + "_files")}/{Uri.EscapeDataString(name)}";
        }

        foreach (PdfRagChunker.FlowItem item in flow)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Page != lastPage)
            {
                lastPage = item.Page;
                string label = document.GetPageLabel(item.Page) ?? (item.Page + 1).ToString(CultureInfo.InvariantCulture);
                body.Append(CultureInfo.InvariantCulture,
                    $"<div class=\"page\" id=\"page-{item.Page + 1}\" data-pdf-page=\"{item.Page + 1}\" data-page-label=\"{Attr(label)}\"><span>p. {Text(label)}</span></div>\n");
            }

            if (item.Para is { } para)
            {
                string text = para.Text;
                if (item.IsHeading || PdfRagChunker.IsStandaloneHeading(text))
                {
                    int level = Math.Clamp(PdfRagChunker.HeadingDepth(text) + 1, 2, 6);
                    string id = UniqueId("sec-" + (PdfRagChunker.HeadingNumber(text) ?? text[..Math.Min(40, text.Length)]));
                    body.Append(CultureInfo.InvariantCulture, $"<h{level} id=\"{id}\" data-pdf-page=\"{item.Page + 1}\">{Text(text)}</h{level}>\n");
                    toc.Append(CultureInfo.InvariantCulture, $"<li class=\"l{level}\"><a href=\"#{id}\">{Text(text)}</a></li>\n");
                    headings++;
                    continue;
                }

                paragraphs++;
                var classes = new List<string>();
                var attrs = new StringBuilder();
                if (NoteStart().IsMatch(text))
                {
                    classes.Add("note");
                }

                (int unmapped, int unexpected) = PdfTextQuality.Count(text, a.Profile);
                IReadOnlyList<string> repeated = PdfTextQuality.RepeatedLetterTokens(text);
                if (unmapped + unexpected > 0 || repeated.Count > 0)
                {
                    suspectParagraphs++;
                    classes.Add("suspect");
                    var why = new List<string>();
                    if (unmapped + unexpected > 0)
                    {
                        why.Add($"{unmapped + unexpected} character(s) the font doesn't map to real symbols");
                    }

                    if (repeated.Count > 0)
                    {
                        why.Add("letters repeated by a broken subscript map: " + string.Join(", ", repeated));
                    }

                    attrs.Append(CultureInfo.InvariantCulture, $" data-quality=\"suspect\" title=\"{Attr(string.Join("; ", why))}\"");
                }
                else if (para.HasMath)
                {
                    classes.Add("math");
                    attrs.Append(" data-quality=\"check\"");
                }

                string cls = classes.Count > 0 ? $" class=\"{string.Join(' ', classes)}\"" : string.Empty;
                body.Append(CultureInfo.InvariantCulture, $"<p{cls}{attrs}>{Text(text)}</p>\n");
                continue;
            }

            if (item.Region is not { } region)
            {
                continue;
            }

            string bbox = Bbox(region.Bounds, a.Pages[item.Page]!.PageBox);
            switch (region.Kind)
            {
                case PageRegionKind.Equation:
                {
                    equations++;
                    RagEquation eq = PdfRagChunker.ToRagEquation(region.Equation!, item.Page, a.Pages[item.Page]!.PageBox, a.Profile);
                    flagged += eq.Quality == "suspect" ? 1 : 0;
                    string id = UniqueId("eq-" + region.Label.Trim('(', ')'));
                    string? src = ImageSrc(item.Page, region, id);
                    body.Append(CultureInfo.InvariantCulture,
                        $"<figure class=\"equation\" id=\"{id}\" data-label=\"{Attr(region.Label)}\" data-pdf-page=\"{item.Page + 1}\" data-bbox=\"{bbox}\" data-quality=\"{eq.Quality}\"");
                    if (eq.Issues.Count > 0)
                    {
                        body.Append(CultureInfo.InvariantCulture, $" title=\"{Attr(string.Join("; ", eq.Issues))}\"");
                    }

                    body.Append(">\n");
                    if (src is not null)
                    {
                        body.Append(CultureInfo.InvariantCulture, $"<img src=\"{Attr(src)}\" alt=\"{Attr(eq.RawText)}\">\n");
                    }

                    body.Append(CultureInfo.InvariantCulture, $"<figcaption>{Text(region.Label)}</figcaption>\n");
                    body.Append(CultureInfo.InvariantCulture,
                        $"<details class=\"text-layer\"><summary>Text layer</summary><code>{Text(eq.RawText)}</code></details>\n</figure>\n");
                    break;
                }

                case PageRegionKind.Table:
                case PageRegionKind.Figure:
                {
                    bool isTable = region.Kind == PageRegionKind.Table;
                    if (isTable)
                    {
                        tables++;
                    }
                    else
                    {
                        figures++;
                    }

                    string kind = isTable ? "table" : "figure";
                    string id = UniqueId(region.Label.Replace(' ', '-').ToLowerInvariant());
                    string? src = ImageSrc(item.Page, region, id);
                    body.Append(CultureInfo.InvariantCulture,
                        $"<figure class=\"{kind}\" id=\"{id}\" data-label=\"{Attr(region.Label)}\" data-pdf-page=\"{item.Page + 1}\" data-bbox=\"{bbox}\">\n");
                    body.Append(CultureInfo.InvariantCulture, $"<figcaption>{Text(region.Caption)}</figcaption>\n");
                    if (src is not null)
                    {
                        body.Append(CultureInfo.InvariantCulture, $"<img src=\"{Attr(src)}\" alt=\"{Attr(region.Caption)}\">\n");
                    }

                    if (isTable && options.IncludeExtractedTables && ExtractGrid(document, item.Page, region.Bounds) is { } grid)
                    {
                        body.Append("<details class=\"extracted\"><summary>Grid read from the text layer (unverified — check against the image)</summary>\n");
                        body.Append("<table data-verified=\"false\">\n");
                        foreach (IReadOnlyList<string> row in grid.Rows)
                        {
                            body.Append("<tr>");
                            foreach (string cell in row)
                            {
                                body.Append(CultureInfo.InvariantCulture, $"<td>{Text(cell)}</td>");
                            }

                            body.Append("</tr>\n");
                        }

                        body.Append("</table></details>\n");
                    }

                    if (item.RegionText.Length > 0)
                    {
                        body.Append(CultureInfo.InvariantCulture,
                            $"<details class=\"text-layer\"><summary>Text layer</summary><pre>{Text(item.RegionText)}</pre></details>\n");
                    }

                    body.Append("</figure>\n");
                    break;
                }
            }
        }

        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        html.Append(CultureInfo.InvariantCulture, $"<title>{Text(a.DocTitle)}</title>\n");
        html.Append(CultureInfo.InvariantCulture, $"<meta name=\"source\" content=\"{Attr(a.SourcePath)}\">\n");
        html.Append(CultureInfo.InvariantCulture, $"<meta name=\"generator\" content=\"DocDr\">\n");
        html.Append("<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n");
        html.Append(CultureInfo.InvariantCulture, $"<header><h1>{Text(a.DocTitle)}</h1>\n");
        html.Append(CultureInfo.InvariantCulture, $"<p class=\"source\">Source: {Text(a.SourcePath)} · {document.PageCount} pages</p>\n");
        html.Append(About);
        html.Append("</header>\n");
        if (toc.Length > 0)
        {
            html.Append("<nav><details><summary>Contents</summary><ul>\n").Append(toc).Append("</ul></details></nav>\n");
        }

        html.Append("<main>\n").Append(body).Append("</main>\n</body>\n</html>\n");
        File.WriteAllText(htmlPath, html.ToString(), new UTF8Encoding(false));

        return new HtmlExportResult(htmlPath, imageFolder, headings, paragraphs, suspectParagraphs,
            equations, flagged, tables, figures, images, a.PagesWithoutText);
    }

    /// <summary>A region of a page rendered at <paramref name="dpi"/> as PNG (3 pt padding), or null
    /// when it's empty.</summary>
    internal static byte[]? RenderRegionPng(PdfDocument document, int page, PdfRect rect, double dpi, CancellationToken ct)
    {
        PdfSize size = document.GetUnrotatedPageSize(page);
        PdfRotation rotation = document.GetPageRotation(page);
        PdfPoint crop = document.GetCropOrigin(page);
        double scale = dpi / PdfCoordinates.PointsPerInch;
        var padded = new PdfRect(rect.Left - 3, rect.Top + 3, rect.Right + 3, rect.Bottom - 3);
        DeviceRect d = PdfCoordinates.PageToDevice(padded, size, rotation, scale, crop);
        bool swap = rotation is PdfRotation.Clockwise90 or PdfRotation.CounterClockwise90;
        int fullW = (int)Math.Ceiling((swap ? size.Height : size.Width) * scale);
        int fullH = (int)Math.Ceiling((swap ? size.Width : size.Height) * scale);
        int x0 = Math.Clamp((int)Math.Floor(d.X), 0, fullW), y0 = Math.Clamp((int)Math.Floor(d.Y), 0, fullH);
        int x1 = Math.Clamp((int)Math.Ceiling(d.X + d.Width), 0, fullW), y1 = Math.Clamp((int)Math.Ceiling(d.Y + d.Height), 0, fullH);
        int w = Math.Min(x1 - x0, 6000), h = Math.Min(y1 - y0, 6000);
        if (w < 2 || h < 2)
        {
            return null;
        }

        RenderedPage r = new PageRenderer().Render(document, page, w, h, ct, new PageRenderRegion(fullW, fullH, x0, y0));
        return PngWriter.EncodeBgrx(r.PixelWidth, r.PixelHeight, r.Stride, r.Pixels);
    }

    private static TableGrid? ExtractGrid(PdfDocument document, int page, PdfRect bounds)
    {
        try
        {
            TableGrid grid = PdfTableExtractor.Extract(document, page, bounds);
            return grid.Rows.Count >= 2 && grid.Rows.Max(r => r.Count) >= 2 ? grid : null;
        }
        catch (Exception ex) when (ex is PdfException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>[left, top, right, bottom] in points from the top-left of the visible page.</summary>
    private static string Bbox(PdfRect r, PdfRect pageBox) => string.Join(',',
        new[] { r.Left - pageBox.Left, pageBox.Top - r.Top, r.Right - pageBox.Left, pageBox.Top - r.Bottom }
            .Select(v => Math.Round(v, 1).ToString(CultureInfo.InvariantCulture)));

    private static string Text(string s) => WebUtility.HtmlEncode(s);

    private static string Attr(string s) => WebUtility.HtmlEncode(s);

    private const string About = """
        <details class="about"><summary>How to read this export</summary>
        <ul>
        <li>Text is the PDF's text layer, cleaned: running headers/footers and licence stamps removed, contents pages skipped.</li>
        <li><b>Equations, tables and figures are images cropped from the page — treat the image as authoritative.</b> The text layer under each is for searching; for stacked layouts (fractions, roots, indices) it is in drawing order and unreliable.</li>
        <li><code>data-quality="suspect"</code>: characters the font doesn't map, letters repeated by a broken subscript map, or an equation the text layer scrambles — read it from the image. <code>"check"</code>: maths flattened to one line.</li>
        <li>Table grids under "Grid read from the text layer" are unverified.</li>
        <li><code>data-pdf-page</code> is the 1-based PDF page; <code>data-page-label</code> the printed page number. <code>data-bbox</code> is [left, top, right, bottom] in points from the top-left of the page.</li>
        </ul></details>

        """;

    private const string Css = """
        body { font: 15px/1.5 system-ui, sans-serif; max-width: 52rem; margin: 2rem auto; padding: 0 1rem; color: #222; }
        h1 { font-size: 1.6rem; } h2 { font-size: 1.35rem; margin-top: 2rem; } h3 { font-size: 1.15rem; } h4, h5, h6 { font-size: 1rem; }
        .source { color: #666; }
        .page { border-top: 1px dashed #ccc; margin: 1.5rem 0 .5rem; text-align: right; font-size: .75rem; color: #999; }
        p.note { font-size: .9em; border-left: 3px solid #bbb; padding-left: .6rem; }
        p.suspect, figure[data-quality="suspect"] { background: #fff4e5; }
        figure { margin: 1rem 0; padding: .5rem; border: 1px solid #eee; }
        figure img { max-width: 100%; height: auto; display: block; }
        figure.equation { display: grid; grid-template-columns: 1fr auto; align-items: center; gap: .5rem; }
        figure.equation details { grid-column: 1 / -1; }
        figcaption { font-weight: 600; font-size: .9rem; }
        details.text-layer, details.extracted { font-size: .8rem; color: #555; }
        table { border-collapse: collapse; } td { border: 1px solid #ccc; padding: 2px 6px; }
        nav li.l3 { margin-left: 1rem; } nav li.l4 { margin-left: 2rem; } nav li.l5, nav li.l6 { margin-left: 3rem; }

        """;
}
