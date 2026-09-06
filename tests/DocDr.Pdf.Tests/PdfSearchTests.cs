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
}
