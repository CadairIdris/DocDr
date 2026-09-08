using System.Collections.Generic;
using DocDr.App.Services;
using DocDr.Pdf;

namespace DocDr.App.Tests;

public sealed class BackgroundRenderQueueTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly PdfDocument _doc;

    public BackgroundRenderQueueTests() =>
        _doc = PdfDocument.Load(TestPdfBuilder.WritePdf(_dir.File("d.pdf"), ["a", "b", "c", "d"]));

    public void Dispose()
    {
        _doc.Dispose();
        _dir.Dispose();
    }

    private RenderRequest Req(int page, int width) => new()
    {
        Owner = this,
        Document = _doc,
        PageIndex = page,
        PixelWidth = width,
        PixelHeight = width * 2,
        OnRendered = (_, _, _) => { },
    };

    private static Dictionary<RenderKey, (RenderRequest Request, long Seq)> Pending(params (RenderRequest Req, long Seq)[] items)
    {
        var d = new Dictionary<RenderKey, (RenderRequest, long)>();
        foreach ((RenderRequest req, long seq) in items)
        {
            d[req.Key] = (req, seq);
        }

        return d;
    }

    [Fact]
    public void Empty_queue_picks_nothing() =>
        Assert.Null(BackgroundRenderQueue.PickNext(Pending()));

    [Fact]
    public void Thumbnail_requests_are_served_before_a_newer_page_request()
    {
        RenderRequest thumb = Req(0, 200);
        RenderRequest page = Req(1, 1600);
        var pending = Pending((thumb, 1), (page, 2));

        Assert.Equal(thumb.Key, BackgroundRenderQueue.PickNext(pending));
    }

    [Fact]
    public void Among_page_renders_the_newest_wins()
    {
        RenderRequest older = Req(0, 1600);
        RenderRequest newer = Req(1, 1600);
        var pending = Pending((older, 5), (newer, 9));

        Assert.Equal(newer.Key, BackgroundRenderQueue.PickNext(pending));
    }

    [Fact]
    public void Among_thumbnails_the_oldest_wins_so_the_strip_fills_top_down()
    {
        RenderRequest first = Req(0, 200);
        RenderRequest second = Req(1, 200);
        var pending = Pending((second, 20), (first, 10));

        Assert.Equal(first.Key, BackgroundRenderQueue.PickNext(pending));
    }

    [Fact]
    public void Clear_targets_page_renders_but_not_thumbnails()
    {
        Assert.True(BackgroundRenderQueue.IsStalePageRender(Req(1, 1600)));
        Assert.False(BackgroundRenderQueue.IsStalePageRender(Req(0, BackgroundRenderQueue.ThumbnailPixelWidth)));
        Assert.False(BackgroundRenderQueue.IsStalePageRender(Req(0, 200)));
    }
}
