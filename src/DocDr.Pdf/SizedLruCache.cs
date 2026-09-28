namespace DocDr.Pdf;

/// <summary>
/// A thread-safe least-recently-used cache bounded by both entry count and a caller-supplied
/// byte size per entry. A single entry bigger than a third of the byte budget is never stored —
/// caching it would just evict a run of smaller, reusable entries.
/// </summary>
/// <remarks>
/// WPF-free so the eviction rules are unit-testable here; the App keys it by page + pixel size
/// and stores finished, frozen page bitmaps in it (<c>PageImageService</c>).
/// </remarks>
public sealed class SizedLruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly long _maxEntryBytes;

    private readonly object _gate = new();
    private readonly LinkedList<(TKey Key, TValue Value, long Bytes)> _lru = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value, long Bytes)>> _index = new();
    private long _bytes;

    public SizedLruCache(int maxEntries, long maxBytes)
    {
        _maxEntries = Math.Max(1, maxEntries);
        _maxBytes = Math.Max(1, maxBytes);
        _maxEntryBytes = Math.Max(1, _maxBytes / 3);
    }

    /// <summary>Current entry count and total bytes held — for diagnostics.</summary>
    public (int Entries, long Bytes) Stats
    {
        get { lock (_gate) { return (_index.Count, _bytes); } }
    }

    /// <summary>Look up <paramref name="key"/>, marking it most recently used on a hit.</summary>
    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    /// <summary>Store <paramref name="value"/> (replacing any entry under the same key), then evict
    /// least-recently-used entries until both limits hold. Returns false — and stores nothing —
    /// when the entry alone exceeds a third of the byte budget.</summary>
    public bool Add(TKey key, TValue value, long bytes)
    {
        if (bytes > _maxEntryBytes)
        {
            return false;
        }

        lock (_gate)
        {
            if (_index.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _bytes -= existing.Value.Bytes;
            }

            _index[key] = _lru.AddFirst((key, value, bytes));
            _bytes += bytes;

            while ((_index.Count > _maxEntries || _bytes > _maxBytes) && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _index.Remove(last.Value.Key);
                _bytes -= last.Value.Bytes;
            }
        }

        return true;
    }

    /// <summary>Drop every entry whose key matches <paramref name="predicate"/>; returns how many.</summary>
    public int RemoveWhere(Func<TKey, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        int removed = 0;
        lock (_gate)
        {
            LinkedListNode<(TKey Key, TValue Value, long Bytes)>? node = _lru.First;
            while (node is not null)
            {
                LinkedListNode<(TKey Key, TValue Value, long Bytes)>? next = node.Next;
                if (predicate(node.Value.Key))
                {
                    _lru.Remove(node);
                    _index.Remove(node.Value.Key);
                    _bytes -= node.Value.Bytes;
                    removed++;
                }

                node = next;
            }
        }

        return removed;
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
}
