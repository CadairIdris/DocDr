using System.Linq;
using DocDr.Pdf;

namespace DocDr.Pdf.Tests;

public sealed class PdfWebLinkTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "docdr-weblinks-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Bare_url_in_page_text_is_detected_as_a_link()
    {
        string path = TestPdfBuilder.WritePdf(
            Path.Combine(_dir, "urls.pdf"),
            ["See https://example.org/spec for the full text", "Nothing here"]);

        using PdfDocument doc = PdfDocument.Load(path);

        PdfLink link = Assert.Single(PdfLinks.Read(doc, 0), l => l.Uri is not null);
        Assert.Equal("https://example.org/spec", link.Uri);
        Assert.Null(link.TargetPageIndex);
        Assert.True(link.Rect.Width > 1 && link.Rect.Height > 1);

        Assert.DoesNotContain(PdfLinks.Read(doc, 1), l => l.Uri is not null);
    }
}
