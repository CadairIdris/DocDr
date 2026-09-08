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
}

/// <summary>Which corner handle of a selected shape box is being dragged to resize it.</summary>
public enum BoxHandle
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}
