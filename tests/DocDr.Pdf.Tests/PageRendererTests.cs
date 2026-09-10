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

    [Theory]
    [InlineData(0, 0)]
    [InlineData(200, 260)]
    [InlineData(150, 130)]
    public void Render_with_region_returns_the_matching_slice_of_the_full_render(int offX, int offY)
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("slice.pdf"), ["The quick brown fox jumps over the lazy dog"]);
        using var doc = PdfDocument.Load(path);
        var renderer = new PageRenderer();

        const int fullW = 400, fullH = 520, tileW = 200, tileH = 260;
        RenderedPage whole = renderer.Render(doc, 0, fullW, fullH);
        RenderedPage tile = renderer.Render(doc, 0, tileW, tileH, default,
            new PageRenderRegion(fullW, fullH, offX, offY));

        Assert.Equal(tileW, tile.PixelWidth);
        Assert.Equal(tileH, tile.PixelHeight);

        for (int y = 0; y < tileH; y++)
        {
            for (int x = 0; x < tileW; x++)
            {
                int ti = y * tile.Stride + x * 4;
                int wi = (y + offY) * whole.Stride + (x + offX) * 4;
                Assert.Equal(whole.Pixels[wi], tile.Pixels[ti]);
                Assert.Equal(whole.Pixels[wi + 1], tile.Pixels[ti + 1]);
                Assert.Equal(whole.Pixels[wi + 2], tile.Pixels[ti + 2]);
            }
        }
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
