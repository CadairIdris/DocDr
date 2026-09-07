namespace DocDr.Pdf.Tests;

public sealed class PdfReplyTests
{
    private const uint Amber = 0xFFFFD54Fu;

    private static PdfDocument Make(TempWorkspace ws, params string[] pages) =>
        PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("r.pdf"), pages));

    [Fact]
    public void Reply_thread_round_trips_through_save_and_reload()
    {
        using var ws = new TempWorkspace();
        byte[] saved;

        using (PdfDocument doc = Make(ws, "one", "two"))
        {
            var comment = PdfAnnotation.NewComment(new PdfRect(72, 700, 90, 682), "Is this clause current?", "rob")
                with
            {
                Replies =
                [
                    PdfReply.New("Superseded by the 2023 amendment.", "sam"),
                    PdfReply.New("Thanks — updating the ref.", "rob"),
                ],
            };
            doc.AddAnnotation(0, comment);
            saved = doc.SaveToBytes();
        }

        using PdfDocument reloaded = PdfDocument.Load(saved);
        PdfAnnotation a = Assert.Single(reloaded.GetAnnotations(0));
        Assert.Equal("Is this clause current?", a.Contents);
        Assert.Equal(2, a.Replies.Count);
        Assert.Equal("Superseded by the 2023 amendment.", a.Replies[0].Text);
        Assert.Equal("sam", a.Replies[0].Author);
        Assert.Equal("Thanks — updating the ref.", a.Replies[1].Text);
    }

    [Fact]
    public void Adding_a_reply_is_undoable()
    {
        using var ws = new TempWorkspace();
        using PdfDocument doc = Make(ws, "one");

        doc.AddAnnotation(0, PdfAnnotation.NewComment(new PdfRect(72, 700, 90, 682), "root", "rob"));
        PdfAnnotation created = doc.GetAnnotations(0)[0];

        doc.UpdateAnnotation(0, created with { Replies = [PdfReply.New("a reply", "sam")] });
        Assert.Single(doc.GetAnnotations(0)[0].Replies);

        doc.Undo();
        Assert.Empty(doc.GetAnnotations(0)[0].Replies);
    }

    [Fact]
    public void No_replies_writes_no_thread_key()
    {
        using var ws = new TempWorkspace();
        using PdfDocument doc = Make(ws, "one");
        doc.AddAnnotation(0, PdfAnnotation.NewComment(new PdfRect(72, 700, 90, 682), "just a note", "rob"));

        string raw = System.Text.Encoding.Latin1.GetString(doc.SaveToBytes());
        Assert.DoesNotContain("DocDrThread", raw);
    }
}
