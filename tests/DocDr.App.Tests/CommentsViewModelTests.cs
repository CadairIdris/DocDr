using DocDr.App.ViewModels;
using DocDr.Pdf;

namespace DocDr.App.Tests;

public sealed class CommentsViewModelTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly PdfDocument _doc;

    public CommentsViewModelTests() =>
        _doc = PdfDocument.Load(TestPdfBuilder.WritePdf(_dir.File("c.pdf"), ["one", "two", "three"]));

    public void Dispose()
    {
        _doc.Dispose();
        _dir.Dispose();
    }

    private static PdfRect Pin(double x) => new(x, 700, x + 18, 682);

    private CommentsViewModel Load()
    {
        var vm = new CommentsViewModel("tester");
        vm.Reload(_doc, i => (i + 1).ToString());
        // Wire the panel to the document the way DocumentTabViewModel does.
        vm.UpdateRequested += (page, a) => { _doc.UpdateAnnotation(page, a); vm.Reload(_doc, i => (i + 1).ToString()); };
        return vm;
    }

    [Fact]
    public void Notes_and_noted_highlights_are_comments_markup_is_not()
    {
        Assert.True(CommentClassifier.IsComment(PdfAnnotation.NewComment(Pin(72), "n")));
        Assert.True(CommentClassifier.IsComment(PdfAnnotation.NewHighlight([Pin(72)], 0xFFFFD54Fu, "why?")));
        Assert.False(CommentClassifier.IsComment(PdfAnnotation.NewHighlight([Pin(72)], 0xFFFFD54Fu)));
        Assert.False(CommentClassifier.IsComment(PdfAnnotation.NewRectangle(Pin(72), 0xFF2244AA)));
    }

    [Fact]
    public void Comments_are_grouped_by_page_and_markup_stays_in_the_annotations_list()
    {
        _doc.AddAnnotation(0, PdfAnnotation.NewComment(Pin(72), "first"));
        _doc.AddAnnotation(2, PdfAnnotation.NewComment(Pin(72), "second"));
        _doc.AddAnnotation(2, PdfAnnotation.NewComment(Pin(120), "third"));
        _doc.AddAnnotation(2, PdfAnnotation.NewRectangle(Pin(200), 0xFF2244AA));

        CommentsViewModel vm = Load();
        Assert.Equal(3, vm.Count);
        Assert.Equal(["Page 1", "Page 3"], vm.Groups.Select(g => g.Header).ToArray());
        Assert.Equal(2, vm.Groups[1].Count);

        var markup = new AnnotationListViewModel();
        markup.Reload(_doc);
        Assert.Equal("Rectangle", Assert.Single(markup.Rows).Kind);
    }

    [Fact]
    public void Posting_a_reply_appends_it_and_keeps_the_card_selected()
    {
        _doc.AddAnnotation(1, PdfAnnotation.NewComment(Pin(72), "Is this current?", "rob"));
        CommentsViewModel vm = Load();
        CommentCardViewModel card = vm.Cards.Single();
        vm.Select(card.Id);

        card.ReplyDraft = "  Superseded in 2023.  ";
        card.PostReplyCommand.Execute(null);

        PdfReply reply = Assert.Single(_doc.GetAnnotations(1)[0].Replies);
        Assert.Equal("Superseded in 2023.", reply.Text);
        Assert.Equal("tester", reply.Author);

        CommentCardViewModel reloaded = vm.Cards.Single();
        Assert.True(reloaded.IsSelected);
        Assert.Equal(string.Empty, reloaded.ReplyDraft);
        Assert.Single(reloaded.Replies);
    }

    [Fact]
    public void An_empty_reply_cannot_be_posted()
    {
        _doc.AddAnnotation(0, PdfAnnotation.NewComment(Pin(72), "x"));
        CommentCardViewModel card = Load().Cards.Single();

        card.ReplyDraft = "   ";
        Assert.False(card.PostReplyCommand.CanExecute(null));
    }

    [Fact]
    public void Status_and_checkmark_are_applied_to_the_document()
    {
        _doc.AddAnnotation(0, PdfAnnotation.NewComment(Pin(72), "x"));
        CommentsViewModel vm = Load();

        vm.Cards.Single().SetStatusCommand.Execute(PdfReviewStatus.Completed);
        vm.Cards.Single().ToggleMarkCommand.Execute(null);

        PdfAnnotation a = _doc.GetAnnotations(0)[0];
        Assert.Equal(PdfReviewStatus.Completed, a.Status);
        Assert.True(a.IsMarked);
        Assert.Equal("Completed", vm.Cards.Single().StatusLabel);
    }

    [Fact]
    public void A_half_written_reply_survives_a_reload()
    {
        _doc.AddAnnotation(0, PdfAnnotation.NewComment(Pin(72), "a"));
        _doc.AddAnnotation(0, PdfAnnotation.NewComment(Pin(120), "b"));
        CommentsViewModel vm = Load();
        CommentCardViewModel drafting = vm.Cards.First();
        drafting.ReplyDraft = "working on it";

        // Another comment changes (its own reply) → the whole panel reloads.
        vm.Cards.Last().SetStatusCommand.Execute(PdfReviewStatus.Accepted);

        Assert.Equal("working on it", vm.Cards.Single(c => c.Id == drafting.Id).ReplyDraft);
    }
}
