using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>
/// Process-wide PDFium lifecycle. PDFium must be initialised once before any document is
/// loaded and is not per-thread. <see cref="EnsureInitialized"/> is safe to call from
/// anywhere, any number of times.
/// </summary>
public static class PdfiumLibrary
{
    private static readonly object Gate = new();
    private static bool _initialized;

    /// <summary>
    /// Process-wide serialization root for every PDFium call. The <c>pdfium-binaries</c> build is
    /// not safe for concurrent use even across different documents (shared font cache and
    /// allocator state), so all work — regardless of which <see cref="PdfDocument"/> it targets —
    /// funnels through this single lock. See the Stage 1 threading constraint in the spec.
    /// </summary>
    internal static readonly object SyncRoot = new();

    /// <summary>Idempotently initialise the native PDFium library.</summary>
    public static void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized))
        {
            return;
        }

        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            fpdfview.FPDF_InitLibrary();
            Volatile.Write(ref _initialized, true);
        }
    }

    /// <summary>
    /// Tear down PDFium. Call only at process shutdown, once every <see cref="PdfDocument"/>
    /// has been disposed. Provided mainly for symmetry / leak checkers.
    /// </summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (!_initialized)
            {
                return;
            }

            fpdfview.FPDF_DestroyLibrary();
            _initialized = false;
        }
    }
}
