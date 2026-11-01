using DocDr.App.ViewModels;
using DocDr.Pdf;

namespace DocDr.App.Tests;

public sealed class VisualCompareViewModelTests
{
    [Theory]
    [InlineData(2, 2, PageCompareStatus.Same, 0, "Page 3", "no change")]
    [InlineData(2, 3, PageCompareStatus.Changed, 1, "Page 3 ↔ 4", "1 change")]
    [InlineData(4, 4, PageCompareStatus.Changed, 5, "Page 5", "5 changes")]
    [InlineData(6, null, PageCompareStatus.OnlyInOld, 0, "Page 7", "removed page")]
    [InlineData(null, 0, PageCompareStatus.OnlyInNew, 0, "Page 1 (new)", "added page")]
    public void Overview_rows_read_plainly(int? oldPage, int? newPage, PageCompareStatus status, int regions, string label, string text)
    {
        var row = new ComparePageItemViewModel(new PageCompareSummary(oldPage, newPage, status, regions));
        Assert.Equal(label, row.Label);
        Assert.Equal(text, row.StatusText);
        Assert.Equal(status != PageCompareStatus.Same, row.IsChanged);
    }

    [Fact]
    public void A_pending_row_is_not_yet_a_change()
    {
        var row = new ComparePageItemViewModel(new PageCompareSummary(0, 0, PageCompareStatus.Same, 0), isPending: true);
        Assert.Equal("…", row.StatusText);
        Assert.False(row.IsChanged);
    }
}
