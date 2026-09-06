using System.Windows;

namespace DocDr.App.ViewModels;

/// <summary>
/// One <c>/Link</c> region projected into a page slot's DIP space. Either <see cref="TargetPageIndex"/>
/// (an in-document jump) or <see cref="Uri"/> (an external link) is set.
/// </summary>
public sealed record LinkVisual(Rect Bounds, int? TargetPageIndex, string? Uri)
{
    public string Tooltip => Uri
        ?? (TargetPageIndex is int page ? $"Go to page {page + 1}" : "Link");

    public bool IsInternal => TargetPageIndex is not null;
}
