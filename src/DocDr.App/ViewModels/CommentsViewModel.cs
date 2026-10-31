using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.App.Services;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>Which annotations are "comments" (a conversation) rather than markup.</summary>
public static class CommentClassifier
{
    /// <summary>A note, or a highlight that carries a note or replies. Everything else (ink,
    /// shapes, text boxes, clouds, images, plain highlights) is markup and lives in the
    /// Annotations panel — each annotation appears in exactly one of the two.</summary>
    public static bool IsComment(PdfAnnotation annotation) =>
        annotation.Kind == PdfAnnotationKind.Comment
        || (annotation.Kind == PdfAnnotationKind.Highlight
            && (!string.IsNullOrWhiteSpace(annotation.Contents) || annotation.Replies.Count > 0));
}

/// <summary>One posted reply under a comment card.</summary>
public sealed class CommentReplyViewModel(PdfReply reply)
{
    public string Author { get; } = string.IsNullOrWhiteSpace(reply.Author) ? "—" : reply.Author.Trim();

    public string When { get; } = reply.Created.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    public string Text { get; } = reply.Text;
}

/// <summary>
/// One comment in the Comments panel: the opening note, its reply thread, a reply box, and the
/// review status / checkmark. Every change is handed to <see cref="CommentsViewModel"/> as a new
/// <see cref="PdfAnnotation"/> record, which the tab applies through
/// <see cref="PdfDocument.UpdateAnnotation"/> — so replies, status and ticks are all undoable.
/// </summary>
public sealed partial class CommentCardViewModel : ObservableObject
{
    private readonly CommentsViewModel _owner;

    public CommentCardViewModel(CommentsViewModel owner, int pageIndex, PdfAnnotation annotation)
    {
        _owner = owner;
        PageIndex = pageIndex;
        Annotation = annotation;
        Replies = new ObservableCollection<CommentReplyViewModel>(annotation.Replies.Select(r => new CommentReplyViewModel(r)));
    }

    public PdfAnnotation Annotation { get; }

    public Guid Id => Annotation.Id;

    public int PageIndex { get; }

    public bool IsNote => Annotation.Kind == PdfAnnotationKind.Comment;

    public bool IsHighlight => Annotation.Kind == PdfAnnotationKind.Highlight;

    public Brush Swatch => new SolidColorBrush(AnnotationColors.ToColor(Annotation.ColorArgb));

    public string Author => string.IsNullOrWhiteSpace(Annotation.Author) ? "—" : Annotation.Author.Trim();

    public string When => (Annotation.Created ?? Annotation.Modified) is { } d
        ? d.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
        : string.Empty;

    public string Text => Annotation.Contents?.Trim() ?? string.Empty;

    public bool HasText => Text.Length > 0;

    /// <summary>Shown in place of the text for a highlight that has replies but no note of its own.</summary>
    public string Placeholder => IsHighlight ? "Highlight" : "Note";

    public PdfReviewStatus Status => Annotation.Status;

    public bool HasStatus => Status != PdfReviewStatus.None;

    public string StatusLabel => Status.ToString();

    public bool IsMarked => Annotation.IsMarked;

    public ObservableCollection<CommentReplyViewModel> Replies { get; }

    public bool HasReplies => Replies.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReplyBox))]
    private bool _isSelected;

    /// <summary>The reply box shows on the selected card (like a chat thread you've opened).</summary>
    public bool ShowReplyBox => IsSelected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PostReplyCommand))]
    private string _replyDraft = string.Empty;

    /// <summary>Card clicked: select it and jump the page to the annotation.</summary>
    [RelayCommand]
    private void Select() => _owner.Activate(this);

    [RelayCommand(CanExecute = nameof(CanPostReply))]
    private void PostReply()
    {
        string text = ReplyDraft.Trim();
        if (text.Length == 0)
        {
            return;
        }

        ReplyDraft = string.Empty;
        _owner.Apply(this, Annotation with
        {
            Replies = [.. Annotation.Replies, PdfReply.New(text, _owner.ReplyAuthor)],
            Modified = DateTimeOffset.Now,
        });
    }

    private bool CanPostReply() => !string.IsNullOrWhiteSpace(ReplyDraft);

    [RelayCommand]
    private void ToggleMark() => _owner.Apply(this, Annotation with { IsMarked = !Annotation.IsMarked });

    [RelayCommand]
    private void SetStatus(PdfReviewStatus status)
    {
        if (status != Annotation.Status)
        {
            _owner.Apply(this, Annotation with { Status = status });
        }
    }

    [RelayCommand]
    private void CopyText()
    {
        string all = string.Join(Environment.NewLine + Environment.NewLine,
            new[] { Text }.Concat(Annotation.Replies.Select(r => $"{r.Author}: {r.Text}"))
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (all.Length > 0)
        {
            Citations.CopyToClipboard(all);
        }
    }

    [RelayCommand]
    private void Edit() => _owner.RequestEdit(this);

    [RelayCommand]
    private void Delete() => _owner.RequestDelete(this);
}

