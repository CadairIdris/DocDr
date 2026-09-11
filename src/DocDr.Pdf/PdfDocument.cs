using System.Runtime.InteropServices;
using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>
/// A single loaded PDF. One instance is shared between both viewer panes; every call that
/// touches the underlying PDFium document handle MUST go through <see cref="Locked{T}"/> /
/// <see cref="Locked(Action)"/> — a PDFium document handle is not safe for concurrent use.
/// <para>
/// Files are read fully into memory at load time (not streamed), so the source file is not
/// held open and <see cref="Save"/> can overwrite it. Page edits (<see cref="RotatePages"/>,
/// <see cref="DeletePages"/>, <see cref="InsertPages(string,int)"/>) are modelled as an ordered
/// list of page references into one or more source documents; structural changes rebuild the
/// live PDFium handle from that list. Undo/redo restore earlier versions of the list.
/// </para>
/// </summary>
public sealed class PdfDocument : IDisposable
{
    private const int MaxHistory = 30;
    private const int OriginalSourceId = 0;

    private readonly record struct PageRef(int SourceId, int SourcePageIndex, int Rotation)
    {
        /// <summary>Absolute CropBox (source-page MediaBox coords) applied after a trim, or null
        /// for the source page's own box. Re-applied on every <see cref="Rebuild"/>, like rotation.</summary>
        public PdfRect? CropBox { get; init; }
    }

    private readonly record struct HistoryStep(
        IReadOnlyList<PageRef> Pages,
        IReadOnlyList<IReadOnlyList<PdfAnnotation>> Annotations);

    private sealed class Source
    {
        public required FpdfDocumentT Handle { get; init; }
        public GCHandle Pin { get; init; }
        public bool Owned { get; init; }
    }

    private readonly object _gate = PdfiumLibrary.SyncRoot;
    private readonly Dictionary<int, Source> _sources = [];
    private readonly List<HistoryStep> _undo = [];
    private readonly List<HistoryStep> _redo = [];

    private FpdfDocumentT? _handle;
    private List<PageRef> _pages;

    /// <summary>DocDr-managed annotations per logical page. Index-aligned with <see cref="_pages"/>.</summary>
    private List<List<PdfAnnotation>> _annotations;

    private int _nextSourceId = OriginalSourceId + 1;
    private IReadOnlyList<PdfSize>? _pageSizes;
    private IReadOnlyList<PdfPoint>? _cropOrigins;
    private IReadOnlyList<string?>? _pageLabels;
    private bool _disposed;

    private PdfDocumentInfo _info = PdfDocumentInfo.Empty;
    private bool _infoEdited;
    private bool _handleHasOriginalInfo = true;

    /// <summary>A DocDr-generated outline that overrides the file's own (if any). Written on save.</summary>
    private IReadOnlyList<PdfBookmark>? _outline;

    /// <summary>Decoder for image-annotation bytes, used when baking. Set by the app; null = image
    /// annotations are skipped on save.</summary>
    public IImageDecoder? ImageDecoder { get; set; }

    private PdfDocument(FpdfDocumentT handle, GCHandle pin, string? path, int pageCount)
    {
        _handle = handle;
        _sources[OriginalSourceId] = new Source { Handle = handle, Pin = pin, Owned = true };

        _pages = new List<PageRef>(pageCount);
        _annotations = new List<List<PdfAnnotation>>(pageCount);
        for (int i = 0; i < pageCount; i++)
        {
            // One FPDF_LoadPage per page for all three concerns below (rotation, annotation read,
            // strip) rather than one each — FPDF_LoadPage does real parsing work, and a large book
            // opened noticeably slower paying for it three times over on every page.
            FpdfPageT? page = fpdfview.FPDF_LoadPage(handle, i);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                _pages.Add(new PageRef(OriginalSourceId, i, 0));
                _annotations.Add([]);
                continue;
            }

            try
            {
                _pages.Add(new PageRef(OriginalSourceId, i, GetRotation(page) & 3));

                // Read any existing highlights / notes into our model, then strip them off the
                // original handle so PDFium's renderer (annotations flag on) doesn't draw them
                // under our own overlay. Stripping the original == stripping _sources[0], so every
                // later Rebuild() imports a clean base.
                List<PdfAnnotation> onPage = PdfAnnotations.ReadFromPage(page).ToList();
                _annotations.Add(onPage);
                if (onPage.Count > 0)
                {
                    PdfAnnotationWriter.StripManaged(page);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }

        _info = PdfMetadata.GetInfo(this);
        FilePath = path;
    }

    /// <summary>Path the document was loaded from / last saved to. Null for a doc built from bytes.</summary>
    public string? FilePath { get; private set; }

    /// <summary>Number of pages. Changes as pages are inserted or deleted.</summary>
    public int PageCount => _pages.Count;

    /// <summary>True once any edit has been applied and the document has not been saved since.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>The source PDF carried a standard-security handler (password / permissions).
    /// Saving or stripping watermarks rewrites it without encryption.</summary>
    public bool IsEncrypted => Locked(() => fpdfview.FPDF_GetSecurityHandlerRevision(Handle) >= 0);

    /// <summary>The PDF declares a logical structure tree (<c>/MarkInfo /Marked true</c>) — its
    /// headings, lists and reading order can be read semantically rather than guessed.</summary>
    public bool IsTagged => Locked(() => fpdf_catalog.FPDFCatalogIsTagged(Handle) != 0);

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised (on the calling thread) after any edit, undo, or redo changes the pages.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when <see cref="IsDirty"/> changes.</summary>
    public event EventHandler? DirtyChanged;

    /// <summary>Raised when <see cref="UpdateInfo"/> changes the document metadata.</summary>
    public event EventHandler? MetadataChanged;

    /// <summary>Raised when <see cref="SetOutline"/> replaces the document outline. Lighter than
    /// <see cref="Changed"/> — only the bookmarks view needs refreshing.</summary>
    public event EventHandler? OutlineChanged;

    /// <summary>
    /// Raised after an annotation is added, edited, removed, or moved by undo/redo or a
    /// structural edit. Lighter than <see cref="Changed"/> — only the overlay needs rebuilding.
    /// </summary>
    public event EventHandler<AnnotationsChangedEventArgs>? AnnotationsChanged;

    /// <summary>Current Info-dictionary metadata (as loaded, plus any edits via <see cref="UpdateInfo"/>).</summary>
    public PdfDocumentInfo Info => _info;

    /// <summary>Replace the document metadata. Applied to the file on the next save; not part of undo.</summary>
    public void UpdateInfo(PdfDocumentInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info == _info)
        {
            return;
        }

        _info = info;
        _infoEdited = true;
        SetDirty(true);
        MetadataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The document outline — a DocDr-generated one (<see cref="SetOutline"/>) if present, else the
    /// file's own via PDFium. Callers should use this rather than <see cref="PdfBookmarks.Read"/>.
    /// </summary>
    public IReadOnlyList<PdfBookmark> GetOutline() => _outline ?? PdfBookmarks.Read(this);

    /// <summary>True when the outline was generated by DocDr rather than read from the file.</summary>
    public bool HasGeneratedOutline => _outline is not null;

    /// <summary>
    /// Replace the outline with a generated tree (page indices 0-based into the current page order).
    /// Applied to the file on the next save; not part of undo. A later structural edit is not
    /// reflected in these page indices — regenerate afterwards.
    /// </summary>
    public void SetOutline(IReadOnlyList<PdfBookmark> outline)
    {
        ArgumentNullException.ThrowIfNull(outline);
        _outline = outline;
        SetDirty(true);
        OutlineChanged?.Invoke(this, EventArgs.Empty);
    }

    internal FpdfDocumentT Handle =>
        _handle ?? throw new ObjectDisposedException(nameof(PdfDocument));

    // --- Loading -----------------------------------------------------------------------------

    public static PdfDocument Load(string path, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("PDF file not found.", fullPath);
        }

        return Load(File.ReadAllBytes(fullPath), password, fullPath);
    }

