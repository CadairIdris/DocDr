using System.Collections.Generic;
using DocDr.App.ViewModels;
using DocDr.Pdf;

namespace DocDr.App.Tests;

/// <summary>Two panes kept in step: page pairing (with the offset they had when linked), the
/// relative spot on the page, zoom, and no echo back from the pane that was just moved.</summary>
public sealed class ScrollLinkTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly PdfDocument _left;
    private readonly PdfDocument _right;

    public ScrollLinkTests()
    {
        _left = PdfDocument.Load(TestPdfBuilder.WritePdf(_dir.File("l.pdf"), ["1", "2", "3", "4", "5", "6"]));
        _right = PdfDocument.Load(TestPdfBuilder.WritePdf(_dir.File("r.pdf"), ["1", "2", "3", "4", "5", "6", "7", "8"]));
    }

    public void Dispose()
    {
        _left.Dispose();
        _right.Dispose();
        _dir.Dispose();
    }

    private static PdfPaneViewModel Pane(PdfDocument doc) =>
        new("pane", doc, doc.GetPageSizes(), new FakeRenderQueue());

    private static List<ViewportPosition> Requests(PdfPaneViewModel pane)
    {
        var list = new List<ViewportPosition>();
        pane.ScrollToPositionRequested += list.Add;
        return list;
    }

    [StaFact]
    public void Scrolling_one_pane_puts_the_other_at_the_same_spot_on_the_paired_page()
    {
        PdfPaneViewModel a = Pane(_left), b = Pane(_right);
        using var link = new ScrollLink(a, b);
        List<ViewportPosition> toB = Requests(b);

        a.ReportViewportPosition(new ViewportPosition(3, 0.4, 0.25), userInitiated: true);

        Assert.Equal(new ViewportPosition(3, 0.4, 0.25), Assert.Single(toB));
    }

    [StaFact]
    public void The_page_offset_at_link_time_is_kept()
    {
        PdfPaneViewModel a = Pane(_left), b = Pane(_right);
        b.GoToPage(3); // right-hand doc has two extra front pages: pair left p1 with right p3
        using var link = new ScrollLink(a, b);
        List<ViewportPosition> toB = Requests(b), toA = Requests(a);

        Assert.Equal(2, link.PageOffset);

        a.ReportViewportPosition(new ViewportPosition(1, 0.5, 0), userInitiated: true);
        Assert.Equal(3, Assert.Single(toB).PageIndex);

        b.ReportViewportPosition(new ViewportPosition(6, 0.1, 0), userInitiated: true);
        Assert.Equal(4, Assert.Single(toA).PageIndex); // and back the other way
    }

    [StaFact]
    public void A_move_the_view_made_on_its_own_is_not_broadcast()
    {
        PdfPaneViewModel a = Pane(_left), b = Pane(_right);
        using var link = new ScrollLink(a, b);
        List<ViewportPosition> toB = Requests(b);

        a.ReportViewportPosition(new ViewportPosition(2, 0.3, 0), userInitiated: false);

        Assert.Empty(toB);
        Assert.Equal(new ViewportPosition(2, 0.3, 0), a.LastViewportPosition);
    }

    [StaFact]
    public void The_followers_echo_of_a_sync_is_not_bounced_back()
    {
        PdfPaneViewModel a = Pane(_left), b = Pane(_right);
        using var link = new ScrollLink(a, b);
        List<ViewportPosition> toA = Requests(a);

        a.ReportViewportPosition(new ViewportPosition(2, 0.3, 0), userInitiated: true);
        // b's view settles on (almost exactly) what it was asked for and reports it
        b.ReportViewportPosition(new ViewportPosition(2, 0.302, 0), userInitiated: true);

        Assert.Empty(toA);
    }

    [StaFact]
    public void Zooming_one_pane_zooms_the_other()
    {
        PdfPaneViewModel a = Pane(_left), b = Pane(_right);
        using var link = new ScrollLink(a, b);

        a.ZoomInCommand.Execute(null);

        Assert.Equal(a.Zoom, b.Zoom, precision: 6);
        Assert.Equal(ZoomMode.Custom, b.ZoomMode);
    }

    [StaFact]
    public void Clamps_to_the_shorter_document()
    {
        PdfPaneViewModel a = Pane(_right), b = Pane(_left); // a has 8 pages, b has 6
        using var link = new ScrollLink(a, b);
        List<ViewportPosition> toB = Requests(b);

        a.ReportViewportPosition(new ViewportPosition(7, 0.5, 0), userInitiated: true);

        Assert.Equal(5, Assert.Single(toB).PageIndex);
    }

    [StaFact]
    public void Disposing_the_link_stops_following()
    {
        PdfPaneViewModel a = Pane(_left), b = Pane(_right);
        var link = new ScrollLink(a, b);
        List<ViewportPosition> toB = Requests(b);
        link.Dispose();

        a.ReportViewportPosition(new ViewportPosition(3, 0.4, 0), userInitiated: true);

        Assert.Empty(toB);
    }
}
