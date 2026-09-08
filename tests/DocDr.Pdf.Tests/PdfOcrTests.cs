using System.Linq;
using DocDr.Ocr;
using DocDr.Pdf;

namespace DocDr.Pdf.Tests;

public sealed class PdfOcrTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "docdr-ocr-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>An engine that pretends every page says the same thing at a known spot.</summary>
    private sealed class StubOcrEngine : IOcrEngine
    {
        private readonly IReadOnlyList<OcrWord> _words;
        public int Calls { get; private set; }

        public StubOcrEngine(params OcrWord[] words) => _words = words;

        public IReadOnlyList<OcrWord> Recognise(RenderedPage page, CancellationToken ct = default)
        {
            Calls++;
            return _words;
        }

        public void Dispose() { }
    }

    [Fact]
    public void PagesWithoutText_lists_only_the_blank_pages()
    {
        string path = TestPdfBuilder.WritePdf(
            Path.Combine(_dir, "mixed.pdf"), ["Real text here", "", "   "]);
        using PdfDocument doc = PdfDocument.Load(path);

        Assert.Equal(new[] { 1, 2 }, doc.PagesWithoutText());
    }

    [Fact]
    public void AddOcrTextLayer_writes_a_searchable_layer_on_blank_pages()
    {
        // Page 0 has text; pages 1 and 2 are blank and should get the stub's word.
        string path = TestPdfBuilder.WritePdf(
            Path.Combine(_dir, "scan.pdf"), ["First page text", "", ""]);
        using PdfDocument doc = PdfDocument.Load(path);

        // A word ~40px tall near the top-left of a 300 DPI render of a 612x792pt page.
        var stub = new StubOcrEngine(new OcrWord("KESTREL", 120, 90, 260, 44, 96f));

        OcrResult result = doc.AddOcrTextLayer(stub);

        Assert.Equal(2, result.PagesProcessed);
        Assert.Equal(1, result.PagesAlreadyHadText);
        Assert.Equal(2, result.WordsAdded);
        Assert.Equal(2, stub.Calls);

        Assert.Contains("KESTREL", PdfTextExtractor.GetPageText(doc, 1));
        Assert.Contains("KESTREL", PdfTextExtractor.GetPageText(doc, 2));
        Assert.DoesNotContain("KESTREL", PdfTextExtractor.GetPageText(doc, 0));
        Assert.Empty(doc.PagesWithoutText());
        Assert.True(doc.IsDirty);
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void AddOcrTextLayer_survives_a_save_and_reload()
    {
        string path = TestPdfBuilder.WritePdf(Path.Combine(_dir, "scan2.pdf"), ["", ""]);
        using PdfDocument doc = PdfDocument.Load(path);
        var stub = new StubOcrEngine(new OcrWord("MERLIN", 100, 100, 220, 40, 95f));

        doc.AddOcrTextLayer(stub);
        byte[] saved = doc.SaveToBytes();

        using PdfDocument reloaded = PdfDocument.Load(saved);
        Assert.Contains("MERLIN", PdfTextExtractor.GetPageText(reloaded, 0));
        Assert.Equal(2, reloaded.PageCount);
    }

    [Fact]
    public void AddOcrTextLayer_is_a_no_op_when_every_page_has_text()
    {
        string path = TestPdfBuilder.WritePdf(Path.Combine(_dir, "born-digital.pdf"), ["Alpha", "Beta"]);
        using PdfDocument doc = PdfDocument.Load(path);
        var stub = new StubOcrEngine(new OcrWord("NOPE", 10, 10, 40, 20, 99f));

        OcrResult result = doc.AddOcrTextLayer(stub);

        Assert.Equal(0, result.PagesProcessed);
        Assert.Equal(0, stub.Calls);
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void TesseractOcrEngine_reads_rendered_text()
    {
        string path = TestPdfBuilder.WritePdf(Path.Combine(_dir, "legible.pdf"), ["KESTREL FALCON"]);
        using PdfDocument doc = PdfDocument.Load(path);

        PdfSize size = doc.GetPageSize(0);
        (int w, int h) = (
            (int)Math.Round(size.Width / 72.0 * 300),
            (int)Math.Round(size.Height / 72.0 * 300));
        RenderedPage rendered = new PageRenderer().Render(doc, 0, w, h);

        using var engine = new TesseractOcrEngine();
        IReadOnlyList<OcrWord> words = engine.Recognise(rendered);

        string joined = string.Join(" ", words.Select(x => x.Text.ToUpperInvariant()));
        Assert.Contains("KESTREL", joined);
        Assert.Contains("FALCON", joined);
    }
}
