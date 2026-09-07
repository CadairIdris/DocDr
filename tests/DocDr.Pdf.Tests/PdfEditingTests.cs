namespace DocDr.Pdf.Tests;

public sealed class PdfEditingTests
{
    private static PdfDocument Make(TempWorkspace ws, string name, params string[] pageLines) =>
        PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path(name), pageLines));

    [Fact]
    public void Rotate_swaps_page_size_and_undoes()
    {
        using var ws = new TempWorkspace();
        using var doc = Make(ws, "r.pdf", "one", "two", "three");
        PdfSize before = doc.GetPageSize(1);

        doc.RotatePages([1], PdfRotation.Clockwise90);

        Assert.Equal(PdfRotation.Clockwise90, doc.GetPageRotation(1));
        PdfSize rotated = doc.GetPageSize(1);
        Assert.Equal(before.Height, rotated.Width, precision: 1);
        Assert.Equal(before.Width, rotated.Height, precision: 1);
        Assert.True(doc.IsDirty);

        doc.Undo();
        Assert.Equal(PdfRotation.None, doc.GetPageRotation(1));
        Assert.Equal(before.Width, doc.GetPageSize(1).Width, precision: 1);
    }

    [Fact]
    public void Delete_removes_pages_keeps_order_and_round_trips_through_undo_redo()
    {
        using var ws = new TempWorkspace();
        using var doc = Make(ws, "d.pdf", "P1", "P2", "P3", "P4", "P5");

        doc.DeletePages([1, 3]); // remove P2 and P4

        Assert.Equal(3, doc.PageCount);
        Assert.Equal("P1", PageText(doc, 0));
        Assert.Equal("P3", PageText(doc, 1));
        Assert.Equal("P5", PageText(doc, 2));

        doc.Undo();
        Assert.Equal(5, doc.PageCount);
        Assert.Equal(["P1", "P2", "P3", "P4", "P5"],
            Enumerable.Range(0, 5).Select(i => PageText(doc, i)).ToArray());

        doc.Redo();
        Assert.Equal(3, doc.PageCount);
        Assert.Equal("P3", PageText(doc, 1));
    }

    [Fact]
    public void Insert_adds_source_pages_at_position_and_undoes()
    {
        using var ws = new TempWorkspace();
        using var doc = Make(ws, "host.pdf", "H1", "H2", "H3", "H4", "H5");
        using var source = Make(ws, "src.pdf", "S1", "S2", "S3");

        doc.InsertPages(source, atIndex: 2);

        Assert.Equal(8, doc.PageCount);
        Assert.Equal("H2", PageText(doc, 1));
        Assert.Equal("S1", PageText(doc, 2));
        Assert.Equal("S3", PageText(doc, 4));
        Assert.Equal("H3", PageText(doc, 5));

        doc.Undo();
        Assert.Equal(5, doc.PageCount);
        Assert.Equal("H3", PageText(doc, 2));
    }

    [Fact]
    public void Insert_blank_page_matches_the_previous_page_size_and_round_trips()
    {
        using var ws = new TempWorkspace();
        byte[] saved;

        using (var doc = Make(ws, "h.pdf", "one", "two", "three"))
        {
            PdfSize refSize = doc.GetPageSize(1);

            doc.InsertBlankPage(afterIndex: 1);

            Assert.Equal(4, doc.PageCount);
            Assert.Equal("two", PageText(doc, 1));
            Assert.Equal("", PageText(doc, 2).Trim());          // the new blank page
            Assert.Equal("three", PageText(doc, 3));
            Assert.Equal(refSize.Width, doc.GetPageSize(2).Width, precision: 1);
            Assert.Equal(refSize.Height, doc.GetPageSize(2).Height, precision: 1);
            Assert.True(doc.IsDirty);

            doc.Undo();
            Assert.Equal(3, doc.PageCount);
            Assert.Equal("three", PageText(doc, 2));

            doc.Redo();
            Assert.Equal(4, doc.PageCount);
            saved = doc.SaveToBytes();
        }

        using var reloaded = PdfDocument.Load(saved);
        Assert.Equal(4, reloaded.PageCount);
        Assert.Equal("", PageText(reloaded, 2).Trim());
    }

    [Fact]
    public void Insert_blank_page_at_the_front_uses_the_first_page_size()
    {
        using var ws = new TempWorkspace();
        using var doc = Make(ws, "h.pdf", "one", "two");
        PdfSize first = doc.GetPageSize(0);

        doc.InsertBlankPage(afterIndex: -1);

        Assert.Equal(3, doc.PageCount);
        Assert.Equal("", PageText(doc, 0).Trim());
        Assert.Equal("one", PageText(doc, 1));
        Assert.Equal(first.Width, doc.GetPageSize(0).Width, precision: 1);
    }

    [Fact]
    public void History_is_bounded_and_new_edit_clears_redo()
    {
        using var ws = new TempWorkspace();
        using var doc = Make(ws, "h.pdf", "a", "b", "c", "d");

        doc.RotatePages([0], PdfRotation.Clockwise90);
        doc.RotatePages([1], PdfRotation.Clockwise90);
        doc.DeletePages([3]);

        Assert.True(doc.CanUndo);
        doc.Undo();
        doc.Undo();
        doc.Undo();
        Assert.False(doc.CanUndo);
        Assert.True(doc.CanRedo);
        Assert.Equal(4, doc.PageCount);
        Assert.Equal(PdfRotation.None, doc.GetPageRotation(0));

        doc.Redo();
        doc.RotatePages([2], PdfRotation.Clockwise90); // new edit wipes the redo stack
        Assert.False(doc.CanRedo);
    }

    [Fact]
    public void Edits_persist_through_save_and_reload()
    {
        using var ws = new TempWorkspace();
        byte[] saved;
        PdfSize rotatedExpected;

        using (var doc = Make(ws, "s.pdf", "P1", "P2", "P3", "P4"))
        {
            doc.RotatePages([0], PdfRotation.Clockwise90);
            doc.DeletePages([2]); // remove P3
            rotatedExpected = doc.GetPageSize(0);
            saved = doc.SaveToBytes();
        }

        using var reloaded = PdfDocument.Load(saved);
        Assert.Equal(3, reloaded.PageCount);
        Assert.Equal("P2", PageText(reloaded, 1));
        Assert.Equal("P4", PageText(reloaded, 2));
        Assert.Equal(PdfRotation.Clockwise90, reloaded.GetPageRotation(0));
        Assert.Equal(rotatedExpected.Width, reloaded.GetPageSize(0).Width, precision: 1);
    }

    [Fact]
    public async Task Editing_while_rendering_the_same_document_does_not_corrupt()
    {
        using var ws = new TempWorkspace();
        using var doc = Make(ws, "c.pdf",
            Enumerable.Range(1, 20).Select(i => $"Page {i} lorem ipsum").ToArray());
        var renderer = new PageRenderer();

        var render = Task.Run(() =>
        {
            for (int i = 0; i < 120; i++)
            {
                int page = i % Math.Max(1, doc.PageCount);
                _ = renderer.Render(doc, page, 160, 207);
            }
        });

        var edit = Task.Run(() =>
        {
            for (int i = 0; i < 15; i++)
            {
                doc.RotatePages([0], PdfRotation.Clockwise90);
                doc.Undo();
            }
        });

        await Task.WhenAll(render, edit).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(20, doc.PageCount);
    }

    private static string PageText(PdfDocument doc, int pageIndex) =>
        PdfTextExtractor.GetPageText(doc, pageIndex).Trim();
}
