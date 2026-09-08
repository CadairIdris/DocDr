using DocDr.App.ViewModels;
using DocDr.Pdf;

namespace DocDr.App.Tests;

public sealed class PageDisplayTests
{
    [Fact]
    public void Ordinal_is_the_one_based_page_number()
    {
        Assert.Equal("1", PageDisplay.Ordinal(0));
        Assert.Equal("42", PageDisplay.Ordinal(41));
    }

    [Fact]
    public void Label_is_the_ordinal_when_the_document_has_no_page_labels()
    {
        using var dir = new TempDir();
        using var doc = PdfDocument.Load(TestPdfBuilder.WritePdf(dir.File("plain.pdf"), ["A", "B"]));

        Assert.Equal("1", PageDisplay.Label(doc, 0));
        Assert.Equal("2", PageDisplay.Label(doc, 1));
    }

    [Fact]
    public void Label_uses_the_documents_printed_label_when_present()
    {
        using var dir = new TempDir();
        using var doc = PdfDocument.Load(TestPdfBuilder.WritePdf(
            dir.File("labelled.pdf"), ["A", "B", "C", "D"], romanFrontPages: 2));

        Assert.Equal("i", PageDisplay.Label(doc, 0));
        Assert.Equal("ii", PageDisplay.Label(doc, 1));
        Assert.Equal("1", PageDisplay.Label(doc, 2));
    }
}
