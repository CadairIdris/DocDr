namespace DocDr.Pdf.Tests;

public sealed class PdfBookmarksTests
{
    [Fact]
    public void Read_returns_outline_entries_with_resolved_pages()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(
            ws.Path("outline.pdf"),
            Enumerable.Range(1, 10).Select(i => $"Page {i}").ToArray(),
            bookmarks:
            [
                new TestPdfBuilder.Bookmark("Introduction", 0),
                new TestPdfBuilder.Bookmark("Chapter 1", 2),
                new TestPdfBuilder.Bookmark("Chapter 2", 6),
                new TestPdfBuilder.Bookmark("Appendix", 9),
            ]);
        using var doc = PdfDocument.Load(path);

        var bookmarks = PdfBookmarks.Read(doc);

        Assert.Equal(4, bookmarks.Count);
        Assert.Equal(["Introduction", "Chapter 1", "Chapter 2", "Appendix"],
            bookmarks.Select(b => b.Title).ToArray());
        Assert.Equal([0, 2, 6, 9], bookmarks.Select(b => b.PageIndex).ToArray());
        Assert.All(bookmarks, b => Assert.Empty(b.Children));
    }

    [Fact]
    public void Read_returns_empty_when_the_document_has_no_outline()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("flat.pdf"), ["a", "b", "c"]);
        using var doc = PdfDocument.Load(path);

        Assert.Empty(PdfBookmarks.Read(doc));
    }
}
