namespace DocDr.Pdf;

/// <summary>
/// Each page's extracted text (<c>FPDFText_GetText</c> over the whole page, so string index ==
/// PDFium char index), kept once per document so repeat searches, the clause scan, RAG chunk
/// export and the OCR blank-page check stop re-asking PDFium for text it already gave us.
/// <para>
/// Keyed on <see cref="PdfDocument.ContentVersion"/>: the first lookup or store under a newer
/// version drops every entry (page edits, rotation, trim, watermark strip, OCR, undo/redo all bump
/// it), and a store tagged with an older version than the cache now holds is ignored, so text read
/// before an edit can never be served after it. Annotations never touch the live handle, so their
/// edits don't need to invalidate anything.
/// </para>
/// <para>
/// Strings only — a 400-page standard is a few MB. Character boxes are deliberately not cached
/// (≈ 10× the size); search fetches boxes only for pages that actually have a match.
/// </para>
/// </summary>
internal sealed class PdfPageTextCache
{
    /// <summary>Total cached characters before the cache starts over (~128 MB of UTF-16) — a
    /// backstop for a pathological file, not a working limit for real documents.</summary>
    internal const long MaxChars = 64_000_000;

    private readonly object _gate = new();
    private readonly Dictionary<int, string> _pages = [];
    private int _version = int.MinValue;
    private long _chars;

    public bool TryGet(int version, int pageIndex, out string text)
    {
        lock (_gate)
        {
            if (!Sync(version))
            {
                text = string.Empty;
                return false;
            }

            if (_pages.TryGetValue(pageIndex, out string? found))
            {
                text = found;
                return true;
            }

            text = string.Empty;
            return false;
        }
    }

    public void Store(int version, int pageIndex, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            if (!Sync(version))
            {
                return; // read under a version that has since been superseded
            }

            if (_pages.TryGetValue(pageIndex, out string? existing))
            {
                _chars -= existing.Length;
            }

            if (_chars + text.Length > MaxChars)
            {
                _pages.Clear();
                _chars = 0;
            }

            _pages[pageIndex] = text;
            _chars += text.Length;
        }
    }

    /// <summary>Number of pages currently cached (tests).</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _pages.Count;
            }
        }
    }

    /// <summary>Bring the cache to <paramref name="version"/>; false if that version is older
    /// than what the cache already holds (the caller's read is stale).</summary>
    private bool Sync(int version)
    {
        if (version == _version)
        {
            return true;
        }

        if (version < _version)
        {
            return false;
        }

        _pages.Clear();
        _chars = 0;
        _version = version;
        return true;
    }
}
