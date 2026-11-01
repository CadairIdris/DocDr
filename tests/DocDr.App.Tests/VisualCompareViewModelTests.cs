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

    [Fact]
    public void Text_change_rows_say_what_where()
    {
        var change = new TextChange(4, TextChangeKind.Changed,
            [new RedlineSegment(RedlineKind.Equal, "taken as"), new RedlineSegment(RedlineKind.Deleted, "1.5"), new RedlineSegment(RedlineKind.Inserted, "1.4")],
            "1.5", "1.4", 11, 12, [], [], "6.2.4");
        var row = new TextChangeItemViewModel(change);
        Assert.Equal("5. Changed · 6.2.4 · p. 12 → 13", row.Header);
        Assert.Equal("5. Changed · 6.2.4 · p. 12 → 13: taken as 1.5 1.4", row.AutomationName);
    }

    [Theory]
    [InlineData(ClauseChangeKind.Modified, "6.2", "6.2", 0.823, "82 %", "6.2")]
    [InlineData(ClauseChangeKind.Renumbered, "6.2", "6.3", 1.0, "renumbered", "6.2 → 6.3")]
    [InlineData(ClauseChangeKind.Added, null, "B", 0.0, "added", "Annex B")]
    [InlineData(ClauseChangeKind.Removed, "", null, 0.0, "removed", "Front matter")]
    public void Clause_rows_show_status_and_number(ClauseChangeKind kind, string? oldNumber, string? newNumber, double similarity, string status, string number)
    {
        var clause = new ClauseComparison(kind, oldNumber, newNumber, oldNumber is null ? null : "Shear", newNumber is null ? null : "Shear",
            oldNumber is null ? null : 3, newNumber is null ? null : 4, 2, similarity, 0, 0, []);
        var row = new ClauseRowViewModel(clause);
        Assert.Equal(status, row.Status);
        Assert.Equal(number, row.NumberLabel);
        Assert.Equal(12, row.Indent.Left);
    }
}
