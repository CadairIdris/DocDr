using System.ComponentModel;

namespace DocDr.App.ViewModels;

/// <summary>
/// Keeps two panes (the documents docked side by side) scrolled — and zoomed — together.
/// <para>
/// Alignment is by page, not by pixel: the pages are paired with the offset they had when the
/// link was made (so if the right-hand document has two extra front-matter pages and you line
/// page 12 up with page 14 before linking, they stay paired), and the same relative spot on the
/// paired page is shown. That holds across different zooms and page sizes, which a raw scroll
/// offset wouldn't.
/// </para>
/// <para>
/// Echo control: a pane only broadcasts moves the user made (<see cref="PdfPaneViewModel.ViewportScrolled"/>
/// is never raised for a sync-driven scroll), and a report that matches what this link just
/// pushed to that pane is ignored anyway — so the two can't ping-pong while layout settles.
/// </para>
/// </summary>
public sealed class ScrollLink : IDisposable
{
    private readonly PdfPaneViewModel _a;
    private readonly PdfPaneViewModel _b;
    private readonly int _offset;
    private ViewportPosition? _pushedToA;
    private ViewportPosition? _pushedToB;
    private bool _applying;

    /// <summary>Link <paramref name="b"/> to follow <paramref name="a"/> (and vice versa). The page
    /// offset is taken from where the two are now, then <paramref name="b"/> is brought into line
    /// with <paramref name="a"/>'s zoom and position.</summary>
    public ScrollLink(PdfPaneViewModel a, PdfPaneViewModel b)
    {
        _a = a;
        _b = b;
        _offset = b.CurrentPage - a.CurrentPage;

        _a.ViewportScrolled += OnViewportScrolled;
        _b.ViewportScrolled += OnViewportScrolled;
        _a.PropertyChanged += OnPanePropertyChanged;
        _b.PropertyChanged += OnPanePropertyChanged;

        FollowZoom(_a); // also brings B to A's relative spot on the paired page
    }

    /// <summary>Pages of <see cref="B"/> ahead of the paired page of <see cref="A"/> (negative if behind).</summary>
    public int PageOffset => _offset;

    public PdfPaneViewModel A => _a;

    public PdfPaneViewModel B => _b;

    private PdfPaneViewModel Other(PdfPaneViewModel pane) => ReferenceEquals(pane, _a) ? _b : _a;

    private int MapPage(PdfPaneViewModel from, int pageIndex) =>
        ReferenceEquals(from, _a) ? pageIndex + _offset : pageIndex - _offset;

    /// <summary>Where <paramref name="pane"/> is: its last reported fine position if that's still
    /// on its current page, else the top of the current page (paged modes, or nothing reported yet).</summary>
    private static ViewportPosition Where(PdfPaneViewModel pane)
    {
        ViewportPosition last = pane.LastViewportPosition;
        return last.PageIndex == pane.CurrentPage - 1
            ? last
            : new ViewportPosition(pane.CurrentPage - 1, 0, last.HorizontalFraction);
    }

    private void OnViewportScrolled(PdfPaneViewModel pane, ViewportPosition position)
    {
        if (_applying)
        {
            return;
        }

        // Our own sync coming back round (the view settling on what we asked for) — not a user move.
        ViewportPosition? pushed = ReferenceEquals(pane, _a) ? _pushedToA : _pushedToB;
        if (pushed is { } p && p.IsNear(position))
        {
            return;
        }

        PdfPaneViewModel other = Other(pane);
        Push(other, position with { PageIndex = MapPage(pane, position.PageIndex) });
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applying || sender is not PdfPaneViewModel pane)
        {
            return;
        }

        switch (e.PropertyName)
        {
            // Paged / grid modes have no fine position; follow the page. (Continuous mode follows
            // ViewportScrolled instead — its CurrentPage also moves on every scroll.)
            case nameof(PdfPaneViewModel.CurrentPage) when pane.Mode != ViewMode.Continuous:
                PdfPaneViewModel other = Other(pane);
                int target = MapPage(pane, pane.CurrentPage - 1);
                if (target != other.CurrentPage - 1)
                {
                    Push(other, new ViewportPosition(target, 0, 0));
                }

                break;

            case nameof(PdfPaneViewModel.Zoom):
            case nameof(PdfPaneViewModel.ZoomMode):
                FollowZoom(pane);
                break;
        }
    }

    /// <summary>Give the other pane <paramref name="leader"/>'s zoom, then re-align it: page +
    /// fraction is zoom-independent, so the leader's last position still says where to be.</summary>
    private void FollowZoom(PdfPaneViewModel leader)
    {
        PdfPaneViewModel other = Other(leader);
        _applying = true;
        try
        {
            other.ApplyZoom(leader.Zoom, leader.ZoomMode);
        }
        finally
        {
            _applying = false;
        }

        ViewportPosition at = Where(leader);
        Push(other, at with { PageIndex = MapPage(leader, at.PageIndex) });
    }

    private void Push(PdfPaneViewModel target, ViewportPosition position)
    {
        if (target.PageCount == 0)
        {
            return;
        }

        position = position with { PageIndex = Math.Clamp(position.PageIndex, 0, target.PageCount - 1) };
        if (ReferenceEquals(target, _a))
        {
            _pushedToA = position;
        }
        else
        {
            _pushedToB = position;
        }

        _applying = true;
        try
        {
            target.SyncTo(position);
        }
        finally
        {
            _applying = false;
        }
    }

    public void Dispose()
    {
        _a.ViewportScrolled -= OnViewportScrolled;
        _b.ViewportScrolled -= OnViewportScrolled;
        _a.PropertyChanged -= OnPanePropertyChanged;
        _b.PropertyChanged -= OnPanePropertyChanged;
    }
}
