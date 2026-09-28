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
    public void RenderInto_writes_the_same_pixels_as_Render_into_caller_memory()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("into.pdf"), ["a", "The quick brown fox"]);
        using var doc = PdfDocument.Load(path);
        var renderer = new PageRenderer();
        const int w = 200, h = 260;

        RenderedPage reference = renderer.Render(doc, 1, w, h);

        // A padded stride, as a bitmap back buffer may have one.
        int stride = (w * 4) + 16;
        IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(stride * h);
        try
        {
            renderer.RenderInto(doc, 1, w, h, buffer, stride);

            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(buffer + (y * stride), row, 0, row.Length);
                for (int x = 0; x < w; x++)
                {
                    int o = x * 4, r = (y * reference.Stride) + o; // compare B, G, R — the 4th byte is unused
                    Assert.Equal(reference.Pixels[r], row[o]);
                    Assert.Equal(reference.Pixels[r + 1], row[o + 1]);
                    Assert.Equal(reference.Pixels[r + 2], row[o + 2]);
                }
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void RenderInto_rejects_a_stride_narrower_than_a_row()
    {
        using var ws = new TempWorkspace();
        using var doc = PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("s.pdf"), ["a"]));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PageRenderer().RenderInto(doc, 0, 100, 100, new IntPtr(1), stride: 399));
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
