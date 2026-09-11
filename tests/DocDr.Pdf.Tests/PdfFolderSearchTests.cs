namespace DocDr.Pdf.Tests;

public sealed class PdfFolderSearchTests
{
    [Fact]
    public void Search_reports_hits_per_file_and_skips_files_with_no_match()
    {
        using var ws = new TempWorkspace();
        string a = TestPdfBuilder.WritePdf(ws.Path("a.pdf"), ["alpha needle beta"]);
        string b = TestPdfBuilder.WritePdf(ws.Path("b.pdf"), ["nothing here", "gamma needle delta needle"]);
        string c = TestPdfBuilder.WritePdf(ws.Path("c.pdf"), ["no match in this one"]);

        var reports = new System.Collections.Concurrent.ConcurrentBag<FolderSearchProgress>();
        var progress = new SyncProgress<FolderSearchProgress>(reports.Add);

        PdfFolderSearch.Search([a, b, c], "needle", PdfSearchOptions.None, progress);

        Assert.Equal(3, reports.Count);
        Assert.All(reports, r => Assert.Equal(3, r.TotalFiles));

        var byPath = reports.ToDictionary(r => r.FilePath);
        Assert.Single(byPath[a].Hits);
        Assert.Equal(2, byPath[b].Hits.Count);
        Assert.Empty(byPath[c].Hits);
        Assert.Null(byPath[a].Error);

        FolderSearchProgress last = reports.OrderBy(r => r.FilesScanned).Last();
        Assert.Equal(3, last.TotalMatches);
    }

    [Fact]
    public void Search_captures_a_snippet_of_context_around_each_match()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("s.pdf"), ["some words before needle and words after"]);

        FolderSearchProgress? result = null;
        var progress = new SyncProgress<FolderSearchProgress>(p => result = p);

        PdfFolderSearch.Search([path], "needle", PdfSearchOptions.None, progress);

        Assert.NotNull(result);
        Assert.Single(result!.Hits);
        Assert.Contains("needle", result.Hits[0].Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Search_reports_an_error_for_a_file_that_cannot_be_opened()
    {
        using var ws = new TempWorkspace();
        string bad = ws.Path("bad.pdf");
        File.WriteAllText(bad, "not a pdf");

        FolderSearchProgress? result = null;
        var progress = new SyncProgress<FolderSearchProgress>(p => result = p);

        PdfFolderSearch.Search([bad], "needle", PdfSearchOptions.None, progress);

        Assert.NotNull(result);
        Assert.Empty(result!.Hits);
        Assert.NotNull(result.Error);
    }

    /// <summary>An <see cref="IProgress{T}"/> that invokes the callback synchronously on the
    /// reporting thread — <see cref="System.Progress{T}"/> requires a captured
    /// <see cref="SynchronizationContext"/>, which a plain test thread doesn't have.</summary>
    private sealed class SyncProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
