using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocDr.Pdf;

/// <summary>
/// One retrieval-sized passage of a document, ready to embed for RAG. Serialised with snake_case
/// keys (<c>source_path</c>, <c>page_start</c>, …). <see cref="Id"/> is stable across re-exports;
/// <see cref="TokenEstimate"/> is <c>Text.Length / 4</c>.
/// </summary>
/// <param name="SourcePath">The PDF's file name (no folders — exports move between machines).</param>
/// <param name="PageStart">0-based first page (see <see cref="PdfPageStart"/> for 1-based).</param>
/// <param name="SectionTitle">The section in force where the chunk's own text starts.</param>
/// <param name="Quality"><c>ok</c>; <c>check</c> — contains maths whose 2-D layout (fractions, roots,
/// exponents, indices) is flattened, or a flattened table; or <c>suspect</c> — contains characters the
/// PDF can't vouch for (unmapped glyphs, symbols from a mis-mapped font, letters repeated by a broken
/// subscript map) or an equation the text layer scrambles.</param>
/// <param name="Issues">Why, in plain words an agent can act on; empty when <c>ok</c>.</param>
/// <param name="PdfPageStart">1-based first page — the PDF viewer's page number.</param>
/// <param name="PageLabelStart">The printed page label of the first page ("xii", "45"), or the 1-based
/// number when the PDF has no labels.</param>
/// <param name="Sections">Every section the chunk's text spans, in order.</param>
/// <param name="ContextBefore">The end of the previous chunk (the overlap), kept out of
/// <see cref="Text"/> so nothing is repeated inline; empty at a section start.</param>
/// <param name="Equations">Numbered display equations in this chunk, each with the page box to crop
/// from the page image.</param>
public sealed record RagChunk(
    string Id,
    string Text,
    string SourcePath,
    string DocTitle,
    int PageStart,
    int PageEnd,
    string SectionTitle,
    int ChunkIndex,
    int TokenEstimate,
    string Quality,
    IReadOnlyList<string> Issues,
    int PdfPageStart,
    int PdfPageEnd,
    string PageLabelStart,
    string PageLabelEnd,
    IReadOnlyList<string> Sections,
    string ContextBefore,
    IReadOnlyList<RagEquation> Equations);

/// <summary>A numbered display equation, located so a consumer can crop and read it from the page.</summary>
/// <param name="Label">As printed: "(8.94)".</param>
/// <param name="Page">0-based page; <paramref name="PdfPage"/> is 1-based.</param>
/// <param name="Bbox">[left, top, right, bottom] in PDF points from the top-left corner of the
/// (unrotated) visible page — the CropBox — like pdfplumber; scale by DPI / 72 to crop a render.</param>
/// <param name="RawText">The equation as the text layer gives it (label excluded).</param>
/// <param name="Quality"><c>check</c> — flattened but in order; <c>suspect</c> — stacked / scrambled
/// or holding untrustworthy characters: read it from the page image.</param>
public sealed record RagEquation(
    string Label,
    int Page,
    int PdfPage,
    double[] Bbox,
    string RawText,
    string Quality,
    IReadOnlyList<string> Issues);

/// <summary>Result of a chunk run: the chunks plus what could not be processed.</summary>
/// <param name="SuspectPages">0-based pages whose text includes unmapped or mis-mapped characters.</param>
/// <param name="DoubledGlyphsRemoved">Glyphs drawn twice at the same spot that were dropped.</param>
public sealed record RagChunkResult(
    IReadOnlyList<RagChunk> Chunks,
    IReadOnlyList<int> PagesWithoutText,
    int SectionCount,
    IReadOnlyList<int> SuspectPages,
    int DoubledGlyphsRemoved)
{
    /// <summary>Numbered display equations located.</summary>
    public int EquationCount { get; init; }

    /// <summary>Of those, how many must be read from the page image (stacked / scrambled / garbled).</summary>
    public int EquationsFlagged { get; init; }
}

/// <summary>Tuning for <see cref="PdfRagChunker.Chunk"/>. Token counts are approximated as chars / 4.</summary>
public sealed record RagChunkOptions
{
    /// <summary>Target chunk size in tokens (clamped to 128–4000). Default 600.</summary>
    public int TargetTokens { get; init; } = 600;

    /// <summary>Fraction of a chunk repeated at the start of the next one (clamped to 0–0.5). Default 0.15.</summary>
    public double OverlapFraction { get; init; } = 0.15;

    /// <summary>A chunk below this many tokens is only emitted when it is the tail of a section. Default 48.</summary>
    public int MinChunkTokens { get; init; } = 48;

    /// <summary>Prefix each chunk's <c>text</c> with "<c>{doc title} — {section}</c>" so the embedding
    /// carries where the passage sits (contextual-retrieval-lite). Default true.</summary>
    public bool IncludeSectionHeading { get; init; } = true;

    internal int MaxChars => Math.Clamp(TargetTokens, 48, 4000) * 4;

    internal int OverlapChars => (int)(MaxChars * Math.Clamp(OverlapFraction, 0, 0.5));

    internal int MinChars => Math.Max(1, MinChunkTokens) * 4;
}

