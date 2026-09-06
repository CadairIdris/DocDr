namespace DocDr.Pdf.Tests;

public sealed class PdfMetadataTests
{
    [Fact]
    public void GetInfo_reads_present_fields()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("meta.pdf"), ["x"],
            new TestPdfBuilder.PdfInfo(Title: "Quarterly Report", Author: "R. Scarisbrick"));
        using var doc = PdfDocument.Load(path);

        PdfDocumentInfo info = PdfMetadata.GetInfo(doc);

        Assert.Equal("Quarterly Report", info.Title);
        Assert.Equal("R. Scarisbrick", info.Author);
    }

    [Fact]
    public void GetInfo_returns_empty_strings_when_metadata_absent()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("nometa.pdf"), ["x"]);
        using var doc = PdfDocument.Load(path);

        PdfDocumentInfo info = PdfMetadata.GetInfo(doc);

        Assert.Equal(string.Empty, info.Title);
        Assert.Equal(string.Empty, info.Author);
        Assert.Equal(string.Empty, info.Keywords);
    }
}
