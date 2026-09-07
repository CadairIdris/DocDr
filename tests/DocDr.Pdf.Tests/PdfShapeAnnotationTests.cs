namespace DocDr.Pdf.Tests;

public sealed class PdfShapeAnnotationTests
{
    private const uint Blue = 0xFF64B5F6u;

    private static PdfDocument Make(TempWorkspace ws, params string[] pages) =>
        PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("s.pdf"), pages));

    [Fact]
    public void Text_box_round_trips_through_save_and_reload()
    {
        using var ws = new TempWorkspace();
        byte[] saved;

        using (PdfDocument doc = Make(ws, "one", "two"))
        {
            var box = new PdfRect(100, 500, 300, 440); // left, top, right, bottom
            doc.AddAnnotation(1, PdfAnnotation.NewTextBox(box, "Revised per RFI 12", Blue, fontSize: 12, author: "rob"));
            saved = doc.SaveToBytes();
        }

        // The stamp appearance must carry real font resources so any viewer renders it.
        string raw = System.Text.Encoding.Latin1.GetString(saved);
        Assert.Contains("/Stamp", raw);
        Assert.Contains("/Type1", raw);
        Assert.Contains("DocDrShape", raw);

        using PdfDocument reloaded = PdfDocument.Load(saved);
        Assert.Empty(reloaded.GetAnnotations(0));
        PdfAnnotation a = Assert.Single(reloaded.GetAnnotations(1));
        Assert.Equal(PdfAnnotationKind.TextBox, a.Kind);
        Assert.Equal("Revised per RFI 12", a.Contents);
        Assert.Equal(12, a.FontSize, 1);
        Assert.Equal(100, a.Box.Left, 1);
        Assert.Equal(300, a.Box.Right, 1);
        Assert.Equal(500, a.Box.Top, 1);
        Assert.True(a.AutoSize); // a fresh box auto-fits its text
    }

    [Fact]
    public void Manual_resize_flag_round_trips()
    {
        using var ws = new TempWorkspace();
        byte[] saved;

        using (PdfDocument doc = Make(ws, "one"))
        {
            PdfAnnotation box = PdfAnnotation.NewTextBox(new PdfRect(80, 500, 300, 440), "sized by hand", Blue);
            doc.AddAnnotation(0, box with { AutoSize = false });
            saved = doc.SaveToBytes();
        }

        using PdfDocument reloaded = PdfDocument.Load(saved);
        Assert.False(reloaded.GetAnnotations(0)[0].AutoSize);
    }

    [Fact]
    public void Text_box_renders_visible_ink_including_the_border()
    {
        using var ws = new TempWorkspace();
        using PdfDocument doc = Make(ws, "page");
        var renderer = new PageRenderer();

        int before = CountNonWhite(renderer.Render(doc, 0, 300, 388));
        doc.AddAnnotation(0, PdfAnnotation.NewTextBox(new PdfRect(60, 700, 400, 620), "Hello box", Blue, 12, "rob"));
        int after = doc.WithAnnotationsBaked(() => CountNonWhite(renderer.Render(doc, 0, 300, 388)));

        Assert.True(after > before + 50, "The baked text box should add a visible border + text.");
    }

    [Fact]
    public void Callout_round_trips_with_its_leader_line()
    {
        using var ws = new TempWorkspace();
        byte[] saved;

        using (PdfDocument doc = Make(ws, "one"))
        {
            var box = new PdfRect(200, 400, 380, 350);
            IReadOnlyList<PdfPoint> leader = [new PdfPoint(120, 300), new PdfPoint(200, 375)];
            doc.AddAnnotation(0, PdfAnnotation.NewCallout(box, leader, "Check this dimension", 0xFFF06292u, 11, "rob"));
            saved = doc.SaveToBytes();
        }

        using PdfDocument reloaded = PdfDocument.Load(saved);
        PdfAnnotation a = Assert.Single(reloaded.GetAnnotations(0));
        Assert.Equal(PdfAnnotationKind.Callout, a.Kind);
        Assert.Equal("Check this dimension", a.Contents);
        Assert.Equal(2, a.Leader.Count);
        Assert.Equal(120, a.Leader[0].X, 1);
        Assert.Equal(300, a.Leader[0].Y, 1);
    }

    [Fact]
    public void Revision_cloud_round_trips_and_renders()
    {
        using var ws = new TempWorkspace();
        using PdfDocument doc = Make(ws, "page");
        var renderer = new PageRenderer();

        int before = CountNonWhite(renderer.Render(doc, 0, 300, 388));
        doc.AddAnnotation(0, PdfAnnotation.NewCloud(new PdfRect(80, 700, 420, 560), 0xFFF06292u, "rob"));
        byte[] saved = doc.SaveToBytes();
        int after = doc.WithAnnotationsBaked(() => CountNonWhite(renderer.Render(doc, 0, 300, 388)));
        Assert.True(after > before + 30, "The baked cloud should add a visible scalloped outline.");

        using PdfDocument reloaded = PdfDocument.Load(saved);
        PdfAnnotation a = Assert.Single(reloaded.GetAnnotations(0));
        Assert.Equal(PdfAnnotationKind.Cloud, a.Kind);
        Assert.Equal(80, a.Box.Left, 1);
        Assert.Equal(420, a.Box.Right, 1);
    }

    [Fact]
    public void Text_box_is_undoable_and_kept_off_the_live_handle()
    {
        using var ws = new TempWorkspace();
        using PdfDocument doc = Make(ws, "page");

        doc.AddAnnotation(0, PdfAnnotation.NewTextBox(new PdfRect(50, 400, 250, 350), "note", Blue, 11, "rob"));
        Assert.Single(doc.GetAnnotations(0));
        Assert.Empty(PdfAnnotations.Read(doc, 0)); // live handle stays overlay-only

        doc.Undo();
        Assert.Empty(doc.GetAnnotations(0));
    }

    private static int CountNonWhite(RenderedPage page)
    {
        int n = 0;
        for (int i = 0; i + 3 < page.Pixels.Length; i += 4)
        {
            if (page.Pixels[i] != 0xFF || page.Pixels[i + 1] != 0xFF || page.Pixels[i + 2] != 0xFF)
            {
                n++;
            }
        }

        return n;
    }
}
