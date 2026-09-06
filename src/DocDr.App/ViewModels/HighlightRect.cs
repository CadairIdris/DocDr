using System.Windows;

namespace DocDr.App.ViewModels;

/// <summary>A search-hit rectangle in a page slot's DIP space, plus whether it is the active match.</summary>
public sealed record HighlightRect(Rect Bounds, bool IsActive);
