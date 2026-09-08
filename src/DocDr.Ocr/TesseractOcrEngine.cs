using DocDr.Pdf;
using TesseractOCR;
using TesseractOCR.Enums;
using TesseractOCR.Layout;

namespace DocDr.Ocr;

/// <summary>
/// An <see cref="IOcrEngine"/> backed by Tesseract 5 (the <c>TesseractOCR</c> wrapper). The
/// recognition model (<c>eng.traineddata</c>, tessdata_fast) ships next to the app in
/// <c>tessdata/</c>. One instance is not thread-safe — create one per OCR run.
/// </summary>
public sealed class TesseractOcrEngine : IOcrEngine
{
    /// <summary>DPI the caller should rasterise pages at; matches what the engine is told.</summary>
    public const int RecommendedDpi = 300;

    private readonly Engine _engine;

    public TesseractOcrEngine(string? tessDataPath = null, Language language = Language.English)
    {
        string path = tessDataPath ?? Path.Combine(AppContext.BaseDirectory, "tessdata");
        _engine = new Engine(path, language, EngineMode.Default);
        _engine.SetVariable("user_defined_dpi", RecommendedDpi);
    }

    public IReadOnlyList<OcrWord> Recognise(RenderedPage page, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        byte[] bmp = BmpWriter.FromRendered(page, RecommendedDpi);
        var words = new List<OcrWord>();

        using TesseractOCR.Pix.Image image = TesseractOCR.Pix.Image.LoadFromMemory(bmp);
        using Page result = _engine.Process(image, PageSegMode.Auto);

        foreach (Block block in result.Layout)
        {
            foreach (Paragraph paragraph in block.Paragraphs)
            {
                foreach (TextLine line in paragraph.TextLines)
                {
                    foreach (Word word in line.Words)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string text = word.Text;
                        TesseractOCR.Rect? box = word.BoundingBox;
                        if (box is null || string.IsNullOrWhiteSpace(text))
                        {
                            continue;
                        }

                        TesseractOCR.Rect r = box.Value;
                        words.Add(new OcrWord(
                            text, r.X1, r.Y1, r.Width, r.Height, (float)word.Confidence));
                    }
                }
            }
        }

        return words;
    }

    public void Dispose() => _engine.Dispose();
}
