namespace DocDr.Pdf;

/// <summary>One match found while searching a folder of PDFs.</summary>
public sealed record FolderSearchHit(int PageIndex, int CharStart, int CharCount, string Snippet);

/// <summary>
/// Reported once per file as <see cref="PdfFolderSearch.Search"/> works through the list — either
/// that file's hits (possibly empty) or the reason it couldn't be searched. Running totals are
/// included so the UI doesn't need to keep its own counters in step with a background thread.
/// </summary>
public sealed record FolderSearchProgress(
    string FilePath,
    IReadOnlyList<FolderSearchHit> Hits,
    string? Error,
    int FilesScanned,
    int TotalFiles,
    int TotalMatches);

/// <summary>
/// Full-text search across many PDF files.
/// <para>
/// Every PDFium call is still serialised through <see cref="PdfiumLibrary.SyncRoot"/> (via
/// <see cref="PdfDocument"/>), so running several files at once cannot parallelise the PDFium work
/// itself. What it does overlap is each file's disk read and managed-side bookkeeping with the
/// *previous* file's PDFium work — otherwise pure wall-clock waste while one thread sits in
/// <c>File.ReadAllBytes</c>. A worker per file beyond a small handful just queues up on the same
/// lock, so the degree of parallelism is capped low rather than scaled to core count.
/// </para>
/// </summary>
public static class PdfFolderSearch
{
    private const int MaxParallelism = 4;

    /// <summary>
    /// Search every file in <paramref name="filePaths"/> for <paramref name="term"/>, reporting one
    /// <see cref="FolderSearchProgress"/> per file as it completes (in completion order, not input
    /// order). Runs synchronously on the calling thread — callers should invoke it via
    /// <c>Task.Run</c>, as with every other bulk PDF operation in DocDr.
    /// </summary>
    public static void Search(
        IReadOnlyList<string> filePaths,
        string term,
        PdfSearchOptions options,
        IProgress<FolderSearchProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        ArgumentNullException.ThrowIfNull(progress);
        if (string.IsNullOrWhiteSpace(term) || filePaths.Count == 0)
        {
            return;
        }

        int filesScanned = 0;
        int totalMatches = 0;
        int degree = Math.Max(1, Math.Min(MaxParallelism, Environment.ProcessorCount));

        Parallel.ForEach(
            filePaths,
            new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellationToken },
            path =>
            {
                IReadOnlyList<FolderSearchHit> hits = Array.Empty<FolderSearchHit>();
                string? error = null;
                try
                {
                    using PdfDocument document = PdfDocument.Load(path);
                    var search = new PdfSearch(document);
                    IReadOnlyList<SearchHit> found =
                        search.FindAll(term, options, cancellationToken, includeSnippets: true);
                    if (found.Count > 0)
                    {
                        var list = new List<FolderSearchHit>(found.Count);
                        foreach (SearchHit hit in found)
                        {
                            list.Add(new FolderSearchHit(hit.PageIndex, hit.CharStart, hit.CharCount, hit.Snippet));
                        }

                        hits = list;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
                {
                    error = ex.Message;
                }

                int scanned = Interlocked.Increment(ref filesScanned);
                int matches = hits.Count > 0
                    ? Interlocked.Add(ref totalMatches, hits.Count)
                    : Volatile.Read(ref totalMatches);
                progress.Report(new FolderSearchProgress(path, hits, error, scanned, filePaths.Count, matches));
            });
    }
}
