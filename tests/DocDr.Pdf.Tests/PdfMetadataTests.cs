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

    [Fact]
    public void UpdateInfo_persists_through_save_and_reload()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("m.pdf"), ["a", "b"],
            new TestPdfBuilder.PdfInfo(Title: "Old", Author: "Nobody"));
        byte[] saved;

        using (var doc = PdfDocument.Load(path))
        {
            doc.UpdateInfo(doc.Info with
            {
                Title = "New Title",
                Subject = "Q3 résumé — final",   // non-ASCII forces the hex-string path
                Keywords = "alpha, beta",
            });
            Assert.True(doc.IsDirty);
            saved = doc.SaveToBytes();
        }

        using var reloaded = PdfDocument.Load(saved);
        Assert.Equal("New Title", reloaded.Info.Title);
        Assert.Equal("Q3 résumé — final", reloaded.Info.Subject);
        Assert.Equal("alpha, beta", reloaded.Info.Keywords);
        Assert.Equal("Nobody", reloaded.Info.Author); // untouched field kept
        Assert.NotEqual(string.Empty, reloaded.Info.ModificationDate); // stamped on save
    }

    [Fact]
    public void Metadata_survives_a_structural_edit_and_save()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("s.pdf"), ["a", "b", "c", "d"],
            new TestPdfBuilder.PdfInfo(Title: "Keep Me", Author: "A. Author"));
        byte[] saved;

        using (var doc = PdfDocument.Load(path))
        {
            doc.DeletePages([1]); // rebuild — the rebuilt handle has no Info dict
            saved = doc.SaveToBytes();
        }

        using var reloaded = PdfDocument.Load(saved);
        Assert.Equal(3, reloaded.PageCount);
        Assert.Equal("Keep Me", reloaded.Info.Title);
        Assert.Equal("A. Author", reloaded.Info.Author);
    }
}
