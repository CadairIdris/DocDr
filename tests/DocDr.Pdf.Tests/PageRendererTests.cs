namespace DocDr.Pdf.Tests;

public sealed class PageRendererTests
{
    [Fact]
    public void Render_produces_correctly_sized_buffer_with_visible_ink()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("ink.pdf"), ["The quick brown fox"]);
        using var doc = PdfDocument.Load(path);
        var renderer = new PageRenderer();

        const int w = 400;
        const int h = 518;
        RenderedPage page = renderer.Render(doc, 0, w, h);

        Assert.Equal(w, page.PixelWidth);
        Assert.Equal(h, page.PixelHeight);
        Assert.Equal(w * 4, page.Stride);
        Assert.Equal(page.Stride * h, page.Pixels.Length);

        Assert.True(HasNonWhitePixel(page), "Rendered page should contain drawn text, not be blank white.");
    }

    [Fact]
    public void CachingPageRenderer_returns_same_instance_for_repeat_request()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("cache.pdf"), ["a", "b"]);
        using var doc = PdfDocument.Load(path);
        var cache = new CachingPageRenderer(new PageRenderer());

        RenderedPage first = cache.Render(doc, 1, 200, 260);
        RenderedPage second = cache.Render(doc, 1, 200, 260);
        RenderedPage differentSize = cache.Render(doc, 1, 201, 260);

        Assert.Same(first, second);
        Assert.NotSame(first, differentSize);
    }

    [Fact]
    public void CachingPageRenderer_evicts_beyond_entry_budget()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("evict.pdf"), ["a", "b", "c"]);
        using var doc = PdfDocument.Load(path);
        var cache = new CachingPageRenderer(new PageRenderer(), maxEntries: 2);

        RenderedPage p0 = cache.Render(doc, 0, 100, 130);
        cache.Render(doc, 1, 100, 130);
        cache.Render(doc, 2, 100, 130); // evicts page 0 (least recently used)

        Assert.NotSame(p0, cache.Render(doc, 0, 100, 130));
    }

    private static bool HasNonWhitePixel(RenderedPage page)
    {
        for (int i = 0; i + 3 < page.Pixels.Length; i += 4)
        {
            if (page.Pixels[i] != 0xFF || page.Pixels[i + 1] != 0xFF || page.Pixels[i + 2] != 0xFF)
            {
                return true;
            }
        }

        return false;
    }
}
