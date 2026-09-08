namespace DocDr.Pdf;

/// <summary>
/// LRU cache decorator over an <see cref="IPageRenderer"/>. Keyed by (page, exact pixel size),
/// bounded by both entry count and total bytes. Thread-safe.
/// </summary>
/// <remarks>
/// Safe for several open documents at once — the key includes the document, so pages from
/// different files never collide. Call <see cref="Purge"/> when a document is closed.
/// </remarks>
public sealed class CachingPageRenderer : IPageRenderer
{
    private readonly record struct Key(PdfDocument Document, int PageIndex, int Width, int Height);

    private readonly IPageRenderer _inner;
    private readonly int _maxEntries;
    private readonly long _maxBytes;

    private readonly object _gate = new();
    private readonly LinkedList<(Key Key, RenderedPage Page)> _lru = new();
    private readonly Dictionary<Key, LinkedListNode<(Key Key, RenderedPage Page)>> _index = new();
    private long _bytes;

    private readonly long _maxEntryBytes;

    public CachingPageRenderer(IPageRenderer inner, int maxEntries = 60, long maxBytes = 256L * 1024 * 1024)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _maxEntries = Math.Max(1, maxEntries);
        _maxBytes = Math.Max(1, maxBytes);
        // A single render bigger than a third of the budget (a page at extreme zoom) is used
        // once and dropped — caching it would just evict a run of normal, reusable pages.
        _maxEntryBytes = Math.Max(1, _maxBytes / 3);
    }

    public RenderedPage Render(PdfDocument document, int pageIndex, int pixelWidth, int pixelHeight, CancellationToken cancellationToken = default)
    {
        var key = new Key(document, pageIndex, pixelWidth, pixelHeight);

        lock (_gate)
        {
            if (_index.TryGetValue(key, out var hit))
            {
                _lru.Remove(hit);
                _lru.AddFirst(hit);
                return hit.Value.Page;
            }
        }

        RenderedPage rendered = _inner.Render(document, pageIndex, pixelWidth, pixelHeight, cancellationToken);

        if (rendered.ByteCount > _maxEntryBytes)
        {
            return rendered; // too big to cache — the caller (page slot) holds it
        }

        lock (_gate)
        {
            if (!_index.ContainsKey(key))
            {
                var node = _lru.AddFirst((key, rendered));
                _index[key] = node;
                _bytes += rendered.ByteCount;
                Evict();
            }
        }

        return rendered;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lru.Clear();
            _index.Clear();
            _bytes = 0;
        }
    }

    /// <summary>Current entry count and total bytes held — for diagnostics.</summary>
    public (int Entries, long Bytes) Stats
    {
        get { lock (_gate) { return (_index.Count, _bytes); } }
    }

    /// <summary>Drop every cached page belonging to <paramref name="document"/> (called on tab close).</summary>
    public void Purge(PdfDocument document)
    {
        lock (_gate)
        {
            LinkedListNode<(Key Key, RenderedPage Page)>? node = _lru.First;
            while (node is not null)
            {
                LinkedListNode<(Key Key, RenderedPage Page)>? next = node.Next;
                if (node.Value.Key.Document == document)
                {
                    _lru.Remove(node);
                    _index.Remove(node.Value.Key);
                    _bytes -= node.Value.Page.ByteCount;
                }

                node = next;
            }
        }
    }

    private void Evict()
    {
        while ((_index.Count > _maxEntries || _bytes > _maxBytes) && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            _index.Remove(last.Value.Key);
            _bytes -= last.Value.Page.ByteCount;
        }
    }
}
