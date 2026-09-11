namespace DocDr.Pdf.Tests;

public sealed class PdfSearchTests
{
    [Fact]
    public void FindAll_locates_matches_on_the_right_pages_with_rects()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("s.pdf"),
        [
            "alpha beta gamma",
            "delta needle epsilon",
            "zeta needle needle",
        ]);
        using var doc = PdfDocument.Load(path);
        var search = new PdfSearch(doc);

        var hits = search.FindAll("needle");

        Assert.Equal(3, hits.Count);
        Assert.Equal([1, 2, 2], hits.Select(h => h.PageIndex).ToArray());
        Assert.All(hits, h => Assert.NotEmpty(h.Rects));
        Assert.All(hits, h => Assert.All(h.Rects, r => Assert.True(r.Width > 0 && r.Height > 0)));
    }

    [Fact]
    public void FindAll_is_case_insensitive_by_default_and_case_sensitive_on_request()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("c.pdf"), ["Needle needle NEEDLE"]);
        using var doc = PdfDocument.Load(path);
        var search = new PdfSearch(doc);

        Assert.Equal(3, search.FindAll("needle").Count);
        Assert.Single(search.FindAll("needle", PdfSearchOptions.MatchCase));
    }

    [Fact]
    public void FindAll_empty_term_returns_nothing()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("e.pdf"), ["whatever"]);
        using var doc = PdfDocument.Load(path);

        Assert.Empty(new PdfSearch(doc).FindAll(""));
    }

    [Fact]
    public async Task Two_searches_over_one_document_are_independent()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("iso.pdf"),
            ["apple apple apple", "orange", "apple orange apple"]);
        using var doc = PdfDocument.Load(path);

        var a = new PdfSearch(doc);
        var b = new PdfSearch(doc);

        var appleTask = Task.Run(() => a.FindAll("apple"));
        var orangeTask = Task.Run(() => b.FindAll("orange"));
        var results = await Task.WhenAll(appleTask, orangeTask);

        Assert.Equal(5, results[0].Count);
        Assert.Equal(2, results[1].Count);
    }

    [Fact]
    public void Extracted_page_text_contains_drawn_line()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("t.pdf"), ["Hello", "Catalogued world"]);
        using var doc = PdfDocument.Load(path);

        Assert.Contains("Catalogued world", PdfTextExtractor.GetPageText(doc, 1));
    }

    [Fact]
    public void HitAt_resolves_a_known_range_without_scanning_other_pages()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("h.pdf"),
            ["alpha beta gamma", "delta needle epsilon", "zeta needle needle"]);
        using var doc = PdfDocument.Load(path);
        var search = new PdfSearch(doc);

        SearchHit known = search.FindAll("needle").Single(h => h.PageIndex == 1);
        SearchHit? resolved = search.HitAt(known.PageIndex, known.CharStart, known.CharCount);

        Assert.NotNull(resolved);
        Assert.Equal(known.PageIndex, resolved!.PageIndex);
        Assert.Equal(known.CharStart, resolved.CharStart);
        Assert.NotEmpty(resolved.Rects);
        Assert.Equal(known.Rects.Count, resolved.Rects.Count);
    }

    [Fact]
    public void FindAll_merges_a_multi_character_match_into_one_bounding_box_per_line()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("m.pdf"), ["find the needle in here"]);
        using var doc = PdfDocument.Load(path);
        var search = new PdfSearch(doc);

        var hits = search.FindAll("needle");

        Assert.Single(hits);
        SearchHit hit = hits[0];

        // One clean box for the whole word — not a rect per character (or per PDFium glyph run).
        Assert.Single(hit.Rects);

        // The box extends a little beyond the tight glyph union — a small pad so the highlight
        // doesn't sit flush against the letters (see PdfTextExtractor.MergeIntoLineRects).
        IReadOnlyList<PdfCharBox> charBoxes = PdfTextExtractor.GetCharBoxes(doc, hit.PageIndex);
        var matched = Enumerable.Range(hit.CharStart, hit.CharCount).Select(i => charBoxes[i].Box).ToList();
        Assert.True(hit.Rects[0].Left <= matched.Min(b => b.Left));
        Assert.True(hit.Rects[0].Right >= matched.Max(b => b.Right));
    }

    [Fact]
    public void HitAt_returns_null_for_a_range_past_the_end_of_the_page()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("stale.pdf"), ["short page"]);
        using var doc = PdfDocument.Load(path);
        var search = new PdfSearch(doc);

        Assert.Null(search.HitAt(0, charStart: 9999, charCount: 5));
        Assert.Null(search.HitAt(pageIndex: 5, charStart: 0, charCount: 1));
    }
}
