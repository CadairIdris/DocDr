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

    /// <summary>Measure: click two points.</summary>
    MeasureDistance,

    /// <summary>Measure: click points along a path; double-click / Enter to finish.</summary>
    MeasurePerimeter,

    /// <summary>Measure: click the corners of a region; double-click / Enter to finish.</summary>
    MeasureArea,

    /// <summary>Set the page scale: click two points on a known length.</summary>
    Calibrate,
}

/// <summary>Which corner handle of a selected shape box is being dragged to resize it.</summary>
public enum BoxHandle
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}
