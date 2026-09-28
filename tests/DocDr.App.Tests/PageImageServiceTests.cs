using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocDr.App.Services;
using DocDr.Pdf;

namespace DocDr.App.Tests;

public sealed class PageImageServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly PdfDocument _a;
    private readonly PdfDocument _b;

    public PageImageServiceTests()
    {
        _a = PdfDocument.Load(TestPdfBuilder.WritePdf(_dir.File("a.pdf"), ["ALPHA ALPHA", "two"]));
        _b = PdfDocument.Load(TestPdfBuilder.WritePdf(_dir.File("b.pdf"), ["beta"]));
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
        _dir.Dispose();
    }

    private static PageImageService Cached() =>
        new(new PageRenderer(), cacheEntries: 10, cacheBytes: 64L * 1024 * 1024);

    [Fact]
    public void Renders_an_opaque_frozen_bitmap_at_the_requested_size_and_dpi()
    {
        var image = (BitmapSource)new PageImageService(new PageRenderer()).Render(_a, 0, 150, 194, default, deviceScale: 1.5);

        Assert.True(image.IsFrozen);
        Assert.Equal(PixelFormats.Bgr32, image.Format);
        Assert.Equal(150, image.PixelWidth);
        Assert.Equal(194, image.PixelHeight);
        Assert.Equal(144, image.DpiX, precision: 3);
    }

    [Fact]
    public void A_repeat_request_is_served_from_the_cache_as_the_same_bitmap()
    {
        PageImageService service = Cached();

        ImageSource first = service.Render(_a, 0, 150, 194, default);

        Assert.Same(first, service.Render(_a, 0, 150, 194, default));
        Assert.NotSame(first, service.Render(_a, 0, 151, 194, default));          // size is in the key
        Assert.NotSame(first, service.Render(_a, 0, 150, 194, default, 1.25));    // so is DPI
        Assert.NotSame(first, service.Render(_b, 0, 150, 194, default));          // and the document
    }

    [Fact]
    public void Purge_drops_only_the_named_documents_bitmaps()
    {
        PageImageService service = Cached();
        ImageSource a = service.Render(_a, 0, 120, 155, default);
        ImageSource b = service.Render(_b, 0, 120, 155, default);

        service.Purge(_a);

        Assert.NotSame(a, service.Render(_a, 0, 120, 155, default));
        Assert.Same(b, service.Render(_b, 0, 120, 155, default));
    }

    [Fact]
    public void A_page_change_invalidates_cached_bitmaps_even_without_a_purge()
    {
        PageImageService service = Cached();
        ImageSource before = service.Render(_a, 0, 120, 155, default);

        _a.RotatePages([0], PdfRotation.Rotate180); // bumps ContentVersion

        Assert.NotSame(before, service.Render(_a, 0, 120, 155, default));
    }

    [Fact]
    public void Viewport_slices_are_never_cached()
    {
        PageImageService service = Cached();
        var region = new PageRenderRegion(600, 776, 100, 100);

        ImageSource first = service.Render(_a, 0, 200, 200, default, region: region);

        Assert.NotSame(first, service.Render(_a, 0, 200, 200, default, region: region));
        Assert.Equal(0, service.CacheStats.Entries);
    }
}
