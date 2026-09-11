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

    /// <summary>
    /// Find every match of <paramref name="term"/> across the whole document. The lock is taken
    /// and released per page so a concurrent render in the other pane is never starved.
    /// </summary>
    /// <param name="includeSnippets">Also capture a few words of context around each match (an
    /// extra <c>FPDFTextGetText</c> call per hit) — off by default since the in-pane viewer only
    /// needs highlight rects; folder-wide search turns it on to show a preview line per result.</param>
    public IReadOnlyList<SearchHit> FindAll(
        string term, PdfSearchOptions options = PdfSearchOptions.None,
        CancellationToken cancellationToken = default, bool includeSnippets = false)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrEmpty(term))
        {
            return hits;
        }

        ushort[] needle = PdfTextExtractor.ToWideString(term);

        for (int pageIndex = 0; pageIndex < _document.PageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int page = pageIndex;

            _document.Locked(() => PdfTextExtractor.WithTextPage(_document, page, textPage =>
            {
                FpdfSchhandleT? search = fpdf_text.FPDFTextFindStart(textPage, ref needle[0], (uint)options, 0);
                if (search is null || search.__Instance == IntPtr.Zero)
                {
                    return 0;
                }

                try
                {
                    while (fpdf_text.FPDFTextFindNext(search) != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
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

                return 0;
            }));
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
