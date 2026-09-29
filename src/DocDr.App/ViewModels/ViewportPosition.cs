namespace DocDr.App.ViewModels;

/// <summary>
/// Where a continuous-scroll pane is looking, independent of zoom: the page at the top edge of
/// the viewport, how far down that page the edge sits (0 = page top, 1 = page bottom), and the
/// horizontal scroll as a fraction of its range. Two panes showing different documents — or the
/// same one at different zooms — can be lined up by exchanging this.
/// </summary>
public readonly record struct ViewportPosition(int PageIndex, double Fraction, double HorizontalFraction)
{
    /// <summary>Close enough that re-applying one on top of the other would only jitter.</summary>
    public bool IsNear(ViewportPosition other) =>
        PageIndex == other.PageIndex
        && Math.Abs(Fraction - other.Fraction) < 0.01
        && Math.Abs(HorizontalFraction - other.HorizontalFraction) < 0.02;
}
