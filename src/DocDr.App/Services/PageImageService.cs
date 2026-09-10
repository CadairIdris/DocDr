using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocDr.Pdf;

namespace DocDr.App.Services;

/// <summary>
/// Turns a PDFium page into a frozen, cross-thread-usable <see cref="ImageSource"/>.
/// <para>
/// PDFium rasterises straight into a tightly packed BGRA32 buffer (the spec's "render into a
/// pixel buffer, not System.Drawing.Bitmap" constraint); we wrap that buffer in a
/// <see cref="BitmapSource"/> and freeze it so the background thread can hand it to the UI.
/// </para>
/// </summary>
public sealed class PageImageService
{
    private readonly IPageRenderer _renderer;

    public PageImageService(IPageRenderer renderer)
    {
        _renderer = renderer;
    }

    /// <param name="deviceScale">Physical pixels per DIP the buffer was rasterised for. Baked into
    /// the <see cref="BitmapSource"/> DPI so a page rendered at, say, 1271 px on a 150% display
    /// reports a DIP width of 847.3 — the exact size of its on-screen box — and WPF blits it 1:1
    /// instead of scaling a 96-DPI (1271-DIP) image down into the box and back up to the panel.</param>
    public ImageSource Render(PdfDocument document, int pageIndex, int pixelWidth, int pixelHeight, CancellationToken cancellationToken, double deviceScale = 1.0, PageRenderRegion? region = null)
    {
        RenderedPage page = _renderer.Render(document, pageIndex, pixelWidth, pixelHeight, cancellationToken, region);

        double dpi = 96.0 * (deviceScale > 0 ? deviceScale : 1.0);
        var bitmap = BitmapSource.Create(
            page.PixelWidth,
            page.PixelHeight,
            dpi,
            dpi,
            PixelFormats.Bgra32,
            palette: null,
            page.Pixels,
            page.Stride);

        bitmap.Freeze();
        return bitmap;
    }
}
