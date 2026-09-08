using System.Windows.Media;
using System.Windows.Threading;
using DocDr.Pdf;

namespace DocDr.App.Services;

/// <summary>A request to rasterise one page at one exact pixel size.</summary>
public sealed class RenderRequest
{
    /// <summary>Identifies who wants this render, so two panes requesting the same page/size do not
    /// coalesce into one and lose a callback. The heavy PDFium work is still de-duplicated by the
    /// page cache below the queue.</summary>
    public required object Owner { get; init; }

    public required PdfDocument Document { get; init; }
    public required int PageIndex { get; init; }
    public required int PixelWidth { get; init; }
    public required int PixelHeight { get; init; }

    /// <summary>Invoked on the UI thread with the finished image (never on failure/cancellation).</summary>
    public required Action<int, int, ImageSource> OnRendered { get; init; }

    internal RenderKey Key => new(Owner, PageIndex, PixelWidth, PixelHeight);
}

internal readonly record struct RenderKey(object Owner, int PageIndex, int PixelWidth, int PixelHeight);

/// <summary>
/// Serialises page rasterisation onto one background worker, newest-request-first (the page the
/// user just scrolled to renders before the backlog), coalescing duplicate requests. Results are
/// marshalled back to the UI dispatcher.
/// </summary>
public sealed class BackgroundRenderQueue : IDisposable, IRenderQueue
{
    private readonly PageImageService _images;
    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private readonly Dictionary<RenderKey, (RenderRequest Request, long Seq)> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private long _seq;

    public BackgroundRenderQueue(PageImageService images, Dispatcher dispatcher)
    {
        _images = images;
        _dispatcher = dispatcher;
        _worker = Task.Run(WorkerLoopAsync);
    }

    public void Enqueue(RenderRequest request)
    {
        lock (_gate)
        {
            _pending[request.Key] = (request, ++_seq);
        }

        _signal.Release();
    }

    /// <summary>Drop queued-but-not-started <em>page</em> renders (they go stale on a zoom / document
    /// swap). Thumbnail requests are kept — they're a fixed size, never stale, and cheap.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (RenderKey key in _pending
                         .Where(kv => IsStalePageRender(kv.Value.Request))
                         .Select(kv => kv.Key).ToList())
            {
                _pending.Remove(key);
            }
        }
    }

    /// <summary>A queued render that <see cref="Clear"/> drops: a full-size page render (which goes
    /// stale on a zoom / document swap), as opposed to a fixed-size thumbnail.</summary>
    internal static bool IsStalePageRender(RenderRequest request) => request.PixelWidth > ThumbnailPixelWidth;

    private async Task WorkerLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            RenderRequest? request = TakeNext();
            if (request is null)
            {
                continue;
            }

            try
            {
                ImageSource image = _images.Render(
                    request.Document, request.PageIndex, request.PixelWidth, request.PixelHeight, _shutdown.Token);

                RenderRequest captured = request;
                _ = _dispatcher.BeginInvoke(() =>
                    captured.OnRendered(captured.PageIndex, captured.PixelWidth, image));
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // One page failed — a closed document (ObjectDisposedException), a corrupt page
                // (PdfException), or an allocation too big at extreme zoom (OutOfMemoryException /
                // OverflowException). Skip this request; the worker MUST keep running or every
                // later page stays blank.
            }
        }
    }

    /// <summary>A render this narrow or narrower is a thumbnail — cheap, and served before page
    /// renders so a burst of scroll-driven page requests can't leave the strip blank.</summary>
    internal const int ThumbnailPixelWidth = 320;

    private RenderRequest? TakeNext()
    {
        lock (_gate)
        {
            if (PickNext(_pending) is not { } key)
            {
                return null;
            }

            RenderRequest request = _pending[key].Request;
            _pending.Remove(key);
            return request;
        }
    }

    /// <summary>Choose the next request to serve: thumbnails (≤ <see cref="ThumbnailPixelWidth"/>)
    /// before page renders; oldest-first among thumbnails so the strip fills top-to-bottom;
    /// newest-first among page renders so the page the user just scrolled to wins.</summary>
    internal static RenderKey? PickNext(IReadOnlyDictionary<RenderKey, (RenderRequest Request, long Seq)> pending)
    {
        RenderKey pickKey = default;
        long bestSeq = 0;
        bool haveThumb = false;
        bool have = false;

        foreach ((RenderKey key, (RenderRequest req, long seq)) in pending)
        {
            bool isThumb = req.PixelWidth <= ThumbnailPixelWidth;

            bool better = !have || (isThumb, haveThumb) switch
            {
                (true, false) => true,
                (true, true) => seq < bestSeq,
                (false, true) => false,
                (false, false) => seq > bestSeq,
            };

            if (better)
            {
                pickKey = key;
                bestSeq = seq;
                haveThumb = isThumb;
                have = true;
            }
        }

        return have ? pickKey : null;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _signal.Release();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // ignore shutdown races
        }

        _shutdown.Dispose();
        _signal.Dispose();
    }
}
