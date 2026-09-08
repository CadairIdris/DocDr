using DocDr.Pdf;

namespace DocDr.Pdf.Tests;

public sealed class PdfPageLabelTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "docdr-labels-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Page_labels_read_roman_front_matter_then_decimal()
    {
        string path = TestPdfBuilder.WritePdf(
            Path.Combine(_dir, "labelled.pdf"),
            ["A", "B", "C", "D", "E"],
            romanFrontPages: 2);

        using PdfDocument doc = PdfDocument.Load(path);

        Assert.True(doc.HasPageLabels);
        Assert.Equal("i", doc.GetPageLabel(0));
        Assert.Equal("ii", doc.GetPageLabel(1));
        Assert.Equal("1", doc.GetPageLabel(2));  // printed page 1 sits at document position 3
        Assert.Equal("2", doc.GetPageLabel(3));
        Assert.Equal("3", doc.GetPageLabel(4));
    }

    [Fact]
    public void No_page_labels_tree_means_no_labels()
    {
        string path = TestPdfBuilder.WritePdf(Path.Combine(_dir, "plain.pdf"), ["A", "B"]);
        using PdfDocument doc = PdfDocument.Load(path);

        Assert.False(doc.HasPageLabels);
        Assert.Null(doc.GetPageLabel(0));
    }
}