/// <summary>The comments on one page, under a collapsible "Page n" header.</summary>
public sealed partial class CommentPageGroupViewModel(int pageIndex, string pageLabel) : ObservableObject
{
    public int PageIndex { get; } = pageIndex;

    public string Header { get; } = $"Page {pageLabel}";

    public ObservableCollection<CommentCardViewModel> Cards { get; } = [];

    public int Count => Cards.Count;

    [ObservableProperty]
    private bool _isExpanded = true;
}

/// <summary>
/// The Comments navigation panel: every note / noted highlight, grouped by page, each with its
/// reply thread, a reply box and a review status. Split from the Annotations panel (markup).
/// </summary>
public sealed partial class CommentsViewModel : ObservableObject
{
    private Guid? _selectedId;
    private readonly Dictionary<Guid, string> _drafts = [];
    private readonly HashSet<int> _collapsedPages = [];

    public CommentsViewModel(string replyAuthor)
    {
        ReplyAuthor = string.IsNullOrWhiteSpace(replyAuthor) ? Environment.UserName : replyAuthor;
    }

    /// <summary>Author stamped on replies posted from this panel.</summary>
    public string ReplyAuthor { get; }

    public ObservableCollection<CommentPageGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComments))]
    private int _count;

    public bool HasComments => Count > 0;

    /// <summary>A card was chosen: (page index, annotation id) — select it on the page and go there.</summary>
    public event Action<int, Guid>? CommentActivated;

    /// <summary>Apply an edited record (reply, status, checkmark) to the document.</summary>
    public event Action<int, PdfAnnotation>? UpdateRequested;

    /// <summary>Open the note editor for (page, id).</summary>
    public event Action<int, Guid>? EditRequested;

    public event Action<int, Guid>? DeleteRequested;

    public IEnumerable<CommentCardViewModel> Cards => Groups.SelectMany(g => g.Cards);

    /// <summary>Rebuild from the document, keeping the selected card, half-written replies and
    /// collapsed page groups — the panel is reloaded after every annotation edit, including the
    /// reply the user just posted.</summary>
    public void Reload(PdfDocument document, Func<int, string> pageLabel)
    {
        foreach (CommentCardViewModel card in Cards)
        {
            if (!string.IsNullOrEmpty(card.ReplyDraft))
            {
                _drafts[card.Id] = card.ReplyDraft;
            }
            else
            {
                _drafts.Remove(card.Id);
            }
        }

        foreach (CommentPageGroupViewModel group in Groups)
        {
            if (group.IsExpanded)
            {
                _collapsedPages.Remove(group.PageIndex);
            }
            else
            {
                _collapsedPages.Add(group.PageIndex);
            }
        }

        Groups.Clear();
        int count = 0;
        for (int page = 0; page < document.PageCount; page++)
        {
            CommentPageGroupViewModel? group = null;
            foreach (PdfAnnotation annotation in document.GetAnnotations(page))
            {
                if (!CommentClassifier.IsComment(annotation))
                {
                    continue;
                }

                group ??= new CommentPageGroupViewModel(page, pageLabel(page))
                {
                    IsExpanded = !_collapsedPages.Contains(page),
                };
                var card = new CommentCardViewModel(this, page, annotation)
                {
                    IsSelected = annotation.Id == _selectedId,
                    ReplyDraft = _drafts.TryGetValue(annotation.Id, out string? draft) ? draft : string.Empty,
                };
                group.Cards.Add(card);
                count++;
            }

            if (group is not null)
            {
                Groups.Add(group);
            }
        }

        Count = count;
    }

    /// <summary>Reflect a selection made on the page (no navigation). Null clears it.</summary>
    public void Select(Guid? id)
    {
        _selectedId = id;
        foreach (CommentCardViewModel card in Cards)
        {
            card.IsSelected = card.Id == id;
        }

        if (id is { } selected && Groups.FirstOrDefault(g => g.Cards.Any(c => c.Id == selected)) is { } group)
        {
            group.IsExpanded = true;
        }
    }

    internal void Activate(CommentCardViewModel card)
    {
        Select(card.Id);
        CommentActivated?.Invoke(card.PageIndex, card.Id);
    }

    internal void Apply(CommentCardViewModel card, PdfAnnotation updated)
    {
        Select(card.Id);
        UpdateRequested?.Invoke(card.PageIndex, updated);
    }

    internal void RequestEdit(CommentCardViewModel card) => EditRequested?.Invoke(card.PageIndex, card.Id);

    internal void RequestDelete(CommentCardViewModel card)
    {
        if (_selectedId == card.Id)
        {
            _selectedId = null;
        }

        _drafts.Remove(card.Id);
        DeleteRequested?.Invoke(card.PageIndex, card.Id);
    }
}
