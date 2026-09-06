namespace DocDr.Pdf.Tests;

public sealed class PdfDocumentTests
{
    [Fact]
    public void Load_reports_correct_page_count()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("three.pdf"),
            ["Page one", "Page two", "Page three"]);

        using var doc = PdfDocument.Load(path);

        Assert.Equal(3, doc.PageCount);
        Assert.Equal(path, doc.FilePath, ignoreCase: true);
    }

    [Fact]
    public void Load_missing_file_throws_FileNotFound()
    {
        using var ws = new TempWorkspace();
        Assert.Throws<FileNotFoundException>(() => PdfDocument.Load(ws.Path("nope.pdf")));
    }

    [Fact]
    public void Load_garbage_file_throws_PdfException()
    {
        using var ws = new TempWorkspace();
        string path = ws.Path("bad.pdf");
        File.WriteAllText(path, "this is not a pdf");

        var ex = Assert.Throws<PdfException>(() => PdfDocument.Load(path));
        Assert.NotEqual(PdfiumError.Success, ex.Error);
    }

    [Fact]
    public void GetPageSizes_matches_media_box()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("sized.pdf"), ["only page"]);

        using var doc = PdfDocument.Load(path);
        var size = doc.GetPageSize(0);

        Assert.Equal(TestPdfBuilder.PageWidth, size.Width, precision: 1);
        Assert.Equal(TestPdfBuilder.PageHeight, size.Height, precision: 1);
    }

    [Fact]
    public void Operations_after_dispose_throw()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("d.pdf"), ["x"]);

        var doc = PdfDocument.Load(path);
        doc.Dispose();

        Assert.Throws<ObjectDisposedException>(() => doc.Locked(() => 1));
    }
}
