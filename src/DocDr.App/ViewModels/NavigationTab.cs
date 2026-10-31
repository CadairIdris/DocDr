namespace DocDr.App.ViewModels;

/// <summary>Which section of the navigation panel is showing.</summary>
public enum NavigationTab
{
    Pages,
    Bookmarks,
    Clauses,

    /// <summary>Notes and noted highlights, with reply threads and review status.</summary>
    Comments,

    /// <summary>Markup: ink, shapes, text boxes, clouds, images, plain highlights.</summary>
    Annotations,
}
