namespace DocDr.Pdf.Tests;

public sealed class PdfLinkTests
{
    [Fact]
    public void Reads_in_document_link_targets()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("toc.pdf"),
            ["Contents", "one", "two", "three", "four"],
            links:
            [
                new TestPdfBuilder.Link(FromPage: 0, ToPage: 2),
                new TestPdfBuilder.Link(FromPage: 0, ToPage: 4),
            ]);
        using var doc = PdfDocument.Load(path);

        IReadOnlyList<PdfLink> links = PdfLinks.Read(doc, 0);

        Assert.Equal(2, links.Count);
        Assert.Equal([2, 4], links.Select(l => l.TargetPageIndex).OrderBy(p => p));
        Assert.All(links, l => Assert.Null(l.Uri));
        Assert.All(links, l => Assert.True(l.Rect.Width > 0 && l.Rect.Height > 0));
    }

    [Fact]
    public void Pages_without_links_return_nothing()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("toc.pdf"),
            ["Contents", "one", "two"],
            links: [new TestPdfBuilder.Link(0, 2)]);
        using var doc = PdfDocument.Load(path);

        Assert.Empty(PdfLinks.Read(doc, 1));
        Assert.Single(PdfLinks.Read(doc, 0));
    }

    [Fact]
    public void A_plain_document_has_no_links()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("plain.pdf"), ["a", "b", "c"]);
        using var doc = PdfDocument.Load(path);

        Assert.Empty(PdfLinks.Read(doc, 0));
    }

    [Fact]
    public void Rect_is_in_unrotated_page_space_near_the_top()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("toc.pdf"),
            ["Contents", "one", "two"], links: [new TestPdfBuilder.Link(0, 2)]);
        using var doc = PdfDocument.Load(path);

        PdfLink link = Assert.Single(PdfLinks.Read(doc, 0));
        PdfSize page = doc.GetUnrotatedPageSize(0);

        // The builder puts the band ~60pt below the top edge; Top is the larger Y.
        Assert.True(link.Rect.Top > link.Rect.Bottom);
        Assert.InRange(link.Rect.Top, page.Height - 80, page.Height - 40);
    }
}
