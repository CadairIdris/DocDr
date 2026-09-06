using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>
/// A single loaded PDF. One instance is shared between both viewer panes; every call that
/// touches the underlying PDFium document handle MUST go through <see cref="Locked{T}"/> /
/// <see cref="Locked(Action)"/> — a PDFium document handle is not safe for concurrent use.
/// </summary>
public sealed class PdfDocument : IDisposable
{
    // Serialization is process-wide (see PdfiumLibrary.SyncRoot) — a per-document lock is not
    // enough for this PDFium build. The field keeps call sites self-documenting.
    private readonly object _gate = PdfiumLibrary.SyncRoot;
    private FpdfDocumentT? _handle;
    private IReadOnlyList<PdfSize>? _pageSizes;

    private PdfDocument(FpdfDocumentT handle, string path, int pageCount)
    {
        _handle = handle;
        FilePath = path;
        PageCount = pageCount;
    }

    /// <summary>Absolute path the document was loaded from.</summary>
    public string FilePath { get; }

    /// <summary>Number of pages. Fixed for the lifetime of this Stage 1 object (no editing yet).</summary>
    public int PageCount { get; }

    internal FpdfDocumentT Handle =>
        _handle ?? throw new ObjectDisposedException(nameof(PdfDocument));

    /// <summary>Load a PDF from disk.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="PdfException">PDFium could not open the file.</exception>
    public static PdfDocument Load(string path, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PdfiumLibrary.EnsureInitialized();

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("PDF file not found.", fullPath);
        }

        lock (PdfiumLibrary.SyncRoot)
        {
            FpdfDocumentT? handle = fpdfview.FPDF_LoadDocument(fullPath, password);
            if (handle is null || handle.__Instance == IntPtr.Zero)
            {
                var error = (PdfiumError)fpdfview.FPDF_GetLastError();
                throw new PdfException(error, $"Failed to open '{fullPath}': {error}.");
            }

            int pageCount = fpdfview.FPDF_GetPageCount(handle);
            if (pageCount <= 0)
            {
                fpdfview.FPDF_CloseDocument(handle);
                throw new PdfException(PdfiumError.PageNotFoundOrContentError,
                    $"'{fullPath}' reports {pageCount} pages.");
            }

            return new PdfDocument(handle, fullPath, pageCount);
        }
    }

    /// <summary>Run <paramref name="work"/> holding this document's exclusive PDFium lock.</summary>
    public T Locked<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle is null, this);
            return work();
        }
    }

    /// <summary>Run <paramref name="work"/> holding this document's exclusive PDFium lock.</summary>
    public void Locked(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle is null, this);
            work();
        }
    }

    /// <summary>
    /// Sizes (in points) of every page, computed once in a single locked batch so the
    /// continuous-scroll layout can be established before any page is rendered.
    /// </summary>
    public IReadOnlyList<PdfSize> GetPageSizes()
    {
        if (_pageSizes is not null)
        {
            return _pageSizes;
        }

        return _pageSizes = Locked(() =>
        {
            var sizes = new PdfSize[PageCount];
            for (int i = 0; i < PageCount; i++)
            {
                double width = 0;
                double height = 0;
                fpdfview.FPDF_GetPageSizeByIndex(Handle, i, ref width, ref height);
                sizes[i] = new PdfSize(width, height);
            }

            return (IReadOnlyList<PdfSize>)sizes;
        });
    }

    /// <summary>Size (in points) of a single page.</summary>
    public PdfSize GetPageSize(int pageIndex)
    {
        ValidatePageIndex(pageIndex);
        return GetPageSizes()[pageIndex];
    }

    internal void ValidatePageIndex(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex,
                $"Document has {PageCount} pages.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle is null)
            {
                return;
            }

            fpdfview.FPDF_CloseDocument(_handle);
            _handle = null;
        }
    }
}
