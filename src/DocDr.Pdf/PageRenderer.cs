using System.Runtime.InteropServices;
using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>A rasterised page: a tightly-packed 32-bit BGRx buffer (B, G, R, then an unused
/// fourth byte — the page is always opaque, so there is no alpha channel to honour).</summary>
public sealed class RenderedPage
{
    public RenderedPage(int pageIndex, int pixelWidth, int pixelHeight, int stride, byte[] pixels)
    {
        PageIndex = pageIndex;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        Stride = stride;
        Pixels = pixels;
    }

    public int PageIndex { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }

    /// <summary>Row length in bytes (<see cref="PixelWidth"/> * 4).</summary>
    public int Stride { get; }

    /// <summary>BGRx, top row first. Only the first three bytes of each pixel are meaningful. Length is <see cref="Stride"/> * <see cref="PixelHeight"/>.</summary>
    public byte[] Pixels { get; }

    public long ByteCount => Pixels.LongLength;
}

/// <summary>A sub-rectangle of a page to rasterise at full resolution: the page is drawn as if it
/// were <see cref="FullWidth"/> x <see cref="FullHeight"/> px, shifted up and left by
/// (<see cref="OffsetX"/>, <see cref="OffsetY"/>), into a buffer of the size the caller asked for.
/// Lets the viewer render just the on-screen slice of a heavily-zoomed page at native resolution
/// instead of downscaling the whole page to fit a size cap.</summary>
public readonly record struct PageRenderRegion(int FullWidth, int FullHeight, int OffsetX, int OffsetY);

public interface IPageRenderer
{
    /// <summary>Render one page to a BGRx buffer of exactly <paramref name="pixelWidth"/> x
    /// <paramref name="pixelHeight"/>. With <paramref name="region"/> set, only that slice of the
    /// (notionally much larger) page lands in the buffer.</summary>
    RenderedPage Render(PdfDocument document, int pageIndex, int pixelWidth, int pixelHeight, CancellationToken cancellationToken = default, PageRenderRegion? region = null);
}

/// <summary>
/// Renders pages via PDFium into a pinned managed buffer (<see cref="Render"/>) or caller-owned
/// memory (<see cref="RenderInto"/>). No caching — the App caches finished bitmaps above this
/// (<see cref="SizedLruCache{TKey,TValue}"/>). All PDFium work runs under the document lock.
/// </summary>
public sealed class PageRenderer : IPageRenderer
{
    private const uint OpaqueWhite = 0xFFFFFFFFu;

    // Greyscale text anti-aliasing, not LCD subpixel. The App shows this bitmap through WPF,
    // which resamples it whenever the page box isn't a pixel-exact match (zoom, fractional
    // fit sizes, DPI). LCD subpixel edges only look right blitted 1:1 to the panel — resampled
    // they smear into visible fuzz and colour fringing. Greyscale AA resamples cleanly.
    private const PdfiumRenderFlags DefaultFlags = PdfiumRenderFlags.Annotations;

    // Opaque BGRx, not BGRA. A page is always painted onto opaque white, so an alpha channel buys
    // nothing and costs twice: PDFium's compositing paths for an alpha bitmap are slower, and the
    // App would otherwise hand WPF a Bgra32 image that it format-converts on upload and then
    // alpha-blends on every frame. (PDFium also only permits LCD text on a bitmap without alpha.)
    private const PdfiumBitmapFormat BitmapFormat = PdfiumBitmapFormat.Bgrx;

    public RenderedPage Render(PdfDocument document, int pageIndex, int pixelWidth, int pixelHeight, CancellationToken cancellationToken = default, PageRenderRegion? region = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.ValidatePageIndex(pageIndex);
        ValidateSize(pixelWidth, pixelHeight);

        int stride = checked(pixelWidth * 4);
        var pixels = new byte[checked(stride * pixelHeight)];

        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            RenderInto(document, pageIndex, pixelWidth, pixelHeight, pinned.AddrOfPinnedObject(), stride, cancellationToken, region);
        }
        finally
        {
            pinned.Free();
        }

        return new RenderedPage(pageIndex, pixelWidth, pixelHeight, stride, pixels);
    }

    /// <summary>
    /// Render one page into caller-owned memory (BGRx, top row first) — the same output as
    /// <see cref="Render"/>, without allocating a managed buffer. The App uses this to rasterise
    /// into short-lived native memory that WPF copies straight into its bitmap, rather than a
    /// fresh multi-megabyte large-object-heap array per page. <paramref name="buffer"/> must hold
    /// at least <paramref name="stride"/> × <paramref name="pixelHeight"/> bytes and stay valid
    /// for the duration of the call.
    /// </summary>
    public void RenderInto(PdfDocument document, int pageIndex, int pixelWidth, int pixelHeight, IntPtr buffer, int stride, CancellationToken cancellationToken = default, PageRenderRegion? region = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.ValidatePageIndex(pageIndex);
        ValidateSize(pixelWidth, pixelHeight);
        if (buffer == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        if (stride < pixelWidth * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), "Stride is smaller than one row of pixels.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        document.Locked(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            FpdfPageT? page = fpdfview.FPDF_LoadPage(document.Handle, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                throw new PdfException(PdfiumError.PageNotFoundOrContentError,
                    $"Could not load page {pageIndex}.");
            }

            try
            {
                FpdfBitmapT? bitmap = fpdfview.FPDFBitmapCreateEx(
                    pixelWidth, pixelHeight, (int)BitmapFormat, buffer, stride);
                if (bitmap is null || bitmap.__Instance == IntPtr.Zero)
                {
                    throw new PdfException("PDFium refused to create the render bitmap.");
                }

                try
                {
                    fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, pixelWidth, pixelHeight, OpaqueWhite);
                    if (region is { } r)
                    {
                        // Draw the whole page at its full (large) size, shifted so the wanted slice
                        // falls in the buffer; PDFium clips everything outside it.
                        fpdfview.FPDF_RenderPageBitmap(
                            bitmap, page, -r.OffsetX, -r.OffsetY, r.FullWidth, r.FullHeight, 0, (int)DefaultFlags);
                    }
                    else
                    {
                        fpdfview.FPDF_RenderPageBitmap(
                            bitmap, page, 0, 0, pixelWidth, pixelHeight, 0, (int)DefaultFlags);
                    }
                }
                finally
                {
                    fpdfview.FPDFBitmapDestroy(bitmap);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        });
    }

    private static void ValidateSize(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth), "Render size must be positive.");
        }
    }
}
