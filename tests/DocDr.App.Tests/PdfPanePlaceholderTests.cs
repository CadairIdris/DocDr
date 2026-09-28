using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocDr.App.Services;
using DocDr.App.ViewModels;
using DocDr.Pdf;

namespace DocDr.App.Tests;

/// <summary>A page scrolled into view gets a thumbnail-sized stand-in until its real render lands,
/// and a page that's already cached is shown without going through the render queue.</summary>
public sealed class PdfPanePlaceholderTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly PdfDocument _doc;

    public PdfPanePlaceholderTests() =>
        _doc = PdfDocument.Load(TestPdfBuilder.WritePdf(_dir.File("d.pdf"), ["one", "two", "three"]));

    public void Dispose()
    {
        _doc.Dispose();
        _dir.Dispose();
    }

    private static ImageSource Dummy()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr32, null, new byte[4], 4);
        bitmap.Freeze();
        return bitmap;
    }

    private (PdfPaneViewModel Pane, FakeRenderQueue Queue) NewPane()
    {
        var queue = new FakeRenderQueue();
        return (new PdfPaneViewModel("Left", _doc, _doc.GetPageSizes(), queue), queue);
    }

    private (int W, int H) ThumbSize(int page) =>
        ThumbnailStripViewModel.ThumbnailPixelSize(_doc.GetPageSizes()[page], 1.0);

    [StaFact]
    public void A_page_coming_into_view_queues_a_thumbnail_sized_placeholder_and_its_full_render()
    {
        (PdfPaneViewModel pane, FakeRenderQueue queue) = NewPane();

        pane.UpdateVisibleRange(0, 0);

        var forPage0 = queue.Enqueued.Where(r => r.PageIndex == 0).ToList();
        Assert.Contains(forPage0, r => (r.PixelWidth, r.PixelHeight) == ThumbSize(0));
        Assert.Contains(forPage0, r => r.PixelWidth > ThumbSize(0).W * 2);
    }

    [StaFact]
    public void The_placeholder_shows_until_the_real_image_lands_and_is_then_dropped()
    {
        (PdfPaneViewModel pane, FakeRenderQueue queue) = NewPane();
        pane.UpdateVisibleRange(0, 0);
        RenderRequest placeholder = queue.Enqueued.First(r => r.PageIndex == 0 && r.PixelWidth == ThumbSize(0).W);
        RenderRequest full = queue.Enqueued.First(r => r.PageIndex == 0 && r.PixelWidth > ThumbSize(0).W * 2);
        PageSlotViewModel slot = pane.Pages[0];

        ImageSource low = Dummy();
        placeholder.OnRendered(0, placeholder.PixelWidth, low);
        Assert.Same(low, slot.PlaceholderImage);
        Assert.Null(slot.Image);

        ImageSource real = Dummy();
        full.OnRendered(0, full.PixelWidth, real);
        Assert.Same(real, slot.Image);
        Assert.Null(slot.PlaceholderImage);

        placeholder.OnRendered(0, placeholder.PixelWidth, Dummy()); // a late placeholder is ignored
        Assert.Null(slot.PlaceholderImage);
    }

    [StaFact]
    public void An_already_cached_page_is_shown_immediately_without_queueing_anything_for_it()
    {
        (PdfPaneViewModel pane, FakeRenderQueue queue) = NewPane();

        // Learn the full render size from a first pass, then pretend it's cached.
        pane.UpdateVisibleRange(0, 0);
        RenderRequest full = queue.Enqueued.First(r => r.PageIndex == 0 && r.PixelWidth > ThumbSize(0).W * 2);

        (PdfPaneViewModel fresh, FakeRenderQueue freshQueue) = NewPane();
        ImageSource cached = Dummy();
        freshQueue.Cached[(0, full.PixelWidth, full.PixelHeight)] = cached;

        fresh.UpdateVisibleRange(0, 0);

        Assert.Same(cached, fresh.Pages[0].Image);
        Assert.DoesNotContain(freshQueue.Enqueued, r => r.PageIndex == 0);
    }
}