/// <summary>
/// Splits a text-layer PDF into overlapping, section-aware chunks for retrieval-augmented generation
/// and writes them as JSONL. Section boundaries come from the detected clause tree
/// (<see cref="PdfClauses"/>) when the document has one, else the bookmark outline
/// (<see cref="PdfBookmarks"/>), else the whole document. Running headers / footers and bare page
/// numbers are stripped; wrapped lines are joined and words de-hyphenated across line breaks.
/// Pages with no extractable text are reported, not chunked (full coverage waits on OCR).
/// </summary>
public static partial class PdfRagChunker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex(@"^\s*(?:\d{1,4}|[ivxlcdm]{1,7}|page\s+\d{1,4}|p\.\s*\d{1,4})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PageNumberLine();

    // Line that opens a new block: a list bullet, "(a)", "1.", or a clause number ("6.4", "O.7.1 General").
    [GeneratedRegex(@"^(?:[-–—•·*]\s|\(\w{1,4}\)\s|\d{1,3}[.)]\s|\d+\.\d|[A-Z]\.\d+(?:\.\d+)*\.?\s+\p{Lu})")]
    private static partial Regex BlockStart();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(@"(?<=[.!?;:])\s+")]
    private static partial Regex SentenceBreak();

    // A contents / list-of-tables leader: "…… 243" or a run of dots.
    [GeneratedRegex(@"\.\s?\.\s?\.\s?\.|\.{2,}\s*\d{1,4}\s*$")]
    private static partial Regex TocLeader();

    public static RagChunkResult Chunk(
        PdfDocument document, RagChunkOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new RagChunkOptions();

        DocumentAnalysis a = Analyse(document, cancellationToken);
        var paragraphs = BuildFlow(a, regionsFor: null, cancellationToken)
            .Where(f => f.Para is not null).Select(f => f.Para!.Value).ToList();

        int sectionCount = paragraphs.Count == 0 ? (a.PageCount == 0 ? 0 : 1)
            : paragraphs.Select(x => x.Section).Distinct(StringComparer.Ordinal).Count();

        var chunks = BuildChunks(paragraphs, options, document, a.SourcePath, a.DocTitle, a.Profile, a.EquationsByPage);
        return new RagChunkResult(chunks, a.PagesWithoutText, sectionCount, a.SuspectPages, a.DoubledGlyphs)
        {
            EquationCount = a.EquationCount,
            EquationsFlagged = a.EquationsFlagged,
        };
    }

    /// <summary>Everything read once per document and shared by the chunk and HTML exports: the
    /// cleaned pages, script profile, running chrome, contents pages, headings and equations.</summary>
    internal sealed class DocumentAnalysis
    {
        public required int PageCount { get; init; }
        public required ChunkPage?[] Pages { get; init; }
        public required List<int> PagesWithoutText { get; init; }
        public required List<int> SuspectPages { get; init; }
        public required int DoubledGlyphs { get; init; }
        public required PdfTextQuality.ScriptProfile Profile { get; init; }
        public required bool[] ContentsPage { get; init; }
        public required List<SectionMarker> Markers { get; init; }
        public required Dictionary<string, string> TitleByNumber { get; init; }
        public required Boilerplate Boilerplate { get; init; }
        public required string SourcePath { get; init; }
        public required string DocTitle { get; init; }
        public required Dictionary<int, List<RagEquation>> EquationsByPage { get; init; }
        public required int EquationCount { get; init; }
        public required int EquationsFlagged { get; init; }
    }

    /// <summary>One item of the document in reading order: a paragraph, or a carved-out region
    /// (equation / table / figure) with the text-layer lines it covered.</summary>
    internal sealed record FlowItem(int Page, string Section, Paragraph? Para, PageRegion? Region, string RegionText)
    {
        /// <summary>The paragraph is a heading line that started its section (printed wording).</summary>
        public bool IsHeading { get; init; }
    }

    internal static DocumentAnalysis Analyse(PdfDocument document, CancellationToken cancellationToken)
    {
        int pageCount = document.PageCount;
        var pages = new ChunkPage?[pageCount];
        var pageText = new string?[pageCount];
        var pagesWithoutText = new List<int>();
        int doubledGlyphs = 0;
        for (int p = 0; p < pageCount; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ChunkPage page = PdfChunkText.ReadPage(document, p);
                doubledGlyphs += page.DoubledGlyphsRemoved;
                if (!page.IsBlank)
                {
                    pages[p] = page;
                    pageText[p] = page.Text;
                }
            }
            catch (PdfException)
            {
                // unreadable page — reported below as having no text
            }

            if (pages[p] is null)
            {
                pagesWithoutText.Add(p);
            }
        }

        // Which scripts the document is really written in (so a Greek standard isn't "suspect"),
        // then which pages carry characters that can't be trusted.
        var profile = PdfTextQuality.ScriptProfile.Build(pageText.OfType<string>());
        var suspectPages = new List<int>();
        for (int p = 0; p < pageCount; p++)
        {
            if (pageText[p] is { } t && PdfTextQuality.Count(t, profile) is var (u, x) && u + x > 0)
            {
                suspectPages.Add(p);
            }
        }

        bool[] contentsPage = [.. pages.Select(pg => pg is not null && IsContentsPage(pg))];
        List<SectionMarker> allMarkers = SectionMarkers(document, cancellationToken);
        // A heading "on" a contents page is the contents entry, not the heading: drop it as a page
        // marker, but keep its title so the body heading with the same number can still use it.
        List<SectionMarker> markers = [.. allMarkers.Where(m => m.Page < 0 || m.Page >= pageCount || !contentsPage[m.Page])];
        var titleByNumber = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SectionMarker m in allMarkers)
        {
            if (m.DottedNumber is { } n)
            {
                titleByNumber.TryAdd(n, m.Title);
            }
        }

        Boilerplate boilerplate = Boilerplate.Detect(pages);

        // Equations, located per page; quality judged once here.
        var equationsByPage = new Dictionary<int, List<RagEquation>>();
        int equationCount = 0, equationsFlagged = 0;
        for (int p = 0; p < pageCount; p++)
        {
            if (pages[p] is not { } page || page.Equations.Count == 0)
            {
                continue;
            }

            var list = new List<RagEquation>();
            foreach (ChunkEquation e in page.Equations)
            {
                RagEquation eq = ToRagEquation(e, p, page.PageBox, profile);
                list.Add(eq);
                equationCount++;
                equationsFlagged += eq.Quality == "suspect" ? 1 : 0;
            }

            equationsByPage[p] = list;
        }

        return new DocumentAnalysis
        {
            PageCount = pageCount,
            Pages = pages,
            PagesWithoutText = pagesWithoutText,
            SuspectPages = suspectPages,
            DoubledGlyphs = doubledGlyphs,
            Profile = profile,
            ContentsPage = contentsPage,
            Markers = markers,
            TitleByNumber = titleByNumber,
            Boilerplate = boilerplate,
            SourcePath = document.FilePath is { Length: > 0 } fp ? Path.GetFileName(fp) : string.Empty,
            DocTitle = DocumentTitle(document, boilerplate.RunningTitle),
            EquationsByPage = equationsByPage,
            EquationCount = equationCount,
            EquationsFlagged = equationsFlagged,
        };
    }

    /// <summary>
    /// Paragraphs in reading order, each tagged with the section in force where it starts: a heading
    /// switches the section at the paragraph that prints it, not at the top of its page (so text
    /// continuing an earlier clause at the top of a page keeps that clause's title). With
    /// <paramref name="regionsFor"/>, the lines inside each page region are carved out of the
    /// paragraph text and the region takes their place in the flow (the HTML export's equations,
    /// tables and figures); without it every line is text (the chunk export).
    /// </summary>
    internal static List<FlowItem> BuildFlow(
        DocumentAnalysis a, Func<int, ChunkPage, IReadOnlyList<PageRegion>>? regionsFor, CancellationToken cancellationToken)
    {
        var flow = new List<FlowItem>();
        string current = "Front matter";
        int mi = 0;
        var markers = a.Markers;
        for (int p = 0; p < a.PageCount; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int pageFirst = mi;
            while (mi < markers.Count && markers[mi].Page <= p)
            {
                mi++;
            }

            var pending = new List<SectionMarker>(markers.Skip(pageFirst).Take(mi - pageFirst));
            var items = new List<(string? Text, bool HasMath, PageRegion? Region, string RegionText)>();
            if (a.Pages[p] is { } page && !a.ContentsPage[p])
            {
                IReadOnlyList<PageRegion> regions = regionsFor?.Invoke(p, page) ?? [];
                foreach (var seg in Segments(page, regions, a.Boilerplate))
                {
                    if (seg.Region is { } region)
                    {
                        items.Add((null, false, region, seg.RegionText));
                    }
                    else
                    {
                        items.AddRange(ParagraphsFromPage(seg.Lines, a.Boilerplate).Select(x => ((string?)x.Text, x.HasMath, (PageRegion?)null, string.Empty)));
                    }
                }
            }

            var paras = items.Where(i => i.Text is not null).Select(i => i.Text!).ToList();

            // A heading printed on the page switches the section at its paragraph; one whose text
            // isn't found (a bookmark titled differently from the printed heading) at the page top.
            var unmatched = pending.Where(m => !paras.Any(x => m.StartsParagraph(x))).ToList();
            bool startsSection = unmatched.Count > 0;
            if (unmatched.Count > 0)
            {
                current = unmatched[^1].Title;
                pending.RemoveAll(unmatched.Contains);
            }

            foreach (var item in items)
            {
                if (item.Region is { } region)
                {
                    flow.Add(new FlowItem(p, current, null, region, item.RegionText));
                    continue;
                }

                string para = item.Text!;
                int hit = pending.FindIndex(m => m.StartsParagraph(para));
                bool switched = hit >= 0;
                string? printed = null;
                if (hit >= 0)
                {
                    printed = PrintedHeading(para);
                    current = printed ?? pending[hit].Title;
                    pending.RemoveRange(0, hit + 1);
                }
                else if (BodyHeading().Match(para) is { Success: true } h && para.Length < 120
                    && a.TitleByNumber.TryGetValue(h.Groups[1].Value, out string? title)
                    && !string.Equals(current, title, StringComparison.Ordinal))
                {
                    // A numbered heading printed in the body whose outline entry points elsewhere
                    // (at the contents page, say): the section starts here.
                    printed = PrintedHeading(para);
                    current = printed ?? title;
                    switched = true;
                }

                flow.Add(new FlowItem(p, current, new Paragraph(para, p, current, item.HasMath, switched || startsSection), null, string.Empty)
                {
                    IsHeading = switched && printed is not null,
                });
                startsSection = false;
            }

            if (pending.Count > 0)
            {
                current = pending[^1].Title; // page had no text
            }
        }

        return flow;
    }

    /// <summary>A page's lines split into runs of text and the regions that interrupt them, in
    /// drawing order. A line belongs to a region when its centre lies inside it (or it is the
    /// region's caption line); a region is placed where its first line was, or — with no text of
    /// its own (a drawing) — before the first line below it.</summary>
    private static IEnumerable<(List<ChunkLine> Lines, PageRegion? Region, string RegionText)> Segments(
        ChunkPage page, IReadOnlyList<PageRegion> regions, Boilerplate boilerplate)
    {
        if (regions.Count == 0)
        {
            yield return ([.. page.Lines], null, string.Empty);
            yield break;
        }

        var owner = new PageRegion?[page.Lines.Count];
        for (int li = 0; li < page.Lines.Count; li++)
        {
            ChunkLine line = page.Lines[li];
            owner[li] = regions.FirstOrDefault(r => r.CaptionLine == li || r.CaptionContinuation.Contains(li))
                ?? (line.Bounds.Width > 0 && !string.IsNullOrWhiteSpace(line.Text)
                    ? regions.FirstOrDefault(r => Contains(r.Bounds, line.Bounds))
                    : null);
        }

        var emitted = new HashSet<PageRegion>();
        var run = new List<ChunkLine>();
        for (int li = 0; li < page.Lines.Count; li++)
        {
            ChunkLine line = page.Lines[li];
            // A drawing with no text of its own goes in before the first line below it.
            foreach (PageRegion r in regions.Where(r => !emitted.Contains(r) && !owner.Contains(r)
                && line.Bounds.Width > 0 && (line.Bounds.Top + line.Bounds.Bottom) / 2 < r.Bounds.Bottom).ToList())
            {
                if (run.Count > 0)
                {
                    yield return (run, null, string.Empty);
                    run = [];
                }

                emitted.Add(r);
                yield return ([], r, string.Empty);
            }

            if (owner[li] is not { } region)
            {
                run.Add(line);
                continue;
            }

            if (emitted.Add(region))
            {
                if (run.Count > 0)
                {
                    yield return (run, null, string.Empty);
                    run = [];
                }

                string text = string.Join('\n', page.Lines.Where((l, k) => owner[k] == region && k != region.CaptionLine
                        && !region.CaptionContinuation.Contains(k))
                    .Where(l => !l.InMargin && !boilerplate.Contains(l, Normalise(l.Text)))
                    .Select(l => StripLicence(Normalise(l.Text))).Where(t => t.Length > 0));
                yield return ([], region, text);
            }
        }

        if (run.Count > 0)
        {
            yield return (run, null, string.Empty);
        }

        foreach (PageRegion r in regions.Where(r => !emitted.Contains(r)))
        {
            yield return ([], r, string.Empty);
        }
    }

    private static bool Contains(PdfRect region, PdfRect line)
    {
        double cx = (line.Left + line.Right) / 2, cy = (line.Top + line.Bottom) / 2;
        return cx >= region.Left - 1 && cx <= region.Right + 1 && cy >= region.Bottom - 1 && cy <= region.Top + 1;
    }

    internal static RagEquation ToRagEquation(ChunkEquation e, int page, PdfRect pageBox, PdfTextQuality.ScriptProfile profile)
    {
        var issues = new List<string>();
        if (PdfTextQuality.ScrambleReason(e.RawText, e.Stacked) is { } why)
        {
            issues.Add("scrambled: " + why);
        }

        (int unmapped, int unexpected) = PdfTextQuality.Count(e.RawText, profile);
        if (unmapped + unexpected > 0)
        {
            issues.Add($"garbled: {unmapped + unexpected} character(s) the font doesn't map to real symbols");
        }

        IReadOnlyList<string> repeated = PdfTextQuality.RepeatedLetterTokens(e.RawText);
        if (repeated.Count > 0)
        {
            issues.Add("duplicated_glyphs: " + string.Join(", ", repeated));
        }

        double R(double v) => Math.Round(v, 1);
        double[] bbox =
        [
            R(e.Bounds.Left - pageBox.Left), R(pageBox.Top - e.Bounds.Top),
            R(e.Bounds.Right - pageBox.Left), R(pageBox.Top - e.Bounds.Bottom),
        ];
        return new RagEquation(e.Label, page, page + 1, bbox, e.RawText, issues.Count > 0 ? "suspect" : "check", issues);
    }

    /// <summary>Serialise chunks as JSON Lines (one object per line, UTF-8, no BOM).</summary>
    public static void WriteJsonl(IEnumerable<RagChunk> chunks, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(writer);
        foreach (RagChunk chunk in chunks)
        {
            writer.WriteLine(JsonSerializer.Serialize(chunk, JsonOptions));
        }
    }

    public static void WriteJsonl(IEnumerable<RagChunk> chunks, string path)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        WriteJsonl(chunks, writer);
    }

    internal readonly record struct Paragraph(string Text, int Page, string Section, bool HasMath, bool StartsSection);

    // --- Section boundaries --------------------------------------------------------------

    /// <summary>A heading: the page it's on, its title, and how to recognise the paragraph that prints it.</summary>
    /// <param name="Number">A detected clause's number — matched against the start of a paragraph.</param>
    internal sealed record SectionMarker(int Page, string Title, string? Number)
    {
        /// <summary>A dotted number ("6.5.5", "A.2") from the clause or the title's start, if any.</summary>
        public string? DottedNumber =>
            Number is { } n && n.Contains('.') ? n.TrimEnd('.')
            : BodyHeading().Match(Title) is { Success: true } m ? m.Groups[1].Value : null;

        public bool StartsParagraph(string para)
        {
            if (Number is not null)
            {
                return StartsWithNumber(para, Number.TrimEnd('.'));
            }

            string key = Normalise(Title);
            key = key[..Math.Min(key.Length, 40)];
            return (key.Length > 0 && Normalise(para).StartsWith(key, StringComparison.OrdinalIgnoreCase))
                || (DottedNumber is { } dn && BodyHeading().IsMatch(para) && StartsWithNumber(para, dn));
        }

        private static bool StartsWithNumber(string para, string number) =>
            para.StartsWith(number, StringComparison.Ordinal)
            && (para.Length == number.Length || char.IsWhiteSpace(para[number.Length])
                || (para[number.Length] == '.' && (para.Length == number.Length + 1 || char.IsWhiteSpace(para[number.Length + 1]))));
    }

    /// <summary>A short numbered heading line ("6.4.3 Punching shear", "6.5.5. Reinforcement…") with a
    /// real word in it — so "1.35 Gk" (a value in a calculation) isn't one. The HTML export uses it
    /// for headings the outline doesn't list.</summary>
    internal static bool IsStandaloneHeading(string para) =>
        BodyHeading().Match(para) is { Success: true } m && PrintedHeading(para) is not null
        && RealWord().IsMatch(para[m.Groups[1].Length..]);

    /// <summary>"6.4.3 …" → "6.4.3"; "Annex B …" → "B"; else null.</summary>
    internal static string? HeadingNumber(string heading) =>
        BodyHeading().Match(heading) is { Success: true } m ? m.Groups[1].Value
        : AnnexHeading().Match(heading) is { Success: true } a ? a.Groups[1].Value : null;

    /// <summary>Nesting depth of a heading from its number: "6" / "Annex B" → 1, "6.4" → 2, "6.4.3" → 3;
    /// an un-numbered (outline) heading → 1.</summary>
    internal static int HeadingDepth(string heading) =>
        BodyHeading().Match(heading) is { Success: true } m ? m.Groups[1].Value.Count(c => c == '.') + 1 : 1;

    [GeneratedRegex(@"^Annex\s+([A-Z]{1,2})\b")]
    private static partial Regex AnnexHeading();

    /// <summary>The heading as printed, when <paramref name="para"/> is just a heading line (short, not a
    /// sentence) — better than an outline entry, which may carry a contents-page number or different
    /// wording; null when the heading ran into body text.</summary>
    internal static string? PrintedHeading(string para)
    {
        string t = Normalise(para);
        // Judge "is it a sentence?" past the number — "6.1. General" isn't one.
        string words = BodyHeading().Match(t) is { Success: true } m ? t[(m.Groups[1].Length + 1)..] : t;
        return t.Length is > 0 and <= 120 && !t.EndsWith('.') && !SentenceBreak().IsMatch(words.TrimStart('.', ' '))
            ? TrailingPageNumber().Replace(t, string.Empty)
            : null;
    }

    // A numbered heading: "6.5.5 Reinforcement…", "6.5.5. Reinforcement…", "A.2 Scope".
    [GeneratedRegex(@"^((?:\d{1,2}|[A-Z])(?:\.\d{1,3})+)\.?\s+\p{Lu}")]
    private static partial Regex BodyHeading();

    // A contents entry's page number at the end of a title: "Reinforcement for punching shear 103".
    [GeneratedRegex(@"(?<=\p{L}[^\d]*)\s+\d{1,4}$")]
    private static partial Regex TrailingPageNumber();

    // A contents line: words, then a lone page number ("6.5.5. Reinforcement for punching shear 103").
    [GeneratedRegex(@"\p{L}{3,}\D*\s\d{1,4}$")]
    private static partial Regex ContentsEntry();

    /// <summary>A contents / list-of-figures page: ≥ 5 dotted leaders, or ≥ 5 lines (and ≥ 40 % of
    /// the page's lines) that are words followed by a lone page number. Navigation, not knowledge —
    /// its text isn't chunked and headings "on" it aren't section starts.</summary>
    private static bool IsContentsPage(ChunkPage page)
    {
        var lines = page.Lines.Select(l => Normalise(l.Text)).Where(l => l.Length > 0).ToList();
        if (lines.Count(l => TocLeader().IsMatch(l)) >= 5)
        {
            return true;
        }

        int entries = lines.Count(l => ContentsEntry().IsMatch(l));
        return entries >= 5 && entries >= lines.Count * 0.4;
    }

    /// <summary>
    /// Headings in document order. The PDF's own bookmarks win when there are at least three — the
    /// author's outline beats a heuristic, and on a textbook the clause detector mistakes numbered
    /// values ("1.35 Gk") for headings. Otherwise the detected clause tree, minus "headings" whose title
    /// has no real word (a number followed by a symbol is a value in a calculation, not a clause).
    /// </summary>
    private static List<SectionMarker> SectionMarkers(PdfDocument document, CancellationToken ct)
    {
        List<SectionMarker> markers = BookmarkMarkers(document);
        if (markers.Count < 3)
        {
            List<SectionMarker> clauses = ClauseMarkers(document, ct);
            if (clauses.Count >= 3)
            {
                markers = clauses;
            }
        }

        // Stable: keeps document order for headings on the same page.
        return [.. markers.OrderBy(m => m.Page)];
    }

    [GeneratedRegex(@"\p{L}{3,}")]
    private static partial Regex RealWord();

    private static List<SectionMarker> ClauseMarkers(PdfDocument document, CancellationToken ct)
    {
        var flat = new List<SectionMarker>();
        void Walk(IReadOnlyList<PdfClause> nodes)
        {
            foreach (PdfClause c in nodes)
            {
                if (RealWord().IsMatch(c.Title))
                {
                    flat.Add(new SectionMarker(c.PageIndex, TrailingPageNumber().Replace(c.Display.Trim(), string.Empty), c.Number));
                }

                Walk(c.Children);
            }
        }

        try
        {
            Walk(PdfClauses.Read(document, ct));
        }
        catch (PdfException)
        {
            // no clause tree
        }

        return flat;
    }

    private static List<SectionMarker> BookmarkMarkers(PdfDocument document)
    {
        var flat = new List<SectionMarker>();
        void Walk(IReadOnlyList<PdfBookmark> nodes)
        {
            foreach (PdfBookmark b in nodes)
            {
                if (b.PageIndex is int page && !string.IsNullOrWhiteSpace(b.Title))
                {
                    flat.Add(new SectionMarker(page, TrailingPageNumber().Replace(Normalise(b.Title), string.Empty), null));
                }

                Walk(b.Children);
            }
        }

        try
        {
            Walk(document.GetOutline());
        }
        catch (PdfException)
        {
            // no outline — caller falls back
        }

        return flat;
    }

    // Placeholder titles PDF producers leave behind: "untitled", "30187350.pdf", "Microsoft Word - x.docx".
    [GeneratedRegex(@"^(?:untitled(?:\s+document)?|document\d*|\d+|[\w\-. ]+\.(?:pdf|docx?|indd|qxp|tex|dvi))$", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderTitle();

    /// <summary>Metadata title unless it's a placeholder → the running header (the book's own name
    /// for itself, which is usually better than a file name) → the file name.</summary>
    private static string DocumentTitle(PdfDocument document, string? runningTitle)
    {
        string? title = null;
        try
        {
            title = CleanTitle(PdfMetadata.GetInfo(document).Title);
        }
        catch (PdfException)
        {
            // fall through
        }

        if (string.IsNullOrEmpty(title) || PlaceholderTitle().IsMatch(title))
        {
            title = CleanTitle(runningTitle);
        }

        if (string.IsNullOrEmpty(title))
        {
            title = document.FilePath is { Length: > 0 } path ? CleanTitle(Path.GetFileNameWithoutExtension(path)) : null;
        }

        return string.IsNullOrEmpty(title) ? "Untitled" : title;
    }

    internal static string CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        string t = title.Replace('‐', '-').Replace('‑', '-').Replace('‒', '-');
        if (t.StartsWith("Microsoft Word - ", StringComparison.OrdinalIgnoreCase))
        {
            t = Path.GetFileNameWithoutExtension(t["Microsoft Word - ".Length..]);
        }

        return Normalise(t).TrimEnd('.', ' ');
    }

    // --- Cleaning -----------------------------------------------------------------------

    // Licence / distribution stamps: always removed, from the marker to the end of the line.
    [GeneratedRegex(@"Licensee=|Licensed cop(?:y|ies)\b|Uncontrolled Copy|Not for Resale|No reproduction or networking permitted|Provided by .{0,80}?under licen[cs]e", RegexOptions.IgnoreCase)]
    private static partial Regex LicenceStamp();

    /// <summary>A line with any licence / distribution stamp cut off (from the marker to the end).</summary>
    internal static string StripLicence(string line) =>
        LicenceStamp().Match(line) is { Success: true } m ? line[..m.Index].TrimEnd(' ', ',', ';', '-', '–') : line;

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    // A page number at either end of a running header: "12 Designers' Guide…" / "…EN 1992-1-1 13".
    [GeneratedRegex(@"^\d{1,4}\s+|\s+\d{1,4}$")]
    private static partial Regex PageNumberEnds();

    /// <summary>
    /// Running chrome. Any line recurring verbatim on ≥ 50 % of pages (no length cap — body text doesn't
    /// recur, so even a long legal footer extracted as one run is chrome); and, in the header / footer
    /// band, a line recurring on ≥ 30 % of pages once page numbers are blanked ("BS EN 1992-1-1:2004 45"
    /// vs "… 46") — running headers often alternate book / chapter title, so they miss 50 %. The most
    /// frequent wordy top-band line doubles as the book's own title.
    /// </summary>
    internal sealed class Boilerplate
    {
        private readonly HashSet<string> _lines = new(StringComparer.Ordinal);
        private readonly HashSet<string> _margin = new(StringComparer.Ordinal);

        public string? RunningTitle { get; private set; }

        public bool Contains(ChunkLine raw, string norm) =>
            _lines.Contains(norm) || (raw.InMargin && _margin.Contains(MarginKey(norm)));

        private static string MarginKey(string norm) => Digits().Replace(norm, "#");

        public static Boilerplate Detect(ChunkPage?[] pages)
        {
            var result = new Boilerplate();
            int count = pages.Count(p => p is not null);
            if (count < 4)
            {
                return result;
            }

            var lineCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var marginCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var topCounts = new Dictionary<string, (int Count, string Text)>(StringComparer.Ordinal);
            foreach (ChunkPage? page in pages)
            {
                if (page is null)
                {
                    continue;
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (ChunkLine line in page.Lines)
                {
                    string norm = Normalise(line.Text);
                    if (norm.Length == 0)
                    {
                        continue;
                    }

                    if (seen.Add(norm))
                    {
                        lineCounts[norm] = lineCounts.GetValueOrDefault(norm) + 1;
                    }

                    if (line.InMargin && seen.Add("\0" + MarginKey(norm)))
                    {
                        string key = MarginKey(norm);
                        marginCounts[key] = marginCounts.GetValueOrDefault(key) + 1;
                        bool top = line.Bounds.Bottom >= page.PageBox.Bottom + ((page.PageBox.Top - page.PageBox.Bottom) / 2);
                        if (top)
                        {
                            topCounts[key] = (topCounts.GetValueOrDefault(key).Count + 1, norm);
                        }
                    }
                }
            }

            int half = Math.Max(3, (int)Math.Ceiling(count * 0.5));
            int third = Math.Max(3, (int)Math.Ceiling(count * 0.3));
            result._lines.UnionWith(lineCounts.Where(kv => kv.Value >= half).Select(kv => kv.Key));
            result._margin.UnionWith(marginCounts.Where(kv => kv.Value >= third).Select(kv => kv.Key));

            // The book's title: the most frequent top-band running line with a few real words.
            result.RunningTitle = topCounts
                .Where(kv => kv.Value.Count >= third && RealWord().Count(kv.Value.Text) >= 2)
                .OrderByDescending(kv => kv.Value.Count)
                .Select(kv => PageNumberEnds().Replace(kv.Value.Text, string.Empty).Trim())
                .FirstOrDefault();
            return result;
        }
    }

    private static string Normalise(string line) => WhitespaceRun().Replace(line, " ").Trim();

    private static IEnumerable<(string Text, bool HasMath)> ParagraphsFromPage(
        IReadOnlyList<ChunkLine> pageLines, Boilerplate boilerplate)
    {
        var lines = new List<(string Text, bool HasMath)>();
        foreach (ChunkLine raw in pageLines)
        {
            string line = Normalise(raw.Text);
            if (boilerplate.Contains(raw, line))
            {
                continue;
            }

            if (LicenceStamp().Match(line) is { Success: true } stamp)
            {
                line = line[..stamp.Index].TrimEnd(' ', ',', ';', '-', '–');
            }

            // A bare number / roman numeral is only a page number in the header or footer band:
            // mid-page it's an equation fragment (the "3" of a ⅓ exponent, a lone "d" under a
            // fraction bar) and dropping it silently corrupts the expression.
            if (line.Length == 0
                || (raw.InMargin && PageNumberLine().IsMatch(line)) || TocLeader().IsMatch(line))
            {
                continue;
            }

            lines.Add((line, raw.HasMath));
        }

        var paragraphs = new List<(string, bool)>();
        var sb = new StringBuilder();
        bool math = false;
        foreach ((string line, bool lineMath) in lines)
        {
            if (sb.Length == 0)
            {
                sb.Append(line);
                math = lineMath;
                continue;
            }

            char last = sb[^1];
            if (last == '-' && sb.Length >= 2 && char.IsLetter(sb[^2]) && char.IsLower(line[0]))
            {
                sb.Length -= 1;
                sb.Append(line); // de-hyphenate across the line break
            }
            else if (BlockStart().IsMatch(line) || last is '.' or '!' or '?' || LooksLikeHeading(sb.ToString()))
            {
                paragraphs.Add((sb.ToString(), math));
                sb.Clear();
                sb.Append(line);
                math = lineMath;
                continue;
            }
            else
            {
                sb.Append(' ').Append(line); // wrapped line — join
            }

            math |= lineMath;
        }

        if (sb.Length > 0)
        {
            paragraphs.Add((sb.ToString(), math));
        }

        return paragraphs;
    }

    private static bool LooksLikeHeading(string line) =>
        line.Length < 70 && !line.EndsWith('.') && ClauseHeadingShape().IsMatch(line);

    [GeneratedRegex(@"^(?:\d+(?:\.\d+)*\.?|Annex\s+\w+|[A-Z]\.\d)\s+\p{Lu}")]
    private static partial Regex ClauseHeadingShape();

    // --- Windowing ---------------------------------------------------------------------

    private static IReadOnlyList<RagChunk> BuildChunks(
        List<Paragraph> paragraphs, RagChunkOptions opt, PdfDocument document, string sourcePath, string docTitle,
        PdfTextQuality.ScriptProfile profile, IReadOnlyDictionary<int, List<RagEquation>> equationsByPage)
    {
        int overlapChars = opt.OverlapChars;
        // Leave room for the "{title} — {section}" prefix so the finished text still lands ≤ MaxChars.
        int headingReserve = opt.IncludeSectionHeading ? 160 : 0;
        int maxChars = Math.Max(200, opt.MaxChars - headingReserve);
        int minChars = Math.Min(opt.MinChars, maxChars);
        string idStem = sourcePath is { Length: > 0 }
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : "chunk";

        string Label(int page) => document.GetPageLabel(page) ?? (page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        // 1. Flatten paragraphs to units that are each no longer than the budget.
        var units = new List<Paragraph>();
        foreach (Paragraph para in paragraphs)
        {
            if (para.Text.Length <= maxChars)
            {
                units.Add(para);
                continue;
            }

            bool first = true;
            foreach (string sentence in SentenceBreak().Split(para.Text))
            {
                string s = sentence.Trim();
                if (s.Length == 0)
                {
                    continue;
                }

                for (int i = 0; i < s.Length; i += maxChars)
                {
                    units.Add(para with { Text = s.Substring(i, Math.Min(maxChars, s.Length - i)), StartsSection = para.StartsSection && first });
                    first = false;
                }
            }
        }

        // 2. Pack units into chunks. The overlap — the end of the previous chunk — goes in
        // context_before, not into text, so a passage is never printed twice inline; and a new
        // section starts a new chunk (with no overlap), once the current one is big enough.
        var chunks = new List<RagChunk>();
        var buf = new StringBuilder();
        var sections = new List<string>();
        string context = string.Empty;
        int firstPage = 0, lastPage = 0, index = 0;
        bool chunkHasMath = false;

        void Emit(bool force, bool carryOverlap)
        {
            string body = Normalise(buf.ToString());
            if (body.Length == 0 || (!force && body.Length < minChars))
            {
                return;
            }

            string section = sections.Count > 0 ? sections[0] : "Front matter";
            string text = opt.IncludeSectionHeading && !string.IsNullOrEmpty(section)
                ? $"{docTitle} — {section}\n\n{body}"
                : body;

            var equations = new List<RagEquation>();
            string compactBody = WhitespaceRun().Replace(body, string.Empty);
            for (int p = firstPage; p <= lastPage; p++)
            {
                if (equationsByPage.TryGetValue(p, out List<RagEquation>? onPage))
                {
                    equations.AddRange(onPage.Where(e => compactBody.Contains(e.Label, StringComparison.Ordinal)));
                }
            }

            (string quality, IReadOnlyList<string> issues) = PdfTextQuality.Assess(body, chunkHasMath, profile, equations);
            chunks.Add(new RagChunk(
                $"{idStem}#c{index:D4}", text, sourcePath, docTitle,
                firstPage, lastPage, section, index, text.Length / 4, quality, issues,
                firstPage + 1, lastPage + 1, Label(firstPage), Label(lastPage),
                [.. sections], context, equations));
            index++;

            context = string.Empty;
            if (carryOverlap && overlapChars > 0)
            {
                string tail = body.Length > overlapChars ? body[^overlapChars..] : body;
                int space = tail.IndexOf(' ');
                context = body.Length > overlapChars && space > 0 ? tail[(space + 1)..] : tail;
            }

            buf.Clear();
            sections.Clear();
            chunkHasMath = false;
        }

        foreach (Paragraph u in units)
        {
            if (buf.Length > 0)
            {
                if (u.StartsSection && buf.Length >= minChars)
                {
                    Emit(force: false, carryOverlap: false);
                }
                else if (buf.Length + u.Text.Length + 1 > maxChars)
                {
                    Emit(force: true, carryOverlap: true);
                }
            }

            if (buf.Length == 0)
            {
                firstPage = u.Page;
            }

            if (sections.Count == 0 || !string.Equals(sections[^1], u.Section, StringComparison.Ordinal))
            {
                sections.Add(u.Section);
            }

            buf.Append(u.Text).Append(' ');
            lastPage = u.Page;
            chunkHasMath |= u.HasMath;
        }

        Emit(force: true, carryOverlap: false);
        return chunks;
    }
}
