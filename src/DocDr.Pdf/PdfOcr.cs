using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>One recognised word. <see cref="X"/>/<see cref="Y"/>/<see cref="Width"/>/<see cref="Height"/>
/// are in the rendered image's pixel space (top-left origin, y grows down).</summary>
public readonly record struct OcrWord(string Text, float X, float Y, float Width, float Height, float Confidence);

/// <summary>Progress for an OCR run: page <see cref="Page"/> of <see cref="TotalPages"/> just
/// finished, contributing <see cref="WordsOnPage"/> words.</summary>
public readonly record struct OcrProgress(int Page, int TotalPages, int WordsOnPage);

/// <summary>Outcome of an OCR run.</summary>
public sealed record OcrResult(int PagesProcessed, int PagesAlreadyHadText, int WordsAdded);

/// <summary>An engine that recognises text in a rasterised page.</summary>
public interface IOcrEngine : IDisposable
{
    /// <summary>Recognise the words in <paramref name="page"/>. Boxes are in image pixel space.</summary>
    IReadOnlyList<OcrWord> Recognise(RenderedPage page, CancellationToken cancellationToken = default);
}

/// <summary>
/// Writes a recognised, invisible (render-mode 3) text layer onto a page so the visible scan stays
/// put while search / selection / clause detection / extraction all work off it. Each word is
/// horizontally scaled to its image box via the Helvetica AFM table so selection highlights land
/// roughly on the glyphs.
/// </summary>
internal static class PdfOcr
{
    /// <summary>Words below this Tesseract confidence are dropped.</summary>
    internal const float MinConfidence = 40f;

    /// <summary>DPI the no-text pages are rasterised at for recognition.</summary>
    internal const int RenderDpi = 300;

    /// <summary>Longest rendered edge, in pixels — caps memory on very large pages.</summary>
    internal const int MaxEdge = 4200;

    /// <summary>Pixel size to rasterise a page of the given point size at <see cref="RenderDpi"/>.</summary>
    internal static (int Width, int Height) PixelSize(PdfSize points)
    {
        double scale = RenderDpi / 72.0;
        double w = points.Width * scale;
        double h = points.Height * scale;
        double longest = Math.Max(w, h);
        if (longest > MaxEdge)
        {
            double k = MaxEdge / longest;
            w *= k;
            h *= k;
        }

        return (Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));
    }

    internal static int WritePageTextLayer(
        FpdfDocumentT document,
        FpdfPageT page,
        FpdfFontT font,
        IReadOnlyList<OcrWord> words,
        PdfSize pageSizePoints,
        PdfPoint cropOrigin,
        int imageWidth,
        int imageHeight)
    {
        if (words.Count == 0 || imageWidth <= 0 || imageHeight <= 0
            || pageSizePoints.Width <= 0 || pageSizePoints.Height <= 0)
        {
            return 0;
        }

        double sx = pageSizePoints.Width / imageWidth;
        double sy = pageSizePoints.Height / imageHeight;
        int written = 0;

        foreach (OcrWord w in words)
        {
            string text = w.Text.Trim();
            if (text.Length == 0 || w.Confidence < MinConfidence || w.Width <= 0 || w.Height <= 0)
            {
                continue;
            }

            double fontSize = Math.Max(1, w.Height * sy);
            double targetWidth = w.Width * sx;
            double naturalWidth = Math.Max(0.1, PdfTextWrap.MeasureHelvetica(text, fontSize));
            double hScale = Math.Clamp(targetWidth / naturalWidth, 0.05, 20.0);

            // The rendered image spans the CropBox; page space is MediaBox-relative and
            // bottom-left origin, so add the crop offset and flip Y.
            double baseline = cropOrigin.Y + pageSizePoints.Height - ((w.Y + w.Height) * sy);
            double left = cropOrigin.X + (w.X * sx);

            FpdfPageobjectT obj = fpdf_edit.FPDFPageObjCreateTextObj(document, font, (float)fontSize);
            ushort[] wide = PdfTextExtractor.ToWideString(text);
            fpdf_edit.FPDFTextSetText(obj, ref wide[0]);
            fpdf_edit.FPDFTextObjSetTextRenderMode(
                obj, FPDF_TEXT_RENDERMODE.FPDF_TEXTRENDERMODE_INVISIBLE);
            fpdf_edit.FPDFPageObjTransform(obj, hScale, 0, 0, 1, left, baseline);
            fpdf_edit.FPDFPageInsertObject(page, obj);
            written++;
        }

        if (written > 0)
        {
            fpdf_edit.FPDFPageGenerateContent(page);
        }

        return written;
    }
}
