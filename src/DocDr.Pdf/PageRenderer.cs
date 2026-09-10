using System.Runtime.InteropServices;
using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>A rasterised page: a tightly-packed BGRA32 (premultiplied-alpha-free, opaque) buffer.</summary>
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

    /// <summary>BGRA, top row first. Length is <see cref="Stride"/> * <see cref="PixelHeight"/>.</summary>
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
    /// <summary>Render one page to a BGRA buffer of exactly <paramref name="pixelWidth"/> x
    /// <paramref name="pixelHeight"/>. With <paramref name="region"/> set, only that slice of the
    /// (notionally much larger) page lands in the buffer.</summary>
    RenderedPage Render(PdfDocument document, int pageIndex, int pixelWidth, int pixelHeight, CancellationToken cancellationToken = default, PageRenderRegion? region = null);
}

/// <summary>
/// Renders pages via PDFium into a caller-owned pinned buffer. No caching — compose with
/// <see cref="CachingPageRenderer"/> for that. All PDFium work runs under the document lock.
/// </summary>
public sealed class PageRenderer : IPageRenderer
{
    private const uint OpaqueWhite = 0xFFFFFFFFu;

    // Greyscale text anti-aliasing, not LCD subpixel. The App shows this bitmap through WPF,
    // which resamples it whenever the page box isn't a pixel-exact match (zoom, fractional
    // fit sizes, DPI). LCD subpixel edges only look right blitted 1:1 to the panel — resampled
    // they smear into visible fuzz and colour fringing. Greyscale AA resamples cleanly.
    private const PdfiumRenderFlags DefaultFlags = PdfiumRenderFlags.Annotations;

    public RenderedPage Render(PdfDocument document, int pageIndex, int pixelWidth, int pixelHeight, CancellationToken cancellationToken = default, PageRenderRegion? region = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.ValidatePageIndex(pageIndex);
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth), "Render size must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        int stride = checked(pixelWidth * 4);
        var pixels = new byte[checked(stride * pixelHeight)];

        document.Locked(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            FpdfPageT? page = fpdfview.FPDF_LoadPage(document.Handle, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                throw new PdfException(PdfiumError.PageNotFoundOrContentError,
                    $"Could not load page {pageIndex}.");
            }

            var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                FpdfBitmapT? bitmap = fpdfview.FPDFBitmapCreateEx(
                    pixelWidth, pixelHeight, (int)PdfiumBitmapFormat.Bgra,
                    pinned.AddrOfPinnedObject(), stride);
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
                pinned.Free();
                fpdfview.FPDF_ClosePage(page);
            }
        });

        return new RenderedPage(pageIndex, pixelWidth, pixelHeight, stride, pixels);
    }
}
