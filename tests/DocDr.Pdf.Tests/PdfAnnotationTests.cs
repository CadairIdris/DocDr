namespace DocDr.Pdf.Tests;

public sealed class PdfAnnotationTests
{
    private const uint Yellow = 0xFFFFEB3Bu;
    private const uint Green = 0xFF66BB6Au;

    private static PdfDocument Make(TempWorkspace ws, string name, params string[] pageLines) =>
        PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path(name), pageLines));

    /// <summary>Page-space rectangles of the first occurrence of <paramref name="term"/> on a page.</summary>
    private static IReadOnlyList<PdfRect> RectsFor(PdfDocument doc, int pageIndex, string term)
    {
        SearchHit hit = new PdfSearch(doc).FindAll(term).First(h => h.PageIndex == pageIndex);
        return hit.Rects;
    }

    [Fact]
    public void Highlight_round_trips_through_save_and_reload()
    {
        using var ws = new TempWorkspace();
        byte[] saved;

        using (PdfDocument doc = Make(ws, "h.pdf", "alpha beta", "gamma delta epsilon", "zeta"))
        {
            IReadOnlyList<PdfRect> quads = RectsFor(doc, 1, "delta");
            doc.AddAnnotation(1, PdfAnnotation.NewHighlight(quads, Yellow, note: null, author: "rob"));
            Assert.True(doc.IsDirty);
            saved = doc.SaveToBytes();
        }

        using PdfDocument reloaded = PdfDocument.Load(saved);
        Assert.Empty(reloaded.GetAnnotations(0));
        Assert.Empty(reloaded.GetAnnotations(2));

        PdfAnnotation a = Assert.Single(reloaded.GetAnnotations(1));
        Assert.Equal(PdfAnnotationKind.Highlight, a.Kind);
        Assert.NotEmpty(a.Quads);
        AssertChannelsClose(Yellow, a.ColorArgb);
        Assert.Equal("rob", a.Author);

        // A standard-format highlight is what other readers need to see.
        string raw = System.Text.Encoding.Latin1.GetString(saved);
        Assert.Contains("/Highlight", raw);
        Assert.Contains("/QuadPoints", raw);
    }

    [Fact]
    public void Comment_round_trips_with_its_text_and_author()
    {
        using var ws = new TempWorkspace();
        byte[] saved;

        using (PdfDocument doc = Make(ws, "c.pdf", "page one text", "page two text"))
        {
            var rect = new PdfRect(72, 700, 96, 676);
            doc.AddAnnotation(0, PdfAnnotation.NewComment(rect, "see figure 2", author: "rob"));
            saved = doc.SaveToBytes();
        }

        using PdfDocument reloaded = PdfDocument.Load(saved);
        PdfAnnotation a = Assert.Single(reloaded.GetAnnotations(0));
        Assert.Equal(PdfAnnotationKind.Comment, a.Kind);
        Assert.Equal("see figure 2", a.Contents);
        Assert.Equal("rob", a.Author);
        Assert.NotNull(a.Created);
        Assert.NotNull(a.Modified);
        Assert.True((System.DateTimeOffset.Now - a.Created!.Value).Duration() < System.TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Editing_keeps_the_creation_date_and_advances_the_modified_date()
    {
        using var ws = new TempWorkspace();
        using PdfDocument doc = Make(ws, "dates.pdf", "alpha beta gamma");

        PdfAnnotation created = PdfAnnotation.NewComment(new PdfRect(72, 700, 90, 682), "first", "rob")
            with { Created = new System.DateTimeOffset(2020, 1, 2, 3, 4, 5, System.TimeSpan.Zero) };
        doc.AddAnnotation(0, created);

        doc.UpdateAnnotation(0, doc.GetAnnotations(0)[0] with { Contents = "edited", Modified = System.DateTimeOffset.Now });

        using PdfDocument reloaded = PdfDocument.Load(doc.SaveToBytes());
        PdfAnnotation a = Assert.Single(reloaded.GetAnnotations(0));
        Assert.Equal(2020, a.Created!.Value.Year);
        Assert.Equal(System.DateTime.Now.Year, a.Modified!.Value.Year);
    }

    [Fact]
    public void Update_and_remove_are_reflected_after_reload()
    {
        using var ws = new TempWorkspace();

        using PdfDocument doc = Make(ws, "u.pdf", "one two three");
        PdfAnnotation created = PdfAnnotation.NewHighlight(RectsFor(doc, 0, "two"), Yellow);
        doc.AddAnnotation(0, created);

        PdfAnnotation edited = created with { ColorArgb = Green, Contents = "revised" };
        doc.UpdateAnnotation(0, edited);

        using (PdfDocument afterEdit = PdfDocument.Load(doc.SaveToBytes()))
        {
            PdfAnnotation a = Assert.Single(afterEdit.GetAnnotations(0));
            AssertChannelsClose(Green, a.ColorArgb);
            Assert.Equal("revised", a.Contents);
        }

        doc.RemoveAnnotation(0, created.Id);
        using PdfDocument afterRemove = PdfDocument.Load(doc.SaveToBytes());
        Assert.Empty(afterRemove.GetAnnotations(0));
    }

    [Fact]
    public void Add_is_undoable_and_redoable()
    {
        using var ws = new TempWorkspace();
        using PdfDocument doc = Make(ws, "z.pdf", "hello world");

        doc.AddAnnotation(0, PdfAnnotation.NewHighlight(RectsFor(doc, 0, "world"), Yellow));
        Assert.Single(doc.GetAnnotations(0));

        doc.Undo();
        Assert.Empty(doc.GetAnnotations(0));

        doc.Redo();
        Assert.Single(doc.GetAnnotations(0));
    }

    [Fact]
    public void Annotation_stays_on_its_page_when_an_earlier_page_is_rotated()
    {
        using var ws = new TempWorkspace();
        using PdfDocument doc = Make(ws, "rot.pdf", "first", "second", "third target here");

        IReadOnlyList<PdfRect> quads = RectsFor(doc, 2, "target");
        doc.AddAnnotation(2, PdfAnnotation.NewHighlight(quads, Yellow));

        doc.RotatePages([0], PdfRotation.Clockwise90);

        using PdfDocument reloaded = PdfDocument.Load(doc.SaveToBytes());
        Assert.Empty(reloaded.GetAnnotations(0));
        Assert.Empty(reloaded.GetAnnotations(1));
        PdfAnnotation a = Assert.Single(reloaded.GetAnnotations(2));
        Assert.Equal(quads.Count, a.Quads.Count);
    }

    [Fact]
    public void Annotation_follows_its_page_when_an_earlier_page_is_deleted()
    {
        using var ws = new TempWorkspace();
        using PdfDocument doc = Make(ws, "del.pdf", "p1", "p2", "p3 marker word", "p4");

        doc.AddAnnotation(2, PdfAnnotation.NewHighlight(RectsFor(doc, 2, "marker"), Yellow));
        doc.DeletePages([0]);

        Assert.Empty(doc.GetAnnotations(0)); // was p2
        Assert.Single(doc.GetAnnotations(1)); // was p3

        using PdfDocument reloaded = PdfDocument.Load(doc.SaveToBytes());
        Assert.Single(reloaded.GetAnnotations(1));
        Assert.Contains("marker", PdfTextExtractor.GetPageText(reloaded, 1));
    }

    [Fact]
    public void Annotation_shifts_when_pages_are_inserted_before_it()
    {
        using var ws = new TempWorkspace();
        using PdfDocument doc = Make(ws, "ins.pdf", "host one", "host two flagged");
        using PdfDocument extra = Make(ws, "extra.pdf", "x1", "x2", "x3");

        doc.AddAnnotation(1, PdfAnnotation.NewHighlight(RectsFor(doc, 1, "flagged"), Yellow));
        doc.InsertPages(extra, atIndex: 0);

        Assert.Empty(doc.GetAnnotations(1));
        Assert.Single(doc.GetAnnotations(4));
    }

    [Fact]
    public void Saving_twice_does_not_duplicate_annotations()
    {
        using var ws = new TempWorkspace();
        using PdfDocument doc = Make(ws, "twice.pdf", "repeat word here");

        doc.AddAnnotation(0, PdfAnnotation.NewHighlight(RectsFor(doc, 0, "word"), Yellow));

        _ = doc.SaveToBytes();
        byte[] second = doc.SaveToBytes();

        using PdfDocument reloaded = PdfDocument.Load(second);
        Assert.Single(reloaded.GetAnnotations(0));
    }

    [Fact]
    public void Existing_file_annotations_are_read_and_can_be_deleted()
    {
        using var ws = new TempWorkspace();
        byte[] withHighlight;

        using (PdfDocument doc = Make(ws, "pre.pdf", "keep this highlighted text"))
        {
            doc.AddAnnotation(0, PdfAnnotation.NewHighlight(RectsFor(doc, 0, "highlighted"), Yellow, "a note"));
            withHighlight = doc.SaveToBytes();
        }

        // Fresh load treats it as a pre-existing annotation from "another tool".
        using PdfDocument opened = PdfDocument.Load(withHighlight);
        PdfAnnotation existing = Assert.Single(opened.GetAnnotations(0));
        Assert.Equal("a note", existing.Contents);

        opened.RemoveAnnotation(0, existing.Id);
        using PdfDocument cleared = PdfDocument.Load(opened.SaveToBytes());
        Assert.Empty(cleared.GetAnnotations(0));
    }

    [Fact]
    public void Saved_comment_is_a_standard_text_annotation()
    {
        using var ws = new TempWorkspace();
        using PdfDocument doc = Make(ws, "std.pdf", "anchor text");
        doc.AddAnnotation(0, PdfAnnotation.NewComment(new PdfRect(72, 700, 90, 682), "hello", "rob"));

        string raw = System.Text.Encoding.Latin1.GetString(doc.SaveToBytes());
        Assert.Contains("/Text", raw);
        Assert.Contains("/Contents", raw);
    }

    [Fact]
    public void Char_boxes_land_where_the_text_was_drawn()
    {
        using var ws = new TempWorkspace();
        using PdfDocument doc = Make(ws, "boxes.pdf", "Boxed");

        IReadOnlyList<PdfCharBox> boxes = PdfTextExtractor.GetCharBoxes(doc, 0);
        Assert.Equal(5, boxes.Count);

        PdfCharBox first = boxes[0];
        Assert.InRange(first.Box.Left, 60, 90);              // drawn at x=72
        Assert.InRange(first.Box.Bottom, TestPdfBuilder.PageHeight - 110, TestPdfBuilder.PageHeight - 80);
    }

    private static void AssertChannelsClose(uint expected, uint actual)
    {
        for (int shift = 0; shift <= 16; shift += 8)
        {
            int e = (int)((expected >> shift) & 0xFF);
            int a = (int)((actual >> shift) & 0xFF);
            Assert.True(Math.Abs(e - a) <= 3, $"channel @{shift}: expected {e}, got {a}");
        }
    }
}
