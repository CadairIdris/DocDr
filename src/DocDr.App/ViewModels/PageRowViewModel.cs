using System.Collections.Generic;
using System.Linq;

namespace DocDr.App.ViewModels;

/// <summary>
/// One row of the grid view: a left-to-right run of page slots. The slots are the very same
/// <see cref="PageSlotViewModel"/> instances held by the pane's flat <c>Pages</c> list, so
/// rendering, highlights and layout continue to work unchanged.
/// </summary>
public sealed class PageRowViewModel
{
    public PageRowViewModel(int rowIndex, IReadOnlyList<PageSlotViewModel> slots)
    {
        RowIndex = rowIndex;
        Slots = slots;
    }

    public int RowIndex { get; }

    public IReadOnlyList<PageSlotViewModel> Slots { get; }

    /// <summary>Tallest slot in the row, in DIP at the current layout.</summary>
    public double RowHeight => Slots.Count == 0 ? 0 : Slots.Max(s => s.LayoutHeight);
}
