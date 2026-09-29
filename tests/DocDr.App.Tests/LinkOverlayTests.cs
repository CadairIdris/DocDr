using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using DocDr.App.ViewModels;
using DocDr.Pdf;

namespace DocDr.App.Tests;

/// <summary>Link overlays are rebuilt on every scroll event, so an unchanged page must keep its
/// overlay object — replacing it makes WPF recreate every link button on the page, which is what
/// made link-heavy pages (contents pages) stutter while scrolling.</summary>
public sealed class LinkOverlayTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly PdfDocument _doc;

    public LinkOverlayTests() =>
        _doc = PdfDocument.Load(TestPdfBuilder.WritePdf(
            _dir.File("links.pdf"), ["contents", "two", "three"],
            links: [new TestPdfBuilder.Link(0, 1), new TestPdfBuilder.Link(0, 2)]));

    public void Dispose()
    {
        _doc.Dispose();
        _dir.Dispose();
    }

    /// <summary>Run the dispatcher (where the background link read posts its result) until
    /// <paramref name="done"/> holds.</summary>
    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(5);
        }

        Assert.True(done(), "timed out waiting for the background link read");
    }

    [StaFact]
    public void Scrolling_does_not_rebuild_an_unchanged_pages_link_overlay()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var pane = new PdfPaneViewModel("Left", _doc, _doc.GetPageSizes(), new FakeRenderQueue());
        PageSlotViewModel contents = pane.Pages[0];

        pane.UpdateVisibleRange(0, 0);
        PumpUntil(() => contents.Links.Count > 0);
        IReadOnlyList<LinkVisual> built = contents.Links;

        // What every ScrollChanged does:
        pane.UpdateVisibleRange(0, 0);
        pane.UpdateVisibleRange(0, 1);
        pane.BuildLinkOverlays();

        Assert.Same(built, contents.Links);

        // A zoom moves the link rects, so that one does rebuild.
        pane.ZoomInCommand.Execute(null);
        Assert.NotSame(built, contents.Links);
        Assert.Equal(built.Count, contents.Links.Count);
    }
}
