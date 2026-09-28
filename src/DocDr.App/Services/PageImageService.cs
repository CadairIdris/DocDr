using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocDr.Pdf;

namespace DocDr.App.Services;

/// <summary>What a finished page bitmap was rendered for. <see cref="ContentVersion"/> is
/// <see cref="PdfDocument.ContentVersion"/> as read before the render, so a render still in flight
/// when the pages change (and landing after <see cref="PageImageService.Purge"/>) is filed under a
/// version nothing will ask for again, instead of being served as the new page. The DPI is part of
/// the key because it's baked into the <see cref="BitmapSource"/> (it sets the image's DIP size).</summary>
public readonly record struct PageImageKey(PdfDocument Document, int ContentVersion, int PageIndex, int PixelWidth, int PixelHeight, double Dpi);

/// <summary>
/// Turns a PDFium page into a frozen, cross-thread-usable <see cref="ImageSource"/>, optionally
/// caching the finished bitmaps.
/// <para>
/// PDFium rasterises into short-lived native memory (<see cref="PageRenderer.RenderInto"/>) that
/// <see cref="BitmapSource.Create(int, int, double, double, PixelFormat, BitmapPalette, IntPtr, int, int)"/>
/// copies straight into WPF's own bitmap, which is then frozen so the background thread can hand
/// it to the UI. No managed pixel array is involved, so a page render doesn't leave a multi-MB
/// large-object-heap array behind for the GC.
/// </para>
/// <para>
/// The cache holds those same frozen bitmaps — the very objects the page slots display — so a
/// page on screen is held once, not once as a cached raw buffer plus again as its bitmap, and a
/// cache hit is free: no copy, no allocation. (It used to cache raw BGRA below this layer.)
/// </para>
/// </summary>
public sealed class PageImageService
{
    private readonly PageRenderer _renderer;
    private readonly SizedLruCache<PageImageKey, BitmapSource>? _cache;

    /// <param name="cacheEntries">Zero (the default) for no cache — the print path and one-off
    /// previews render each page once.</param>
    public PageImageService(PageRenderer renderer, int cacheEntries = 0, long cacheBytes = 0)
    {
        _renderer = renderer;
        if (cacheEntries > 0 && cacheBytes > 0)
        {
            _cache = new SizedLruCache<PageImageKey, BitmapSource>(cacheEntries, cacheBytes);
        }
    }

    /// <summary>Entries and bytes the bitmap cache holds (zero without one) — for diagnostics.</summary>
    public (int Entries, long Bytes) CacheStats => _cache?.Stats ?? (0, 0);

    /// <summary>The cached bitmap for exactly this page / size / scale at the document's current
    /// content, or null. Cheap (a dictionary lookup) — safe to call on the UI thread, so a page
    /// that's already cached is shown immediately instead of waiting its turn on the render worker.</summary>
    public ImageSource? TryGetCached(PdfDocument document, int pageIndex, int pixelWidth, int pixelHeight, double deviceScale)
    {
        double dpi = 96.0 * (deviceScale > 0 ? deviceScale : 1.0);
        var key = new PageImageKey(document, document.ContentVersion, pageIndex, pixelWidth, pixelHeight, dpi);
        return _cache is not null && _cache.TryGet(key, out BitmapSource? hit) ? hit : null;
    }

    /// <summary>Drop every cached bitmap of <paramref name="document"/> (tab closed, or its pages changed).</summary>
    public void Purge(PdfDocument document) => _cache?.RemoveWhere(k => k.Document == document);

    /// <param name="deviceScale">Physical pixels per DIP the buffer was rasterised for. Baked into
    /// the <see cref="BitmapSource"/> DPI so a page rendered at, say, 1271 px on a 150% display
    /// reports a DIP width of 847.3 — the exact size of its on-screen box — and WPF blits it 1:1
    /// instead of scaling a 96-DPI (1271-DIP) image down into the box and back up to the panel.</param>
    /// <param name="region">A viewport (detail) slice. Never cached — it's keyed to a scroll
    /// position that changes constantly, so it would only churn the cache.</param>
    public ImageSource Render(PdfDocument document, int pageIndex, int pixelWidth, int pixelHeight, CancellationToken cancellationToken, double deviceScale = 1.0, PageRenderRegion? region = null)
    {
        double dpi = 96.0 * (deviceScale > 0 ? deviceScale : 1.0);
        var key = new PageImageKey(document, document.ContentVersion, pageIndex, pixelWidth, pixelHeight, dpi);
        bool cacheable = _cache is not null && region is null;

        if (cacheable && _cache!.TryGet(key, out BitmapSource? hit))
        {
            return hit;
        }

        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth), "Render size must be positive.");
        }

        int stride = checked(pixelWidth * 4);
        int size = checked(stride * pixelHeight);
        BitmapSource bitmap;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            _renderer.RenderInto(document, pageIndex, pixelWidth, pixelHeight, buffer, stride, cancellationToken, region);
            bitmap = BitmapSource.Create(
                pixelWidth,
                pixelHeight,
                dpi,
                dpi,
                PixelFormats.Bgr32, // opaque BGRx straight from PDFium — no alpha to convert or blend
                palette: null,
                buffer,
                size,
                stride);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        bitmap.Freeze();

        if (cacheable)
        {
            _cache!.Add(key, bitmap, size);
        }

        return bitmap;
    }
}
