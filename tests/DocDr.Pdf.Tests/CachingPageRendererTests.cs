namespace DocDr.Pdf.Tests;

public sealed class CachingPageRendererTests
{
    [Fact]
    public void Different_documents_do_not_share_cache_entries()
    {
        using var ws = new TempWorkspace();
        string pathA = TestPdfBuilder.WritePdf(ws.Path("a.pdf"), ["ALPHA ALPHA ALPHA"]);
        string pathB = TestPdfBuilder.WritePdf(ws.Path("b.pdf"), ["beta"]);
        using var docA = PdfDocument.Load(pathA);
        using var docB = PdfDocument.Load(pathB);
        var cache = new CachingPageRenderer(new PageRenderer());

        RenderedPage a = cache.Render(docA, 0, 150, 194);
        RenderedPage b = cache.Render(docB, 0, 150, 194);

        Assert.NotSame(a, b);
        Assert.False(a.Pixels.AsSpan().SequenceEqual(b.Pixels), "Two different documents rendered identical pixels — cache keys collided.");
    }

    [Fact]
    public void Purge_drops_only_the_named_documents_pages()
    {
        using var ws = new TempWorkspace();
        string pathA = TestPdfBuilder.WritePdf(ws.Path("a.pdf"), ["a"]);
        string pathB = TestPdfBuilder.WritePdf(ws.Path("b.pdf"), ["b"]);
        using var docA = PdfDocument.Load(pathA);
        using var docB = PdfDocument.Load(pathB);
        var cache = new CachingPageRenderer(new PageRenderer());

        RenderedPage a1 = cache.Render(docA, 0, 120, 155);
        RenderedPage b1 = cache.Render(docB, 0, 120, 155);

        cache.Purge(docA);

        Assert.NotSame(a1, cache.Render(docA, 0, 120, 155)); // re-rendered
        Assert.Same(b1, cache.Render(docB, 0, 120, 155));    // still cached
    }
}
