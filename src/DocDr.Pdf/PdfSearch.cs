using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>One text-search match: its page, character range, and highlight rectangles (page space).</summary>
public sealed class SearchHit
{
    public SearchHit(int pageIndex, int charStart, int charCount, IReadOnlyList<PdfRect> rects)
    {
        PageIndex = pageIndex;
        CharStart = charStart;
        CharCount = charCount;
        Rects = rects;
    }

    public int PageIndex { get; }
    public int CharStart { get; }
    public int CharCount { get; }

    /// <summary>Bounding boxes of the matched text, in PDFium page space (points, bottom-left origin).</summary>
    public IReadOnlyList<PdfRect> Rects { get; }
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
    public IReadOnlyList<SearchHit> FindAll(string term, PdfSearchOptions options = PdfSearchOptions.None, CancellationToken cancellationToken = default)
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
                        hits.Add(new SearchHit(page, start, count, CollectRects(textPage, start, count)));
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

    private static IReadOnlyList<PdfRect> CollectRects(FpdfTextpageT textPage, int start, int count)
    {
        int rectCount = fpdf_text.FPDFTextCountRects(textPage, start, count);
        if (rectCount <= 0)
        {
            return Array.Empty<PdfRect>();
        }

        var rects = new List<PdfRect>(rectCount);
        for (int i = 0; i < rectCount; i++)
        {
            double left = 0, top = 0, right = 0, bottom = 0;
            fpdf_text.FPDFTextGetRect(textPage, i, ref left, ref top, ref right, ref bottom);
            rects.Add(new PdfRect(left, top, right, bottom));
        }

        return rects;
    }
}