    public static PdfDocument Load(byte[] bytes, string? password = null) => Load(bytes, password, null);

    private static PdfDocument Load(byte[] bytes, string? password, string? path)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
        {
            throw new PdfException(PdfiumError.InvalidFormat, "The PDF is empty.");
        }

        PdfiumLibrary.EnsureInitialized();
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            lock (PdfiumLibrary.SyncRoot)
            {
                FpdfDocumentT? handle = fpdfview.FPDF_LoadMemDocument64(
                    pin.AddrOfPinnedObject(), (ulong)bytes.LongLength, password);
                if (handle is null || handle.__Instance == IntPtr.Zero)
                {
                    var error = (PdfiumError)fpdfview.FPDF_GetLastError();
                    throw new PdfException(error, $"Failed to open PDF: {error}.");
                }

                int pageCount = fpdfview.FPDF_GetPageCount(handle);
                if (pageCount <= 0)
                {
                    fpdfview.FPDF_CloseDocument(handle);
                    throw new PdfException(PdfiumError.PageNotFoundOrContentError,
                        $"PDF reports {pageCount} pages.");
                }

                return new PdfDocument(handle, pin, path, pageCount);
            }
        }
        catch
        {
            pin.Free();
            throw;
        }
    }

    /// <summary>Construct a merged document from prepared sources. Caller holds the PDFium lock.</summary>
    private PdfDocument(
        FpdfDocumentT handle,
        List<PageRef> pages,
        List<List<PdfAnnotation>> annotations,
        Dictionary<int, Source> sources,
        int nextSourceId)
    {
        _handle = handle;
        _pages = pages;
        _annotations = annotations;
        foreach ((int id, Source source) in sources)
        {
            _sources[id] = source;
        }

        _nextSourceId = nextSourceId;
        _handleHasOriginalInfo = false; // FPDF_CreateNewDocument has no Info dict; re-added at save time
        _info = PdfDocumentInfo.Empty;
        IsDirty = true;
    }

    /// <summary>
    /// Create a new one-page untitled document of the given size (points). No <see cref="FilePath"/>,
    /// dirty, empty undo history — a blank canvas for building a collage.
    /// </summary>
    public static PdfDocument CreateBlank(PdfSize sizePoints)
    {
        if (sizePoints.Width <= 1 || sizePoints.Height <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sizePoints), "Page size must be positive.");
        }

        PdfiumLibrary.EnsureInitialized();
        lock (PdfiumLibrary.SyncRoot)
        {
            FpdfDocumentT? handle = fpdf_edit.FPDF_CreateNewDocument();
            if (handle is null || handle.__Instance == IntPtr.Zero)
            {
                throw new PdfException("PDFium could not create a new document.");
            }

            FpdfPageT? page = fpdf_edit.FPDFPageNew(handle, 0, (float)sizePoints.Width, (float)sizePoints.Height);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                fpdfview.FPDF_CloseDocument(handle);
                throw new PdfException("PDFium could not create the page.");
            }

            fpdf_edit.FPDFPageGenerateContent(page);
            fpdfview.FPDF_ClosePage(page);

            var sources = new Dictionary<int, Source> { [OriginalSourceId] = new() { Handle = handle, Owned = true } };
            return new PdfDocument(handle, [new PageRef(OriginalSourceId, 0, 0)], [[]], sources, OriginalSourceId + 1);
        }
    }

    /// <summary>
    /// Build a new untitled document from every page of each PDF in <paramref name="paths"/>, in
    /// order. The result has no <see cref="FilePath"/>, is dirty, and starts with an empty undo
    /// history; structural edits and saving work as for any loaded document. Any generated
    /// <see cref="SetOutline"/> should be applied before further structural edits.
    /// </summary>
    public static MergeResult Merge(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            throw new ArgumentException("Merge needs at least one file.", nameof(paths));
        }

        PdfiumLibrary.EnsureInitialized();

        var sources = new List<PdfDocument>();
        try
        {
            foreach (string path in paths)
            {
                sources.Add(Load(path));
            }

            lock (PdfiumLibrary.SyncRoot)
            {
                FpdfDocumentT? merged = fpdf_edit.FPDF_CreateNewDocument();
                if (merged is null || merged.__Instance == IntPtr.Zero)
                {
                    throw new PdfException("PDFium could not create the merged document.");
                }

                var pages = new List<PageRef>();
                var annotations = new List<List<PdfAnnotation>>();
                var starts = new List<int>(sources.Count);
                var handles = new Dictionary<int, Source>();
                int dest = 0;
                int sourceId = OriginalSourceId;

                try
                {
                    foreach (PdfDocument src in sources)
                    {
                        starts.Add(dest);
                        int count = src._pages.Count;
                        if (fpdf_ppo.FPDF_ImportPages(
                                merged, src._sources[OriginalSourceId].Handle, null, dest) == 0)
                        {
                            throw new PdfException("PDFium could not import pages from a source PDF.");
                        }

                        (FpdfDocumentT handle, GCHandle pin) = DetachSource(src);
                        handles[sourceId] = new Source { Handle = handle, Pin = pin, Owned = true };

                        for (int i = 0; i < count; i++)
                        {
                            int rotation = src._pages[i].Rotation;
                            pages.Add(new PageRef(sourceId, i, rotation));
                            annotations.Add([.. src._annotations[i]]);
                            if (rotation != 0)
                            {
                                SetRotation(merged, dest + i, rotation);
                            }
                        }

                        dest += count;
                        sourceId++;
                    }
                }
                catch
                {
                    fpdfview.FPDF_CloseDocument(merged);
                    foreach (Source s in handles.Values)
                    {
                        fpdfview.FPDF_CloseDocument(s.Handle);
                        if (s.Pin.IsAllocated)
                        {
                            s.Pin.Free();
                        }
                    }

                    throw;
                }

                var document = new PdfDocument(merged, pages, annotations, handles, sourceId);
                return new MergeResult(document, starts);
            }
        }
        finally
        {
            // Detached sources are inert; frees any that were loaded before a failure.
            foreach (PdfDocument src in sources)
            {
                src.Dispose();
            }
        }
    }

    // --- Locked access ---------------------------------------------------------------------

    public T Locked<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle is null, this);
            return work();
        }
    }

    public void Locked(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle is null, this);
            work();
        }
    }

    // --- Page metadata -------------------------------------------------------------------

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

    public PdfSize GetPageSize(int pageIndex)
    {
        ValidatePageIndex(pageIndex);
        return GetPageSizes()[pageIndex];
    }

    /// <summary>
    /// The CropBox lower-left, expressed in the MediaBox coordinate system, for every page.
    /// <para>
    /// PDFium's text and annotation APIs report page-space coordinates with the origin at the
    /// <b>MediaBox</b> lower-left, but a page renders (and <see cref="GetPageSize"/> reports)
    /// from its <b>CropBox</b>. When the two differ, geometry from those APIs must be shifted by
    /// this offset to line up with the rendered image — subtract it to place text / annotation
    /// rects on the page, add it back before writing an annotation.
    /// </para>
    /// </summary>
    public IReadOnlyList<PdfPoint> GetCropOrigins()
    {
        if (_cropOrigins is not null)
        {
            return _cropOrigins;
        }

        return _cropOrigins = Locked(() =>
        {
            var origins = new PdfPoint[PageCount];
            for (int i = 0; i < PageCount; i++)
            {
                origins[i] = ReadCropOrigin(Handle, i);
            }

            return (IReadOnlyList<PdfPoint>)origins;
        });
    }

    public PdfPoint GetCropOrigin(int pageIndex)
    {
        ValidatePageIndex(pageIndex);
        return GetCropOrigins()[pageIndex];
    }

    /// <summary>
    /// The page's printed label ("iv", "A-3", "B-12") from the document's <c>/PageLabels</c> number
    /// tree, or <c>null</c> when there is no tree or the label is just the plain ordinal. Never
    /// changes for a given source; a structural edit clears the cache and merged docs have no tree.
    /// </summary>
    public string? GetPageLabel(int pageIndex)
    {
        ValidatePageIndex(pageIndex);
        return GetPageLabels()[pageIndex];
    }

    /// <summary>True when at least one page carries a non-ordinal <c>/PageLabels</c> label.</summary>
    public bool HasPageLabels => GetPageLabels().Any(l => l is not null);

    private IReadOnlyList<string?> GetPageLabels()
    {
        if (_pageLabels is not null)
        {
            return _pageLabels;
        }

        return _pageLabels = Locked(() =>
        {
            var labels = new string?[PageCount];
            for (int i = 0; i < PageCount; i++)
            {
                string? label = ReadPageLabel(Handle, i);
                labels[i] = label is not null && label != (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    ? label
                    : null;
            }

            return (IReadOnlyList<string?>)labels;
        });
    }

    private static string? ReadPageLabel(FpdfDocumentT handle, int pageIndex)
    {
        uint byteLength = fpdf_doc.FPDF_GetPageLabel(handle, pageIndex, IntPtr.Zero, 0);
        if (byteLength <= 2)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)byteLength);
        try
        {
            fpdf_doc.FPDF_GetPageLabel(handle, pageIndex, buffer, byteLength);
            string? label = Marshal.PtrToStringUni(buffer)?.TrimEnd('\0');
            return string.IsNullOrWhiteSpace(label) ? null : label;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static PdfPoint ReadCropOrigin(FpdfDocumentT handle, int pageIndex)
    {
        FpdfPageT? page = fpdfview.FPDF_LoadPage(handle, pageIndex);
        if (page is null || page.__Instance == IntPtr.Zero)
        {
            return default;
        }

        try
        {
            float mL = 0, mB = 0, mR = 0, mT = 0, cL = 0, cB = 0, cR = 0, cT = 0;
            bool hasMedia = fpdf_transformpage.FPDFPageGetMediaBox(page, ref mL, ref mB, ref mR, ref mT) != 0;
            bool hasCrop = fpdf_transformpage.FPDFPageGetCropBox(page, ref cL, ref cB, ref cR, ref cT) != 0;
            if (!hasCrop)
            {
                return default;
            }

            double mediaLeft = hasMedia ? mL : 0;
            double mediaBottom = hasMedia ? mB : 0;

            // PDFium renders the CropBox clamped to the MediaBox.
            double x = Math.Max(cL, mediaLeft) - mediaLeft;
            double y = Math.Max(cB, mediaBottom) - mediaBottom;
            return new PdfPoint(x, y);
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    /// <summary>Current clockwise rotation of a page.</summary>
    public PdfRotation GetPageRotation(int pageIndex)
    {
        ValidatePageIndex(pageIndex);
        return (PdfRotation)(_pages[pageIndex].Rotation & 3);
    }

    /// <summary>
    /// Page size <em>before</em> its rotation is applied — the space text boxes and annotation
    /// quad points live in. (<see cref="GetPageSize"/> returns the rotated, on-screen size.)
    /// </summary>
    public PdfSize GetUnrotatedPageSize(int pageIndex)
    {
        PdfSize size = GetPageSize(pageIndex);
        return GetPageRotation(pageIndex) is PdfRotation.Clockwise90 or PdfRotation.CounterClockwise90
            ? new PdfSize(size.Height, size.Width)
            : size;
    }

    // --- Annotations -------------------------------------------------------------------

    /// <summary>Total DocDr-managed annotations across every page.</summary>
    public int AnnotationCount => _annotations.Sum(list => list.Count);

    public bool HasAnnotations => _annotations.Any(list => list.Count > 0);

    /// <summary>The managed annotations on a page (a copy — edit via <see cref="UpdateAnnotation"/>).</summary>
    public IReadOnlyList<PdfAnnotation> GetAnnotations(int pageIndex)
    {
        ValidatePageIndex(pageIndex);
        return _annotations[pageIndex].ToArray();
    }

    public void AddAnnotation(int pageIndex, PdfAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ValidatePageIndex(pageIndex);

        Locked(() =>
        {
            PushUndo();
            _annotations[pageIndex].Add(annotation);
        });

        AfterAnnotationEdit(pageIndex);
    }

    /// <summary>Replace the annotation with the same <see cref="PdfAnnotation.Id"/>. No-op if absent.</summary>
    public void UpdateAnnotation(int pageIndex, PdfAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ValidatePageIndex(pageIndex);

        bool changed = Locked(() =>
        {
            List<PdfAnnotation> list = _annotations[pageIndex];
            int index = list.FindIndex(a => a.Id == annotation.Id);
            if (index < 0)
            {
                return false;
            }

            PushUndo();
            list[index] = annotation;
            return true;
        });

        if (changed)
        {
            AfterAnnotationEdit(pageIndex);
        }
    }

    public void RemoveAnnotation(int pageIndex, Guid id)
    {
        ValidatePageIndex(pageIndex);

        bool changed = Locked(() =>
        {
            List<PdfAnnotation> list = _annotations[pageIndex];
            int index = list.FindIndex(a => a.Id == id);
            if (index < 0)
            {
                return false;
            }

            PushUndo();
            list.RemoveAt(index);
            return true;
        });

        if (changed)
        {
            AfterAnnotationEdit(pageIndex);
        }
    }

    private void AfterAnnotationEdit(int pageIndex)
    {
        SetDirty(true);
        AnnotationsChanged?.Invoke(this, new AnnotationsChangedEventArgs(pageIndex));
    }

    internal void ValidatePageIndex(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex,
                $"Document has {PageCount} pages.");
        }
    }

    // --- Editing --------------------------------------------------------------------------

    /// <summary>Rotate the given pages by <paramref name="delta"/> quarter-turns clockwise.</summary>
    public void RotatePages(IReadOnlyList<int> pageIndices, PdfRotation delta)
    {
        int[] indices = Normalize(pageIndices);
        if (indices.Length == 0 || delta == PdfRotation.None)
        {
            return;
        }

        Locked(() =>
        {
            PushUndo();
            foreach (int i in indices)
            {
                PageRef pr = _pages[i];
                _pages[i] = pr with { Rotation = (pr.Rotation + (int)delta) & 3 };
            }

            SyncRotations();
        });

        AfterEdit();
    }

    /// <summary>Delete the given pages.</summary>
    public void DeletePages(IReadOnlyList<int> pageIndices)
    {
        int[] indices = Normalize(pageIndices);
        if (indices.Length == 0)
        {
            return;
        }

        if (indices.Length >= PageCount)
        {
            throw new PdfException("A document must keep at least one page.");
        }

        Locked(() =>
        {
            PushUndo();
            for (int k = indices.Length - 1; k >= 0; k--)
            {
                _pages.RemoveAt(indices[k]);
                _annotations.RemoveAt(indices[k]);
            }

            Rebuild();
        });

        AfterEdit();
    }

    /// <summary>Insert every page of the PDF at <paramref name="sourcePath"/> at <paramref name="atIndex"/>.</summary>
    public void InsertPages(string sourcePath, int atIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        InsertFromDocument(Load(sourcePath), atIndex, ownsSource: true);
    }

    /// <summary>Insert every page of <paramref name="source"/> at <paramref name="atIndex"/>.</summary>
    public void InsertPages(PdfDocument source, int atIndex) =>
        InsertFromDocument(source, atIndex, ownsSource: false);

    private void InsertFromDocument(PdfDocument source, int atIndex, bool ownsSource)
    {
        ArgumentNullException.ThrowIfNull(source);

        Locked(() =>
        {
            int at = Math.Clamp(atIndex, 0, _pages.Count);
            PushUndo();

            int sourceId = _nextSourceId++;
            if (ownsSource)
            {
                (FpdfDocumentT handle, GCHandle pin) = DetachSource(source);
                _sources[sourceId] = new Source { Handle = handle, Pin = pin, Owned = true };
            }
            else
            {
                _sources[sourceId] = new Source { Handle = source.Handle, Owned = false };
            }

            var inserted = new List<PageRef>(source.PageCount);
            var insertedAnnotations = new List<List<PdfAnnotation>>(source.PageCount);
            for (int i = 0; i < source._pages.Count; i++)
            {
                inserted.Add(new PageRef(sourceId, i, source._pages[i].Rotation));
                insertedAnnotations.Add([.. source._annotations[i]]);
            }

            _pages.InsertRange(at, inserted);
            _annotations.InsertRange(at, insertedAnnotations);
            Rebuild();
        });

        AfterEdit();
    }

    /// <summary>
    /// Insert one blank page after <paramref name="afterIndex"/> (0-based; pass -1 for the very
    /// front). The new page's on-screen size matches the page it follows — or the page after it
    /// when inserting at the front.
    /// </summary>
    public void InsertBlankPage(int afterIndex)
    {
        Locked(() =>
        {
            int at = Math.Clamp(afterIndex + 1, 0, _pages.Count);
            int sizeRef = Math.Clamp(at - 1 >= 0 ? at - 1 : at, 0, _pages.Count - 1);
            PdfSize size = GetPageSizes()[sizeRef]; // the displayed (rotation-applied) size

            PushUndo();

            int sourceId = _nextSourceId++;
            FpdfDocumentT blank = fpdf_edit.FPDF_CreateNewDocument();
            FpdfPageT page = fpdf_edit.FPDFPageNew(blank, 0, size.Width, size.Height);
            fpdf_edit.FPDFPageGenerateContent(page);
            fpdfview.FPDF_ClosePage(page);
            _sources[sourceId] = new Source { Handle = blank, Owned = true };

            _pages.Insert(at, new PageRef(sourceId, 0, 0));
            _annotations.Insert(at, []);
            Rebuild();
        });

        AfterEdit();
    }

    // --- Watermark removal --------------------------------------------------------------

    /// <summary>
    /// Strip the given repeating-content watermarks (from <see cref="PdfWatermarks.Scan"/>) from
    /// every page. This edits page content and is <b>not undoable</b>: it rebuilds the document
    /// from the cleaned bytes and clears the undo history (any earlier rotate / delete / insert
    /// can no longer be undone). The original file is untouched until the next save.
    /// </summary>
    public void RemoveWatermarks(IReadOnlyList<WatermarkCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var textSignatures = candidates
            .Where(c => c.Signature.Kind == WatermarkKind.Text && c.Signature.Text is not null)
            .Select(c => c.Signature.Text!)
            .ToHashSet();
        var imageHashes = candidates
            .Where(c => c.Signature.Kind == WatermarkKind.Image && c.Signature.ImageHash is not null)
            .Select(c => c.Signature.ImageHash!)
            .ToHashSet();

        if (textSignatures.Count == 0 && imageHashes.Count == 0)
        {
            return;
        }

        bool changed = Locked(() =>
        {
            // Serialise the current pages (rotations + structural edits included; our annotation
            // model is left untouched), delete the watermark operators from the bytes, then
            // reload. Editing the bytes — rather than FPDFPageRemoveObject + GenerateContent —
            // keeps every other operator byte-for-byte, so kerned tables don't get scrambled.
            byte[] current = SerialiseCurrentHandle();
            byte[] stripped = PdfWatermarkStripper.Strip(current, textSignatures, imageHashes);
            if (ReferenceEquals(stripped, current))
            {
                return false;
            }

            AdoptStrippedBytes(stripped);
            return true;
        });

        if (!changed)
        {
            return;
        }

        _pageSizes = null;
        _cropOrigins = null;
        _pageLabels = null;
        SetDirty(true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replace every source and the live handle with a freshly loaded copy of <paramref name="bytes"/>.
    /// Caller holds <see cref="Locked(Action)"/>. The annotation model is index-aligned with the
    /// (unchanged) page order, so it is kept as-is.</summary>
    private void AdoptStrippedBytes(byte[] bytes, string? failureMessage = null)
    {
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        FpdfDocumentT? fresh = fpdfview.FPDF_LoadMemDocument64(
            pin.AddrOfPinnedObject(), (ulong)bytes.LongLength, null);
        if (fresh is null || fresh.__Instance == IntPtr.Zero)
        {
            pin.Free();
            throw new PdfException(
                (PdfiumError)fpdfview.FPDF_GetLastError(),
                failureMessage ?? "Could not reopen the document after removing watermarks.");
        }

        int pageCount = fpdfview.FPDF_GetPageCount(fresh);

        FpdfDocumentT original = _sources[OriginalSourceId].Handle;
        if (_handle is not null && !ReferenceEquals(_handle, original))
        {
            fpdfview.FPDF_CloseDocument(_handle);
        }

        foreach (Source source in _sources.Values)
        {
            if (source.Owned)
            {
                fpdfview.FPDF_CloseDocument(source.Handle);
                if (source.Pin.IsAllocated)
                {
                    source.Pin.Free();
                }
            }
        }

        _sources.Clear();
        _handle = fresh;
        _sources[OriginalSourceId] = new Source { Handle = fresh, Pin = pin, Owned = true };
        _nextSourceId = OriginalSourceId + 1;

        // SerialiseCurrentHandle already folded any metadata edit into these bytes.
        _handleHasOriginalInfo = true;
        _infoEdited = false;

        var pages = new List<PageRef>(pageCount);
        for (int i = 0; i < pageCount; i++)
        {
            pages.Add(new PageRef(OriginalSourceId, i, GetRotation(fresh, i) & 3));
        }

        _pages = pages;
        _undo.Clear();
        _redo.Clear();
    }

    // --- OCR --------------------------------------------------------------------------------

    /// <summary>Page indices that currently carry no extractable text — the OCR candidates.</summary>
    public IReadOnlyList<int> PagesWithoutText()
    {
        var result = new List<int>();
        for (int i = 0; i < _pages.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(PdfTextExtractor.GetPageText(this, i)))
            {
                result.Add(i);
            }
        }

        return result;
    }

    /// <summary>
    /// Recognise text on every page that has none and bake it in as an invisible layer, so search,
    /// selection, clause detection and extraction all work on a scanned document. Each candidate
    /// page is rasterised at ~300 DPI, run through <paramref name="engine"/>, and the words written
    /// as render-mode-3 text scaled to their image boxes. Like <see cref="RemoveWatermarks"/> this
    /// edits page content, is <b>not undoable</b>, and clears the undo history. Returns a zero
    /// result if every page already has text. Cancelling keeps the pages recognised so far
    /// (re-running picks up the rest).
    /// </summary>
    public OcrResult AddOcrTextLayer(
        IOcrEngine engine,
        IProgress<OcrProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);

        int total = PageCount;
        IReadOnlyList<int> candidates = PagesWithoutText();
        if (candidates.Count == 0)
        {
            return new OcrResult(0, total, 0);
        }

        var renderer = new PageRenderer();
        int wordsAdded = 0;
        int pagesProcessed = 0;

        Locked(() =>
        {
            // The font handle belongs to the current document, so it must be closed *before*
            // AdoptStrippedBytes swaps the handle out and closes the old one — closing it after
            // would be a use-after-free.
            FpdfFontT font = fpdf_edit.FPDFTextLoadStandardFont(Handle, "Helvetica");
            try
            {
                foreach (int pageIndex in candidates)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    PdfSize size = GetPageSize(pageIndex);
                    PdfPoint crop = GetCropOrigin(pageIndex);
                    (int pxW, int pxH) = PdfOcr.PixelSize(size);

                    RenderedPage rendered;
                    IReadOnlyList<OcrWord> words;
                    try
                    {
                        rendered = renderer.Render(this, pageIndex, pxW, pxH, cancellationToken);
                        words = engine.Recognise(rendered, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    int written = 0;
                    FpdfPageT? page = fpdfview.FPDF_LoadPage(Handle, pageIndex);
                    if (page is not null && page.__Instance != IntPtr.Zero)
                    {
                        try
                        {
                            written = PdfOcr.WritePageTextLayer(
                                Handle, page, font, words, size, crop,
                                rendered.PixelWidth, rendered.PixelHeight);
                        }
                        finally
                        {
                            fpdfview.FPDF_ClosePage(page);
                        }
                    }

                    wordsAdded += written;
                    pagesProcessed++;
                    progress?.Report(new OcrProgress(pageIndex + 1, total, written));
                }
            }
            finally
            {
                if (font is not null && font.__Instance != IntPtr.Zero)
                {
                    fpdf_edit.FPDFFontClose(font);
                }
            }

            if (pagesProcessed > 0)
            {
                byte[] bytes = SerialiseCurrentHandle();
                AdoptStrippedBytes(
                    bytes, "Could not reopen the document after adding the OCR text layer.");
            }
        });

        if (pagesProcessed == 0)
        {
            return new OcrResult(0, total - candidates.Count, 0);
        }

        _pageSizes = null;
        _cropOrigins = null;
        _pageLabels = null;
        SetDirty(true);
        Changed?.Invoke(this, EventArgs.Empty);

        return new OcrResult(pagesProcessed, total - candidates.Count, wordsAdded);
    }

    public void Undo()
    {
        HistoryResult result = Locked(() =>
        {
            if (_undo.Count == 0)
            {
                return HistoryResult.None;
            }

            HistoryStep step = Pop(_undo);
            _redo.Add(Snapshot());
            return RestoreTo(step) ? HistoryResult.Pages : HistoryResult.AnnotationsOnly;
        });

        ApplyHistoryResult(result);
    }

    public void Redo()
    {
        HistoryResult result = Locked(() =>
        {
            if (_redo.Count == 0)
            {
                return HistoryResult.None;
            }

            HistoryStep step = Pop(_redo);
            _undo.Add(Snapshot());
            return RestoreTo(step) ? HistoryResult.Pages : HistoryResult.AnnotationsOnly;
        });

        ApplyHistoryResult(result);
    }

    private enum HistoryResult
    {
        None,
        AnnotationsOnly,
        Pages,
    }

    private HistoryStep Snapshot() => new(
        _pages.ToArray(),
        _annotations.Select(list => (IReadOnlyList<PdfAnnotation>)list.ToArray()).ToArray());

    private void PushUndo()
    {
        _undo.Add(Snapshot());
        _redo.Clear();
        while (_undo.Count > MaxHistory)
        {
            _undo.RemoveAt(0);
        }
    }

    /// <returns>True when pages or rotations moved (a full reload is needed), false for an
    /// annotation-only step.</returns>
    private bool RestoreTo(HistoryStep step)
    {
        IReadOnlyList<PageRef> target = step.Pages;
        bool sameStructure = _pages.Count == target.Count;
        bool sameRotation = sameStructure;
        bool sameCrop = sameStructure;
        for (int i = 0; sameStructure && i < target.Count; i++)
        {
            sameStructure = _pages[i].SourceId == target[i].SourceId
                            && _pages[i].SourcePageIndex == target[i].SourcePageIndex;
            sameRotation = sameRotation && sameStructure && _pages[i].Rotation == target[i].Rotation;
            sameCrop = sameCrop && sameStructure && Nullable.Equals(_pages[i].CropBox, target[i].CropBox);
        }

        _pages = target.ToList();
        _annotations = step.Annotations.Select(list => list.ToList()).ToList();

        // A crop change can't be undone in place (FPDFPage_SetCropBox only sets) — rebuild from
        // source, which re-applies the restored crops.
        if (!sameStructure || !sameCrop)
        {
            Rebuild();
            return true;
        }

        if (!sameRotation)
        {
            SyncRotations();
            return true;
        }

        return false;
    }

    private void AfterEdit()
    {
        _pageSizes = null;
        _cropOrigins = null;
        _pageLabels = null;
        SetDirty(true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyHistoryResult(HistoryResult result)
    {
        if (result == HistoryResult.None)
        {
            return;
        }

        SetDirty(true);
        if (result == HistoryResult.Pages)
        {
            _pageSizes = null;
            _cropOrigins = null;
            _pageLabels = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        AnnotationsChanged?.Invoke(this, new AnnotationsChangedEventArgs(-1));
    }

    private static HistoryStep Pop(List<HistoryStep> stack)
    {
        HistoryStep step = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return step;
    }

    private void SetDirty(bool value)
    {
        if (IsDirty == value)
        {
            return;
        }

        IsDirty = value;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    // --- Handle rebuild (caller holds Locked) ------------------------------------------

    private void SyncRotations()
    {
        for (int i = 0; i < _pages.Count; i++)
        {
            SetRotation(Handle, i, _pages[i].Rotation);
        }
    }

    private static void ApplyCropBox(FpdfDocumentT doc, int pageIndex, PdfRect crop)
    {
        FpdfPageT? page = fpdfview.FPDF_LoadPage(doc, pageIndex);
        if (page is null || page.__Instance == IntPtr.Zero)
        {
            return;
        }

        try
        {
            double left = Math.Min(crop.Left, crop.Right), right = Math.Max(crop.Left, crop.Right);
            double bottom = Math.Min(crop.Top, crop.Bottom), top = Math.Max(crop.Top, crop.Bottom);
            fpdf_transformpage.FPDFPageSetCropBox(page, (float)left, (float)bottom, (float)right, (float)top);
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    /// <summary>
    /// Set each given page's CropBox to its visible content bounds plus <paramref name="marginPt"/>
    /// points of white space. Renders the page to find the content — one <see cref="IProgress{T}"/>
    /// tick per page. A single undo step; survives a later structural edit (re-applied by
    /// <see cref="Rebuild"/>). Pages that already fit their content tightly are skipped.
    /// </summary>
    /// <returns>The number of pages whose CropBox was actually changed.</returns>
    public int TrimMargins(
        IReadOnlyList<int> pageIndexes, double marginPt = 6,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        int[] indices = Normalize(pageIndexes);
        if (indices.Length == 0)
        {
            return 0;
        }

        var renderer = new PageRenderer();
        var newCrops = new Dictionary<int, PdfRect>();
        for (int k = 0; k < indices.Length; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int i = indices[k];
            if (DetectTrimmedCropBox(renderer, i, marginPt) is { } crop)
            {
                newCrops[i] = crop;
            }

            progress?.Report(k + 1);
        }

        if (newCrops.Count == 0)
        {
            return 0;
        }

        Locked(() =>
        {
            PushUndo();

            // Never mutate a source page's box (undo can't restore it — FPDFPage_SetCropBox only
            // sets). Rebuild so the live handle is a private copy first.
            if (ReferenceEquals(_handle, _sources[OriginalSourceId].Handle))
            {
                Rebuild();
            }

            foreach ((int i, PdfRect crop) in newCrops)
            {
                _pages[i] = _pages[i] with { CropBox = crop };
                ApplyCropBox(Handle, i, crop);
            }
        });

        AfterEdit();
        return newCrops.Count;
    }

    private PdfRect? DetectTrimmedCropBox(PageRenderer renderer, int pageIndex, double marginPt)
    {
        const double dpi = 120.0;
        double scale = dpi / 72.0;
        PdfSize displayed = GetPageSize(pageIndex); // rotated, current-crop size
        int pw = Math.Max(1, (int)Math.Round(displayed.Width * scale));
        int ph = Math.Max(1, (int)Math.Round(displayed.Height * scale));

        RenderedPage img;
        try
        {
            img = renderer.Render(this, pageIndex, pw, ph);
        }
        catch (PdfException)
        {
            return null;
        }

        if (!TryFindContentBox(img, out int dx0, out int dy0, out int dx1, out int dy1))
        {
            return null; // blank page — leave it alone
        }

        PdfSize unrotated = GetUnrotatedPageSize(pageIndex);
        PdfRotation rotation = GetPageRotation(pageIndex);
        PdfPoint crop = GetCropOrigin(pageIndex);

        // Map the device bbox corners back to unrotated MediaBox-relative page points.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach ((int px, int py) in new[] { (dx0, dy0), (dx1, dy0), (dx1, dy1), (dx0, dy1) })
        {
            PdfPoint p = PdfCoordinates.DeviceToPage(px, py, unrotated, rotation, scale, crop);
            minX = Math.Min(minX, p.X);
            maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y);
            maxY = Math.Max(maxY, p.Y);
        }

        double pageLeft = crop.X, pageBottom = crop.Y;
        double pageRight = crop.X + unrotated.Width, pageTop = crop.Y + unrotated.Height;

        double left = Math.Clamp(minX - marginPt, pageLeft, pageRight);
        double right = Math.Clamp(maxX + marginPt, pageLeft, pageRight);
        double bottom = Math.Clamp(minY - marginPt, pageBottom, pageTop);
        double top = Math.Clamp(maxY + marginPt, pageBottom, pageTop);

        if (right - left < 1 || top - bottom < 1)
        {
            return null;
        }

        // Already tight (content fills > 97% both ways) — nothing to gain.
        if ((right - left) > 0.97 * unrotated.Width && (top - bottom) > 0.97 * unrotated.Height)
        {
            return null;
        }

        return new PdfRect(left, top, right, bottom);
    }

    private static bool TryFindContentBox(RenderedPage img, out int x0, out int y0, out int x1, out int y1)
    {
        const int white = 245; // any channel below this counts as content
        x0 = img.PixelWidth;
        y0 = img.PixelHeight;
        x1 = -1;
        y1 = -1;
        byte[] px = img.Pixels;
        int stride = img.Stride;

        for (int y = 0; y < img.PixelHeight; y++)
        {
            int row = y * stride;
            for (int x = 0; x < img.PixelWidth; x++)
            {
                int o = row + (x * 4);
                if (px[o] < white || px[o + 1] < white || px[o + 2] < white)
                {
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
            }
        }

        return x1 >= x0 && y1 >= y0;
    }

    private void Rebuild()
    {
        FpdfDocumentT fresh = fpdf_edit.FPDF_CreateNewDocument();
        try
        {
            int dest = 0;
            int i = 0;
            while (i < _pages.Count)
            {
                int sourceId = _pages[i].SourceId;
                int j = i;
                while (j < _pages.Count && _pages[j].SourceId == sourceId)
                {
                    j++;
                }

                string range = string.Join(",", Enumerable.Range(i, j - i).Select(k => _pages[k].SourcePageIndex + 1));
                int ok = fpdf_ppo.FPDF_ImportPages(fresh, _sources[sourceId].Handle, range, dest);
                if (ok == 0)
                {
                    throw new PdfException("PDFium could not assemble the edited document.");
                }

                dest += j - i;
                i = j;
            }

            for (int k = 0; k < _pages.Count; k++)
            {
                if (_pages[k].Rotation != 0)
                {
                    SetRotation(fresh, k, _pages[k].Rotation);
                }

                if (_pages[k].CropBox is { } crop)
                {
                    ApplyCropBox(fresh, k, crop);
                }
            }
        }
        catch
        {
            fpdfview.FPDF_CloseDocument(fresh);
            throw;
        }

        FpdfDocumentT? old = _handle;
        _handle = fresh;
        _handleHasOriginalInfo = false; // FPDF_CreateNewDocument has no Info dict; re-added at save time
        if (old is not null && !ReferenceEquals(old, _sources[OriginalSourceId].Handle))
        {
            fpdfview.FPDF_CloseDocument(old);
        }
    }

    private static int GetRotation(FpdfDocumentT doc, int pageIndex)
    {
        FpdfPageT? page = fpdfview.FPDF_LoadPage(doc, pageIndex);
        if (page is null || page.__Instance == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            return GetRotation(page);
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    private static int GetRotation(FpdfPageT page) => fpdf_edit.FPDFPageGetRotation(page);

    private static void SetRotation(FpdfDocumentT doc, int pageIndex, int rotation)
    {
        FpdfPageT? page = fpdfview.FPDF_LoadPage(doc, pageIndex);
        if (page is null || page.__Instance == IntPtr.Zero)
        {
            return;
        }

        try
        {
            fpdf_edit.FPDFPageSetRotation(page, rotation & 3);
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    private static (FpdfDocumentT Handle, GCHandle Pin) DetachSource(PdfDocument source)
    {
        // Take ownership of the freshly loaded source's handle + pinned bytes; neuter the object.
        Source original = source._sources[OriginalSourceId];
        source._handle = null;
        source._disposed = true;
        source._sources.Clear();
        return (original.Handle, original.Pin);
    }

    // --- Saving --------------------------------------------------------------------------

    public byte[] SaveToBytes()
    {
        return Locked(() =>
        {
            // Annotations live only in our model during a session; write them onto the live
            // pages just for this serialisation, then strip them back off so a second save
            // can't double them and the renderer keeps drawing our overlay instead.
            BakeAnnotations();
            try
            {
                byte[] bytes = SerialiseCurrentHandle();
                return _outline is null ? bytes : PdfOutlineWriter.Append(bytes, _outline);
            }
            finally
            {
                UnbakeAnnotations();
            }
        });
    }

    private byte[] SerialiseCurrentHandle()
    {
            using var stream = new MemoryStream();
            PDFiumCore.Delegates.Func_int___IntPtr___IntPtr_uint writeBlock = (_, data, size) =>
            {
                var chunk = new byte[size];
                Marshal.Copy(data, chunk, 0, (int)size);
                stream.Write(chunk, 0, (int)size);
                return 1;
            };

            var fileWrite = new FPDF_FILEWRITE_ { Version = 1, WriteBlock = writeBlock };
            try
            {
                // An encrypted source (e.g. a BSI "licensed copy" PDF) keeps its /Encrypt dict and
                // RC4/AES streams through a plain FPDF_NO_INCREMENTAL save — so the watermark
                // stripper, which edits the content-stream bytes, would see ciphertext and match
                // nothing. FPDF_REMOVE_SECURITY (3) makes PDFium rewrite the whole file with no
                // encryption. There is no owner password to preserve anyway once we're editing.
                uint flags = fpdfview.FPDF_GetSecurityHandlerRevision(Handle) >= 0
                    ? 3u  // FPDF_REMOVE_SECURITY
                    : 2u; // FPDF_NO_INCREMENTAL
                int ok = fpdf_save.FPDF_SaveAsCopy(Handle, fileWrite, flags);
                if (ok == 0)
                {
                    throw new PdfException("PDFium failed to serialise the document.");
                }
            }
            finally
            {
                GC.KeepAlive(writeBlock);
                fileWrite.Dispose();
            }

            byte[] bytes = stream.ToArray();

            // FPDF_SaveAsCopy keeps the original Info dict, but a rebuilt handle has none, and
            // PDFium can't write our metadata edits — so append an incremental Info update.
            if (_infoEdited || !_handleHasOriginalInfo)
            {
                PdfDocumentInfo toWrite = _info with { ModificationDate = PdfDate.Now() };
                bytes = PdfMetadataWriter.AppendInfo(bytes, toWrite);
            }

            return bytes;
    }

    /// <summary>
    /// Runs <paramref name="work"/> with this document's managed annotations (highlights,
    /// comments, ink) temporarily written onto the live pages, then strips them again — the
    /// same bake/unbake dance as <see cref="SaveToBytes"/>, but for rendering (printing /
    /// export) instead of serialising. Serialised through the document lock; re-entrant, so
    /// <paramref name="work"/> may itself call <see cref="Locked{T}"/> (e.g. a page render).
    /// </summary>
    public T WithAnnotationsBaked<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Locked(() =>
        {
            BakeAnnotations();
            try
            {
                return work();
            }
            finally
            {
                UnbakeAnnotations();
            }
        });
    }

    private void BakeAnnotations()
    {
        for (int i = 0; i < _pages.Count; i++)
        {
            FpdfPageT? page = fpdfview.FPDF_LoadPage(Handle, i);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                PdfAnnotationWriter.StripManaged(page);
                if (_annotations[i].Count > 0)
                {
                    PdfAnnotationWriter.Write(Handle, page, _annotations[i], ImageDecoder);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    private void UnbakeAnnotations()
    {
        for (int i = 0; i < _pages.Count; i++)
        {
            FpdfPageT? page = fpdfview.FPDF_LoadPage(Handle, i);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                PdfAnnotationWriter.StripManaged(page);
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    public void SaveAs(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        byte[] bytes = SaveToBytes();

        string temp = fullPath + ".docdr-tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, fullPath, overwrite: true);

        FilePath = fullPath;
        if (_infoEdited || !_handleHasOriginalInfo)
        {
            _info = _info with { ModificationDate = PdfDate.Now() };
        }

        _infoEdited = false;
        SetDirty(false);
    }

    public void Save()
    {
        if (FilePath is null)
        {
            throw new InvalidOperationException("This document has no path; use SaveAs.");
        }

        SaveAs(FilePath);
    }

    // --- Helpers -----------------------------------------------------------------------

    private int[] Normalize(IReadOnlyList<int> pageIndices)
    {
        ArgumentNullException.ThrowIfNull(pageIndices);
        int[] indices = pageIndices.Distinct().OrderBy(i => i).ToArray();
        foreach (int i in indices)
        {
            ValidatePageIndex(i);
        }

        return indices;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed && _handle is null)
            {
                return;
            }

            _disposed = true;

            FpdfDocumentT? live = _handle;
            _handle = null;

            FpdfDocumentT original = _sources.TryGetValue(OriginalSourceId, out Source? s0) ? s0.Handle : null!;
            if (live is not null && !ReferenceEquals(live, original))
            {
                fpdfview.FPDF_CloseDocument(live);
            }

            foreach (Source source in _sources.Values)
            {
                if (source.Owned)
                {
                    fpdfview.FPDF_CloseDocument(source.Handle);
                    if (source.Pin.IsAllocated)
                    {
                        source.Pin.Free();
                    }
                }
            }

            _sources.Clear();
            _undo.Clear();
            _redo.Clear();
        }
    }
}

/// <summary>The result of <see cref="PdfDocument.Merge"/>: the new document and the 0-based page
/// each input file's pages begin at (same order as the input paths).</summary>
public sealed record MergeResult(PdfDocument Document, IReadOnlyList<int> SourceStartPages);
