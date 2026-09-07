namespace DocDr.Pdf.Tests;

public sealed class PdfPrintSupportTests
{
    private const uint Yellow = 0xFFFFEB3Bu;

    private static int NonWhitePixels(RenderedPage page)
    {
        int count = 0;
        for (int i = 0; i + 3 < page.Pixels.Length; i += 4)
        {
            if (page.Pixels[i] != 0xFF || page.Pixels[i + 1] != 0xFF || page.Pixels[i + 2] != 0xFF)
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void WithAnnotationsBaked_renders_the_overlay_then_strips_it_again()
    {
        using var ws = new TempWorkspace();
        using PdfDocument doc = PdfDocument.Load(
            TestPdfBuilder.WritePdf(ws.Path("print.pdf"), ["alpha beta gamma delta"]));

        SearchHit hit = new PdfSearch(doc).FindAll("beta gamma").First();
        doc.AddAnnotation(0, PdfAnnotation.NewHighlight(hit.Rects, Yellow, note: null, author: "rob"));

        var renderer = new PageRenderer();
        int before = NonWhitePixels(renderer.Render(doc, 0, 300, 388));

        int baked = doc.WithAnnotationsBaked(() => NonWhitePixels(renderer.Render(doc, 0, 300, 388)));

        int after = NonWhitePixels(renderer.Render(doc, 0, 300, 388));

        Assert.True(baked > before, "The baked render should carry the highlight the plain render omits.");
        Assert.Equal(before, after); // stripped back off — the live handle is unchanged
        Assert.Single(doc.GetAnnotations(0)); // model still owns it
    }
}
