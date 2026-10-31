namespace DocDr.App.ViewModels;

/// <summary>The armed "drop a shape" tool, if any. Mutually exclusive with the comment / highlighter tools.</summary>
public enum ShapeTool
{
    None,
    TextBox,
    Callout,
    Cloud,
    Rectangle,
    Ellipse,
    Line,
    Arrow,

    /// <summary>Click to drop a tick (or drag to size one). Stays armed for ticking item after item.</summary>
    Tick,
}

/// <summary>Which corner handle of a selected shape box is being dragged to resize it.</summary>
public enum BoxHandle
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}
