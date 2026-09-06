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

public interface IPageRenderer
{
    /// <summary>Render one page to a BGRA buffer of exactly <paramref name="pixelWidth"/> x <paramref name="pixelHeight"/>.</summary>
    RenderedPage Render(PdfDocument document, int pageIndex, int pixelWidth, int pixelHeight, CancellationToken cancellationToken = default);
}

/// <summary>
/// Renders pages via PDFium into a caller-owned pinned buffer. No caching — compose with
/// <see cref="CachingPageRenderer"/> for that. All PDFium work runs under the document lock.
/// </summary>
public sealed class PageRenderer : IPageRenderer
{
    private const uint OpaqueWhite = 0xFFFFFFFFu;

    private const PdfiumRenderFlags DefaultFlags =
        PdfiumRenderFlags.Annotations | PdfiumRenderFlags.LcdText;

    public RenderedPage Render(PdfDocument document, int pageIndex, int pixelWidth, int pixelHeight, CancellationToken cancellationToken = default)
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
                    fpdfview.FPDF_RenderPageBitmap(
                        bitmap, page, 0, 0, pixelWidth, pixelHeight, 0, (int)DefaultFlags);
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
