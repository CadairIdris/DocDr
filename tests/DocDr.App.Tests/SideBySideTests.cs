using System.Linq;
using System.Windows.Threading;
using DocDr.App.Services;
using DocDr.App.ViewModels;
using DocDr.Pdf;

namespace DocDr.App.Tests;

/// <summary>Docking tabs side by side: which side a tab lives on, which side the toolbar acts on,
/// and what happens as tabs move and close.</summary>
public sealed class SideBySideTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly BackgroundRenderQueue _queue;
    private readonly MainViewModel _main;

    public SideBySideTests()
    {
        _queue = new BackgroundRenderQueue(new PageImageService(new PageRenderer()), Dispatcher.CurrentDispatcher);
        _main = new MainViewModel(_queue, new PageImageService(new PageRenderer()), new AppSettings());
    }

    public void Dispose()
    {
        _main.Dispose(); // disposes the tabs (and their documents) and the queue
        _dir.Dispose();
    }

    private DocumentTabViewModel Open(string name, int pages = 3)
    {
        string[] lines = Enumerable.Range(1, pages).Select(i => $"{name} page {i}").ToArray();
        _main.AddDocumentTab(PdfDocument.Load(TestPdfBuilder.WritePdf(_dir.File(name + ".pdf"), lines)), name);
        return _main.SelectedTab!;
    }

    [StaFact]
    public void Tabs_open_on_the_left_and_nothing_is_side_by_side_at_first()
    {
        DocumentTabViewModel a = Open("a");
        DocumentTabViewModel b = Open("b");

        Assert.Equal(new[] { a, b }, _main.LeftGroup.Tabs);
        Assert.False(_main.IsSideBySide);
        Assert.Same(b, _main.SelectedTab);
    }

    [StaFact]
    public void The_only_open_tab_cannot_be_split_off_on_its_own()
    {
        DocumentTabViewModel a = Open("a");

        Assert.False(_main.CanMoveTab(a, _main.RightGroup));
        _main.MoveTabToOtherSide(a);
        Assert.False(_main.IsSideBySide);
    }

    [StaFact]
    public void Moving_a_tab_across_docks_it_on_the_right_and_makes_that_side_active()
    {
        DocumentTabViewModel a = Open("a");
        DocumentTabViewModel b = Open("b");

        _main.MoveTab(b, _main.RightGroup);

        Assert.True(_main.IsSideBySide);
        Assert.Equal(new[] { a }, _main.LeftGroup.Tabs);
        Assert.Equal(new[] { b }, _main.RightGroup.Tabs);
        Assert.Same(a, _main.LeftGroup.SelectedTab); // the left side falls back to its neighbour
        Assert.Same(_main.RightGroup, _main.ActiveGroup);
        Assert.Same(b, _main.SelectedTab);             // …and the toolbar follows the moved tab
        Assert.Equal(2, _main.Tabs.Count);             // still one flat list of every open tab
    }

    [StaFact]
    public void The_toolbar_follows_whichever_side_is_active()
    {
        DocumentTabViewModel a = Open("a");
        DocumentTabViewModel b = Open("b");
        _main.MoveTab(b, _main.RightGroup);

        _main.ActivateGroup(_main.LeftGroup);
        Assert.Same(a, _main.SelectedTab);

        _main.ActivateGroup(_main.RightGroup);
        Assert.Same(b, _main.SelectedTab);

        _main.SelectedTab = a; // selecting a tab activates its side
        Assert.Same(_main.LeftGroup, _main.ActiveGroup);
    }

    [StaFact]
    public void New_documents_open_on_the_active_side()
    {
        Open("a");
        DocumentTabViewModel b = Open("b");
        _main.MoveTab(b, _main.RightGroup);

        DocumentTabViewModel c = Open("c");

        Assert.Contains(c, _main.RightGroup.Tabs);
    }

    [StaFact]
    public void Moving_the_last_right_hand_tab_back_ends_side_by_side()
    {
        Open("a");
        DocumentTabViewModel b = Open("b");
        _main.MoveTab(b, _main.RightGroup);

        _main.MoveTabToOtherSide(b);

        Assert.False(_main.IsSideBySide);
        Assert.Contains(b, _main.LeftGroup.Tabs);
        Assert.Same(_main.LeftGroup, _main.ActiveGroup);
    }

    [StaFact]
    public void Closing_the_right_sides_last_tab_ends_side_by_side()
    {
        DocumentTabViewModel a = Open("a");
        DocumentTabViewModel b = Open("b");
        _main.MoveTab(b, _main.RightGroup);

        _main.CloseTabCommand.Execute(b);

        Assert.False(_main.IsSideBySide);
        Assert.Same(_main.LeftGroup, _main.ActiveGroup);
        Assert.Same(a, _main.SelectedTab);
        Assert.Equal(new[] { a }, _main.Tabs);
    }

    [StaFact]
    public void Emptying_the_left_side_slides_the_right_side_over()
    {
        DocumentTabViewModel a = Open("a");
        DocumentTabViewModel b = Open("b");
        DocumentTabViewModel c = Open("c");
        _main.MoveTab(b, _main.RightGroup);
        _main.MoveTab(c, _main.RightGroup);

        _main.CloseTabCommand.Execute(a); // the left side's only tab

        Assert.False(_main.IsSideBySide);
        Assert.Equal(new[] { b, c }, _main.LeftGroup.Tabs);
        Assert.Same(c, _main.SelectedTab); // the right side's selection carries over
    }

    [StaFact]
    public void Linking_scrolling_pairs_the_two_visible_documents_only_while_side_by_side()
    {
        DocumentTabViewModel a = Open("a", pages: 5);
        DocumentTabViewModel b = Open("b", pages: 5);

        _main.IsScrollLinked = true;
        Assert.Null(_main.ActiveScrollLink); // nothing docked yet — the setting just waits

        _main.MoveTab(b, _main.RightGroup);
        Assert.NotNull(_main.ActiveScrollLink);
        Assert.Same(a.LeftPane, _main.ActiveScrollLink!.A);
        Assert.Same(b.LeftPane, _main.ActiveScrollLink.B);

        _main.MoveTabToOtherSide(b);
        Assert.Null(_main.ActiveScrollLink);
    }
}
