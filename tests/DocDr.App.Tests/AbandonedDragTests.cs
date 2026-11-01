using DocDr.App.ViewModels;
using DocDr.Pdf;

namespace DocDr.App.Tests;

/// <summary>
/// A drag whose page has gone (reload / page edit) must be dropped, not finished — the crash in the
/// field was a link click's mouse capture "continuing" an old drag on a page index that no longer
/// existed (ArgumentOutOfRangeException in DevicePointToPage).
/// </summary>
public sealed class AbandonedDragTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly PdfDocument _doc;

    public AbandonedDragTests() =>
        _doc = PdfDocument.Load(TestPdfBuilder.WritePdf(_dir.File("d.pdf"), ["one", "two"]));

    public void Dispose()
    {
        _doc.Dispose();
        _dir.Dispose();
    }

    private PdfPaneViewModel Pane() => new("Left", _doc, _doc.GetPageSizes(), new FakeRenderQueue());

    [Fact]
    public void HasPage_knows_which_pages_the_pane_shows()
    {
        PdfPaneViewModel pane = Pane();

        Assert.True(pane.HasPage(0));
        Assert.True(pane.HasPage(1));
        Assert.False(pane.HasPage(2));
        Assert.False(pane.HasPage(-1));
    }

    [Fact]
    public void A_cancelled_ink_stroke_adds_nothing()
    {
        PdfPaneViewModel pane = Pane();
        pane.BeginInk(0, new PdfPoint(100, 100));
        pane.ExtendInk(new PdfPoint(150, 120));
        pane.ExtendInk(new PdfPoint(200, 140));

        pane.CancelInk();
        pane.EndInk(); // a late mouse-up after the cancel commits nothing either

        Assert.Empty(_doc.GetAnnotations(0));
    }

    [Fact]
    public void A_cancelled_table_marquee_raises_no_region_and_leaves_the_tool_armed()
    {
        PdfPaneViewModel pane = Pane();
        pane.TableSelectActive = true;
        int raised = 0;
        pane.TableRegionSelected += (_, _) => raised++;
        pane.BeginTableSelect(0, new PdfPoint(100, 600));
        pane.ExtendTableSelect(new PdfPoint(300, 400));

        pane.CancelTableSelect();

        Assert.Equal(0, raised);
        Assert.True(pane.TableSelectActive);
    }
}
