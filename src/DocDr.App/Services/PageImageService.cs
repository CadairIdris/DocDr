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

    public ImageSource Render(PdfDocument document, int pageIndex, int pixelWidth, int pixelHeight, CancellationToken cancellationToken)
    {
        RenderedPage page = _renderer.Render(document, pageIndex, pixelWidth, pixelHeight, cancellationToken);

        var bitmap = BitmapSource.Create(
            page.PixelWidth,
            page.PixelHeight,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            page.Pixels,
            page.Stride);

        bitmap.Freeze();
        return bitmap;
    }
}
