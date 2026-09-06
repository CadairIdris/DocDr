namespace DocDr.Pdf.Tests;

public sealed class PdfWatermarkTests
{
    private const string Mark = "CONTROLLED COPY - NOT FOR DISTRIBUTION OUTSIDE THE OFFICE";

    private static string PageText(PdfDocument doc, int i) =>
        PdfTextExtractor.GetPageText(doc, i);

    [Fact]
    public void Scan_finds_a_text_stamp_repeated_on_every_page()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("wm.pdf"),
            ["Page one", "Page two", "Page three", "Page four", "Page five"], watermark: Mark);
        using var doc = PdfDocument.Load(path);

        WatermarkCandidate c = Assert.Single(PdfWatermarks.Scan(doc));
        Assert.Equal(WatermarkKind.Text, c.Kind);
        Assert.Equal(5, c.PageCount);
        Assert.Equal(5, c.TotalPages);
        Assert.True(c.OnEveryPage);
        Assert.Contains("CONTROLLED COPY", c.Label);
    }

    [Fact]
    public void Removing_a_watermark_strips_it_from_every_page_and_keeps_the_body()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("wm.pdf"),
            ["Alpha content", "Bravo content", "Charlie content"], watermark: Mark);
        byte[] saved;

        using (var doc = PdfDocument.Load(path))
        {
            Assert.Contains(Mark, PageText(doc, 0));

            doc.RemoveWatermarks(PdfWatermarks.Scan(doc));
            Assert.True(doc.IsDirty);

            for (int i = 0; i < doc.PageCount; i++)
            {
                Assert.DoesNotContain("CONTROLLED COPY", PageText(doc, i));
            }

            Assert.Contains("Alpha content", PageText(doc, 0));
            Assert.Contains("Charlie content", PageText(doc, 2));
            saved = doc.SaveToBytes();
        }

        using var reloaded = PdfDocument.Load(saved);
        Assert.Equal(3, reloaded.PageCount);
        Assert.Empty(PdfWatermarks.Scan(reloaded));
        Assert.DoesNotContain("CONTROLLED COPY", PageText(reloaded, 1));
        Assert.Contains("Bravo content", PageText(reloaded, 1));
    }

    [Fact]
    public void A_stamp_on_only_some_pages_is_not_flagged()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("partial.pdf"),
            ["p1", "p2", "p3", "p4", "p5", "p6"], watermark: Mark, watermarkOnFirstNPages: 2);
        using var doc = PdfDocument.Load(path);

        Assert.Empty(PdfWatermarks.Scan(doc));
    }

    [Fact]
    public void A_document_with_no_repeating_content_yields_no_candidates()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("plain.pdf"),
            ["Introduction", "Methodology", "Results", "Discussion"]);
        using var doc = PdfDocument.Load(path);

        Assert.Empty(PdfWatermarks.Scan(doc));
    }

    [Fact]
    public void Watermark_removal_survives_a_later_rotate_and_reload()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("wm.pdf"),
            ["one", "two", "three", "four"], watermark: Mark);

        using var doc = PdfDocument.Load(path);
        doc.RemoveWatermarks(PdfWatermarks.Scan(doc));
        doc.RotatePages([0], PdfRotation.Clockwise90);

        using var reloaded = PdfDocument.Load(doc.SaveToBytes());
        Assert.Equal(4, reloaded.PageCount);
        Assert.Equal(PdfRotation.Clockwise90, reloaded.GetPageRotation(0));
        for (int i = 0; i < reloaded.PageCount; i++)
        {
            Assert.DoesNotContain("CONTROLLED COPY", PageText(reloaded, i));
        }
    }

    [Fact]
    public void Removing_a_watermark_clears_the_undo_history()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("wm.pdf"),
            ["one", "two", "three", "four"], watermark: Mark);

        using var doc = PdfDocument.Load(path);
        doc.RotatePages([0], PdfRotation.Clockwise90);
        Assert.True(doc.CanUndo);

        doc.RemoveWatermarks(PdfWatermarks.Scan(doc));

        Assert.False(doc.CanUndo);
        Assert.False(doc.CanRedo);

        // The rotation is baked into the cleaned bytes and the document still works.
        doc.RotatePages([1], PdfRotation.Clockwise90);
        using var reloaded = PdfDocument.Load(doc.SaveToBytes());
        Assert.Equal(PdfRotation.Clockwise90, reloaded.GetPageRotation(0));
        Assert.Equal(PdfRotation.Clockwise90, reloaded.GetPageRotation(1));
    }

    [Fact]
    public void Body_text_that_looks_like_the_watermark_is_left_alone()
    {
        using var ws = new TempWorkspace();
        // Only page 3 carries this line as real content; the stamp is on every page.
        string path = TestPdfBuilder.WritePdf(ws.Path("wm.pdf"),
            ["intro", "method", "quoting the CONTROLLED COPY notice here", "results", "end"],
            watermark: Mark);

        using var doc = PdfDocument.Load(path);
        doc.RemoveWatermarks(PdfWatermarks.Scan(doc));

        Assert.Contains("quoting the CONTROLLED COPY notice here", PageText(doc, 2));
        Assert.DoesNotContain(Mark, PageText(doc, 2));
    }

    [Fact]
    public void Removing_nothing_selected_is_a_no_op()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("wm.pdf"), ["a", "b", "c"], watermark: Mark);
        using var doc = PdfDocument.Load(path);

        doc.RemoveWatermarks([]);

        Assert.False(doc.IsDirty);
        Assert.Contains("CONTROLLED COPY", PageText(doc, 0));
    }
}
