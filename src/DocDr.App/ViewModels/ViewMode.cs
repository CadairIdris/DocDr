namespace DocDr.App.ViewModels;

/// <summary>How a pane lays its pages out.</summary>
public enum ViewMode
{
    /// <summary>One page at a time.</summary>
    SinglePage,

    /// <summary>All pages stacked vertically in a single scroll.</summary>
    Continuous,

    /// <summary>Pages wrapped into rows; the column count auto-fits the pane width.</summary>
    Grid,

    /// <summary>One two-page spread at a time, sized to fill the viewport. Used by read mode.</summary>
    TwoPage,
}
