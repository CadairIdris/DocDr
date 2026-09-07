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
public sealed class BackgroundRenderQueue : IDisposable
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

    /// <summary>Drop every queued-but-not-started request (e.g. on zoom change or document swap).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }

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

            RenderRequest? request = TakeNewest();
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

    private RenderRequest? TakeNewest()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return null;
            }

            RenderKey newestKey = default;
            long newestSeq = long.MinValue;
            foreach ((RenderKey key, (_, long seq)) in _pending)
            {
                if (seq > newestSeq)
                {
                    newestSeq = seq;
                    newestKey = key;
                }
            }

            RenderRequest request = _pending[newestKey].Request;
            _pending.Remove(newestKey);
            return request;
        }
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
