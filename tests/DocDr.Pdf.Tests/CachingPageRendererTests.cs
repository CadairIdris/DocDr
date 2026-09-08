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

    [Fact]
    public void Evicts_least_recently_used_once_the_entry_cap_is_hit()
    {
        using var ws = new TempWorkspace();
        using var doc = PdfDocument.Load(TestPdfBuilder.WritePdf(
            ws.Path("d.pdf"), ["p0", "p1", "p2", "p3"]));
        var fake = new FixedRenderer(bytesPerPage: 1024);
        var cache = new CachingPageRenderer(fake, maxEntries: 2, maxBytes: long.MaxValue);

        RenderedPage p0 = cache.Render(doc, 0, 10, 10);
        cache.Render(doc, 1, 10, 10);
        cache.Render(doc, 2, 10, 10); // evicts page 0 (LRU)

        Assert.Equal(2, cache.Stats.Entries);
        Assert.NotSame(p0, cache.Render(doc, 0, 10, 10)); // page 0 had to be re-rendered
    }

    [Fact]
    public void Evicts_by_total_byte_budget()
    {
        using var ws = new TempWorkspace();
        using var doc = PdfDocument.Load(TestPdfBuilder.WritePdf(
            ws.Path("d.pdf"), ["p0", "p1", "p2", "p3"]));
        var fake = new FixedRenderer(bytesPerPage: 900); // < maxBytes/3, so each one is cacheable
        var cache = new CachingPageRenderer(fake, maxEntries: 100, maxBytes: 3000);

        cache.Render(doc, 0, 10, 10);
        cache.Render(doc, 1, 10, 10);
        cache.Render(doc, 2, 10, 10);
        cache.Render(doc, 3, 10, 10); // 3600 > 3000 → oldest dropped

        Assert.Equal(3, cache.Stats.Entries);
        Assert.True(cache.Stats.Bytes <= 3000);
    }

    [Fact]
    public void A_single_render_larger_than_a_third_of_the_budget_bypasses_the_cache()
    {
        using var ws = new TempWorkspace();
        using var doc = PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("d.pdf"), ["p0"]));
        var fake = new FixedRenderer(bytesPerPage: 400);
        var cache = new CachingPageRenderer(fake, maxEntries: 100, maxBytes: 900); // third = 300

        RenderedPage first = cache.Render(doc, 0, 10, 10);

        Assert.Equal(0, cache.Stats.Entries);
        Assert.NotSame(first, cache.Render(doc, 0, 10, 10)); // never stored → fresh each call
    }

    /// <summary>An <see cref="IPageRenderer"/> that returns a buffer of a fixed size, so a test
    /// can drive the cache's byte/entry accounting without depending on real raster output.</summary>
    private sealed class FixedRenderer(int bytesPerPage) : IPageRenderer
    {
        public RenderedPage Render(PdfDocument document, int pageIndex, int pixelWidth, int pixelHeight, CancellationToken cancellationToken = default) =>
            new(pageIndex, pixelWidth, pixelHeight, pixelWidth * 4, new byte[bytesPerPage]);
    }
}
