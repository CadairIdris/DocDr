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

    private readonly record struct PageRef(int SourceId, int SourcePageIndex, int Rotation);

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
    private bool _disposed;

    private PdfDocumentInfo _info = PdfDocumentInfo.Empty;
    private bool _infoEdited;
    private bool _handleHasOriginalInfo = true;

    private PdfDocument(FpdfDocumentT handle, GCHandle pin, string? path, int pageCount)
    {
        _handle = handle;
        _sources[OriginalSourceId] = new Source { Handle = handle, Pin = pin, Owned = true };

        _pages = new List<PageRef>(pageCount);
        _annotations = new List<List<PdfAnnotation>>(pageCount);
        for (int i = 0; i < pageCount; i++)
        {
            _pages.Add(new PageRef(OriginalSourceId, i, GetRotation(handle, i) & 3));

            // Read any existing highlights / notes into our model, then strip them off the
            // original handle so PDFium's renderer (annotations flag on) doesn't draw them
            // under our own overlay. Stripping the original == stripping _sources[0], so every
            // later Rebuild() imports a clean base.
            List<PdfAnnotation> onPage = PdfAnnotations.ReadLocked(handle, i).ToList();
            _annotations.Add(onPage);
            if (onPage.Count > 0)
            {
                StripManagedAnnotations(handle, i);
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

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised (on the calling thread) after any edit, undo, or redo changes the pages.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when <see cref="IsDirty"/> changes.</summary>
    public event EventHandler? DirtyChanged;

    /// <summary>Raised when <see cref="UpdateInfo"/> changes the document metadata.</summary>
    public event EventHandler? MetadataChanged;

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

    private static void StripManagedAnnotations(FpdfDocumentT doc, int pageIndex)
    {
        FpdfPageT? page = fpdfview.FPDF_LoadPage(doc, pageIndex);
        if (page is null || page.__Instance == IntPtr.Zero)
        {
            return;
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
        SetDirty(true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replace every source and the live handle with a freshly loaded copy of <paramref name="bytes"/>.
    /// Caller holds <see cref="Locked(Action)"/>. The annotation model is index-aligned with the
    /// (unchanged) page order, so it is kept as-is.</summary>
    private void AdoptStrippedBytes(byte[] bytes)
    {
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        FpdfDocumentT? fresh = fpdfview.FPDF_LoadMemDocument64(
            pin.AddrOfPinnedObject(), (ulong)bytes.LongLength, null);
        if (fresh is null || fresh.__Instance == IntPtr.Zero)
        {
            pin.Free();
            throw new PdfException(
                (PdfiumError)fpdfview.FPDF_GetLastError(),
                "Could not reopen the document after removing watermarks.");
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
        for (int i = 0; sameStructure && i < target.Count; i++)
        {
            sameStructure = _pages[i].SourceId == target[i].SourceId
                            && _pages[i].SourcePageIndex == target[i].SourcePageIndex;
            sameRotation = sameRotation && sameStructure && _pages[i].Rotation == target[i].Rotation;
        }

        _pages = target.ToList();
        _annotations = step.Annotations.Select(list => list.ToList()).ToList();

        if (!sameStructure)
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
            return fpdf_edit.FPDFPageGetRotation(page);
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

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
                return SerialiseCurrentHandle();
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
                int ok = fpdf_save.FPDF_SaveAsCopy(Handle, fileWrite, 2 /* FPDF_NO_INCREMENTAL */);
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
                    PdfAnnotationWriter.Write(page, _annotations[i]);
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
