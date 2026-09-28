using DocDr.Pdf;

namespace DocDr.App.Services;

/// <summary>The page-rasterisation queue as the view-models see it: enqueue a request, or drop the
/// stale backlog. <see cref="BackgroundRenderQueue"/> is the only real implementation; tests use a
/// fake to assert what a view-model asks to be rendered.</summary>
public interface IRenderQueue
{
    void Enqueue(RenderRequest request);

    void Clear();

    /// <summary>Drop queued-but-not-started requests matching <paramref name="predicate"/> (e.g. pages
    /// that have scrolled out of view). A render already in progress is unaffected. The predicate
    /// runs synchronously on the caller's thread.</summary>
    void RemoveWhere(Func<RenderRequest, bool> predicate);

    /// <summary>An already-rendered bitmap for this exact page / pixel size / scale, or null —
    /// checked synchronously, so a cache hit never waits behind the worker's current render.</summary>
    System.Windows.Media.ImageSource? TryGetCached(PdfDocument document, int pageIndex, int pixelWidth, int pixelHeight, double deviceScale);
}
