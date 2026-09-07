namespace DocDr.App.ViewModels;

/// <summary>The armed "drop a shape" tool, if any. Mutually exclusive with the comment / highlighter tools.</summary>
public enum ShapeTool
{
    None,
    TextBox,
    Callout,
    Cloud,
}
