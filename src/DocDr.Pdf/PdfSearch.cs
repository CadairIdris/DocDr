using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>One text-search match: its page, character range, and highlight rectangles (page space).</summary>
public sealed class SearchHit
{
    public SearchHit(int pageIndex, int charStart, int charCount, IReadOnlyList<PdfRect> rects, string snippet = "")
    {
        PageIndex = pageIndex;
        CharStart = charStart;
        CharCount = charCount;
        Rects = rects;
        Snippet = snippet;
    }

    public int PageIndex { get; }
    public int CharStart { get; }
    public int CharCount { get; }

    /// <summary>Bounding boxes of the matched text, in PDFium page space (points, bottom-left origin).</summary>
    public IReadOnlyList<PdfRect> Rects { get; }

    /// <summary>A few words of context around the match, whitespace-collapsed, with an ellipsis where
    /// it was truncated. Empty unless the caller asked for it (see <see cref="PdfSearch.FindAll"/>).</summary>
    public string Snippet { get; }
}

/// <summary>
/// A single, stateless full-document text search over a <see cref="PdfDocument"/>.
/// <para>
/// This type holds no "current match" cursor and never moves any view — the owning view model
/// tracks which hit is selected. Two <see cref="PdfSearch"/> instances over the same document
/// are fully independent; they contend only on the document's PDFium lock, one page at a time.
/// </para>
/// </summary>
public sealed class PdfSearch
{
    private readonly PdfDocument _document;

    public PdfSearch(PdfDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
    }

    /// <summary>Pages this search has had to open in PDFium (text page loads) — for tests showing a
    /// warm-cache search touches only the pages that match.</summary>
    internal int PagesOpened => Volatile.Read(ref _pagesOpened);

    private int _pagesOpened;

    /// <summary>How long a progressive search accumulates hits before handing the next batch to
    /// <c>onBatch</c> (the first hit is always handed over at once).</summary>
    internal static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Find every match of <paramref name="term"/> across the whole document, in page order. The
    /// lock is taken and released per page so a concurrent render in the other pane is never
    /// starved. See <see cref="Search"/> for how pages are read.
    /// </summary>
    /// <param name="includeSnippets">Also capture a few words of context around each match —
    /// off by default since the in-pane viewer only needs highlight rects; folder-wide search
    /// turns it on to show a preview line per result.</param>
    public IReadOnlyList<SearchHit> FindAll(
        string term, PdfSearchOptions options = PdfSearchOptions.None,
        CancellationToken cancellationToken = default, bool includeSnippets = false) =>
        Search(term, options, startPage: 0, onBatch: null, cancellationToken, includeSnippets);

    /// <summary>
    /// Progressive search: visits pages from <paramref name="startPage"/> to the end, then wraps to
    /// the beginning, handing newly found hits to <paramref name="onBatch"/> (on the calling
    /// thread) as it goes — the first hit immediately, then at most every
    /// <see cref="BatchInterval"/>, then a final flush — so a viewer can show the match nearest the
    /// reader long before a 400-page standard has been scanned. Returns every hit, sorted by page
    /// then character.
    /// <para>
    /// Per page: if its text is already in the document's <see cref="PdfPageTextCache"/> the term
    /// is matched in managed code (<see cref="PdfTextMatcher"/>) and PDFium is touched only when
    /// there's a match (for its highlight boxes). Otherwise the page is opened once, its text read
    /// and cached, and matched and boxed in that same load — so a cold search costs what the old
    /// per-page <c>FPDFText_FindStart</c> loop did, and every later one costs almost nothing on
    /// pages without a hit.
    /// </para>
    /// </summary>
    public IReadOnlyList<SearchHit> Search(
        string term, PdfSearchOptions options, int startPage,
        Action<IReadOnlyList<SearchHit>>? onBatch,
        CancellationToken cancellationToken = default, bool includeSnippets = false)
    {
        var all = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(term))
        {
            return all;
        }

        int pageCount = _document.PageCount;
        if (pageCount == 0)
        {
            return all;
        }

