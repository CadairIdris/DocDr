namespace DocDr.App.Services;

/// <summary>The page-rasterisation queue as the view-models see it: enqueue a request, or drop the
/// stale backlog. <see cref="BackgroundRenderQueue"/> is the only real implementation; tests use a
/// fake to assert what a view-model asks to be rendered.</summary>
public interface IRenderQueue
{
    void Enqueue(RenderRequest request);

    void Clear();
}