        int first = Math.Clamp(startPage, 0, pageCount - 1);
        var pending = new List<SearchHit>();
        var sinceFlush = System.Diagnostics.Stopwatch.StartNew();
        bool flushedAny = false;

        void Flush()
        {
            if (onBatch is null || pending.Count == 0)
            {
                return;
            }

            onBatch(pending.ToArray());
            pending.Clear();
            sinceFlush.Restart();
            flushedAny = true;
        }

        for (int step = 0; step < pageCount; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int page = (first + step) % pageCount;

            IReadOnlyList<SearchHit> pageHits = SearchPage(page, term, options, includeSnippets, cancellationToken);
            if (pageHits.Count == 0)
            {
                continue;
            }

            all.AddRange(pageHits);
            if (onBatch is not null)
            {
                pending.AddRange(pageHits);
                if (!flushedAny || sinceFlush.Elapsed >= BatchInterval)
                {
                    Flush();
                }
            }
        }

        Flush();
        all.Sort(static (a, b) => a.PageIndex != b.PageIndex
            ? a.PageIndex.CompareTo(b.PageIndex)
            : a.CharStart.CompareTo(b.CharStart));
        return all;
    }

    private IReadOnlyList<SearchHit> SearchPage(
        int page, string term, PdfSearchOptions options, bool includeSnippets, CancellationToken ct)
    {
        int version = _document.ContentVersion;
        PdfPageTextCache cache = _document.TextCache;

        if (cache.TryGet(version, page, out string cachedText))
        {
            List<(int Start, int Count)> ranges = PdfTextMatcher.Find(cachedText, term, options);
            if (ranges.Count == 0)
            {
                return Array.Empty<SearchHit>();
            }

            return _document.Locked(() => PdfTextExtractor.WithTextPage<IReadOnlyList<SearchHit>>(_document, page, textPage =>
            {
                Interlocked.Increment(ref _pagesOpened);
                // A page whose text doesn't line up 1:1 with PDFium's char indices (non-BMP
                // glyphs → surrogate pairs) gets PDFium's own find instead.
                return fpdf_text.FPDFTextCountChars(textPage) == cachedText.Length
                    ? BuildHits(textPage, page, ranges, includeSnippets, ct)
                    : NativeFind(textPage, page, term, options, includeSnippets, ct);
            }));
        }

        return _document.Locked(() => PdfTextExtractor.WithTextPage<IReadOnlyList<SearchHit>>(_document, page, textPage =>
        {
            Interlocked.Increment(ref _pagesOpened);
            string text = PdfTextExtractor.ReadAllText(textPage);
            cache.Store(version, page, text);
            if (fpdf_text.FPDFTextCountChars(textPage) != text.Length)
            {
                return NativeFind(textPage, page, term, options, includeSnippets, ct);
            }

            List<(int Start, int Count)> ranges = PdfTextMatcher.Find(text, term, options);
            return ranges.Count == 0
                ? Array.Empty<SearchHit>()
                : BuildHits(textPage, page, ranges, includeSnippets, ct);
        }));
    }

    private static IReadOnlyList<SearchHit> BuildHits(
        FpdfTextpageT textPage, int page, List<(int Start, int Count)> ranges, bool includeSnippets,
        CancellationToken ct)
    {
        var hits = new List<SearchHit>(ranges.Count);
        foreach ((int start, int count) in ranges)
        {
            ct.ThrowIfCancellationRequested();
            string snippet = includeSnippets ? BuildSnippet(textPage, start, count) : string.Empty;
            hits.Add(new SearchHit(page, start, count, CollectRects(textPage, start, count), snippet));
        }

        return hits;
    }

    /// <summary>PDFium's own find, for the rare page whose extracted text can't be index-mapped.</summary>
    private static IReadOnlyList<SearchHit> NativeFind(
        FpdfTextpageT textPage, int page, string term, PdfSearchOptions options, bool includeSnippets,
        CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        ushort[] needle = PdfTextExtractor.ToWideString(term.Trim());
        FpdfSchhandleT? search = fpdf_text.FPDFTextFindStart(textPage, ref needle[0], (uint)options, 0);
        if (search is null || search.__Instance == IntPtr.Zero)
        {
            return hits;
        }

        try
        {
            while (fpdf_text.FPDFTextFindNext(search) != 0)
            {
                ct.ThrowIfCancellationRequested();
                int start = fpdf_text.FPDFTextGetSchResultIndex(search);
                int count = fpdf_text.FPDFTextGetSchCount(search);
                string snippet = includeSnippets ? BuildSnippet(textPage, start, count) : string.Empty;
                hits.Add(new SearchHit(page, start, count, CollectRects(textPage, start, count), snippet));
            }
        }
        finally
        {
            fpdf_text.FPDFTextFindClose(search);
        }

        return hits;
    }

    /// <summary>
    /// Build a hit directly from an already-known page and character range — e.g. one found by an
    /// earlier out-of-process scan (folder-wide search) — without re-running
    /// <c>FPDFTextFindStart</c>/<c>FindNext</c> across the whole document. Touches only the one
    /// page. Returns null if the range no longer checks out against the page's current text (the
    /// file changed since the earlier scan).
    /// </summary>
    public SearchHit? HitAt(int pageIndex, int charStart, int charCount)
    {
        if (pageIndex < 0 || pageIndex >= _document.PageCount || charStart < 0 || charCount <= 0)
        {
            return null;
        }

        return _document.Locked(() => PdfTextExtractor.WithTextPage(_document, pageIndex, textPage =>
        {
            int totalChars = fpdf_text.FPDFTextCountChars(textPage);
            if (charStart + charCount > totalChars)
            {
                return null;
            }

            return new SearchHit(pageIndex, charStart, charCount, CollectRects(textPage, charStart, charCount));
        }));
    }

    private const int SnippetContextChars = 44;

    private static string BuildSnippet(FpdfTextpageT textPage, int start, int count)
    {
        int totalChars = fpdf_text.FPDFTextCountChars(textPage);
        int snippetStart = Math.Max(0, start - SnippetContextChars);
        int snippetEnd = Math.Min(totalChars, start + count + SnippetContextChars);
        if (snippetEnd <= snippetStart)
        {
            return string.Empty;
        }

        string raw = PdfTextExtractor.ReadText(textPage, snippetStart, snippetEnd - snippetStart);
        string collapsed = CollapseWhitespace(raw);
        return (snippetStart > 0 ? "…" : "") + collapsed + (snippetEnd < totalChars ? "…" : "");
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        bool lastWasSpace = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
            }
            else
            {
                builder.Append(c);
                lastWasSpace = false;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Highlight rects for a match, one per text line rather than PDFium's own
    /// <c>FPDFTextCountRects</c>/<c>GetRect</c> — that API often returns one rect per glyph run
    /// instead of per word on PDFs that position each character explicitly (a kerned or justified
    /// line's <c>TJ</c> array), which is exactly what made search highlights look like a strip of
    /// individual character boxes. Walking the match's own char boxes and merging them with
    /// <see cref="PdfTextExtractor.MergeIntoLineRects"/> — the same heuristic the click-drag text
    /// selection highlight uses — gives one clean box per line regardless.
    /// </summary>
    private static IReadOnlyList<PdfRect> CollectRects(FpdfTextpageT textPage, int start, int count)
    {
        if (count <= 0)
        {
            return Array.Empty<PdfRect>();
        }

        var boxes = new List<PdfRect>(count);
        for (int i = start; i < start + count; i++)
        {
            double left = 0, right = 0, bottom = 0, top = 0;
            fpdf_text.FPDFTextGetCharBox(textPage, i, ref left, ref right, ref bottom, ref top);
            boxes.Add(new PdfRect(left, top, right, bottom));
        }

        var rects = PdfTextExtractor.MergeIntoLineRects(boxes);

        return rects;
    }
}
