using System.Globalization;
using System.Runtime.InteropServices;
using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>Options for <see cref="PdfDocument.ExtractPages"/>.</summary>
public sealed record PdfExtractOptions
{
    /// <summary>Burn DocDr's highlights, notes, shapes and measurements into the extracted pages
    /// (as real PDF annotations, like a save). Default true.</summary>
    public bool IncludeAnnotations { get; init; } = true;

    /// <summary>Carry over the bookmarks that point into the extracted pages, re-targeted to their new
    /// page numbers. Default true.</summary>
    public bool IncludeBookmarks { get; init; } = true;

    /// <summary>Title for the new file's metadata; null keeps the source's.</summary>
    public string? Title { get; init; }
}

public sealed partial class PdfDocument
{
    /// <summary>
    /// A new PDF holding just <paramref name="pageIndices"/> (0-based, in document order, duplicates
    /// ignored) of the document <em>as it is now</em> — page edits, rotations and crops included.
    /// Built like <see cref="Rebuild"/>: a fresh <c>FPDF_CreateNewDocument</c> + <c>FPDF_ImportPages</c>
    /// (the only destination this PDFium build accepts) from the live handle, so the source is never
    /// modified. Annotations are baked onto <em>only</em> the extracted pages for the import (not the
    /// whole book) and stripped again. The result is never encrypted: PDFium has already decrypted
    /// the source pages it imports, which is what makes a licensed copy's single page shareable.
    /// </summary>
    public byte[] ExtractPages(IReadOnlyList<int> pageIndices, PdfExtractOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pageIndices);
        options ??= new PdfExtractOptions();
        int[] pages = [.. pageIndices.Distinct().Order()];
        if (pages.Length == 0)
        {
            throw new ArgumentException("Choose at least one page to extract.", nameof(pageIndices));
        }

        foreach (int p in pages)
        {
            ValidatePageIndex(p);
        }

        return Locked(() =>
        {
            if (options.IncludeAnnotations)
            {
                BakeAnnotations(pages);
            }

            byte[] bytes;
            FpdfDocumentT dest = fpdf_edit.FPDF_CreateNewDocument();
            try
            {
                if (dest is null || dest.__Instance == IntPtr.Zero)
                {
                    throw new PdfException("PDFium could not create a new document.");
                }

                if (fpdf_ppo.FPDF_ImportPages(dest, Handle, PdfPageRange.Format(pages), 0) == 0)
                {
                    throw new PdfException("PDFium could not copy those pages.");
                }

                bytes = SaveHandle(dest);
            }
            finally
            {
                if (dest is not null && dest.__Instance != IntPtr.Zero)
                {
                    fpdfview.FPDF_CloseDocument(dest);
                }

                if (options.IncludeAnnotations)
                {
                    UnbakeAnnotations(pages);
                }
            }

            // A fresh document has no Info dict: carry the source's (author, subject…) with the title.
            PdfDocumentInfo info = _info with
            {
                Title = options.Title ?? _info.Title,
                ModificationDate = PdfDate.Now(),
            };
            bytes = PdfMetadataWriter.AppendInfo(bytes, info);

            if (options.IncludeBookmarks)
            {
                var newIndex = new Dictionary<int, int>(pages.Length);
                for (int i = 0; i < pages.Length; i++)
                {
                    newIndex[pages[i]] = i;
                }

                IReadOnlyList<PdfBookmark> outline = SliceOutline(GetOutline(), newIndex);
                if (outline.Count > 0)
                {
                    bytes = PdfOutlineWriter.Append(bytes, outline);
                }
            }

            return bytes;
        });
    }

    /// <summary>
    /// The bookmarks that point into the extracted pages, re-targeted via <paramref name="newIndex"/>
    /// (old page → new page). A bookmark outside the range is dropped but its children that fall
    /// inside are kept, promoted to its place — so extracting clause 8.4 from inside chapter 8 still
    /// gets 8.4's sub-clauses.
    /// </summary>
    internal static IReadOnlyList<PdfBookmark> SliceOutline(IReadOnlyList<PdfBookmark> tree, IReadOnlyDictionary<int, int> newIndex)
    {
        var result = new List<PdfBookmark>();
        foreach (PdfBookmark node in tree)
        {
            IReadOnlyList<PdfBookmark> children = SliceOutline(node.Children, newIndex);
            if (node.PageIndex is int page && newIndex.TryGetValue(page, out int moved))
            {
                result.Add(new PdfBookmark(node.Title, moved, children));
            }
            else
            {
                result.AddRange(children);
            }
        }

        return result;
    }

    private void BakeAnnotations(IReadOnlyList<int> pages)
    {
        foreach (int i in pages)
        {
            if (_annotations[i].Count == 0)
            {
                continue;
            }

            FpdfPageT? page = fpdfview.FPDF_LoadPage(Handle, i);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                PdfAnnotationWriter.StripManaged(page);
                PdfAnnotationWriter.Write(Handle, page, _annotations[i], ImageDecoder);
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    private void UnbakeAnnotations(IReadOnlyList<int> pages)
    {
        foreach (int i in pages)
        {
            if (_annotations[i].Count == 0)
            {
                continue;
            }

            FpdfPageT? page = fpdfview.FPDF_LoadPage(Handle, i);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                PdfAnnotationWriter.StripManaged(page);
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>Serialise a document PDFium built in memory (no encryption, full rewrite).</summary>
    private static byte[] SaveHandle(FpdfDocumentT doc)
    {
        using var stream = new MemoryStream();
        PDFiumCore.Delegates.Func_int___IntPtr___IntPtr_uint writeBlock = (_, data, size) =>
        {
            var chunk = new byte[size];
            Marshal.Copy(data, chunk, 0, (int)size);
            stream.Write(chunk, 0, (int)size);
            return 1;
        };

        var fileWrite = new FPDF_FILEWRITE_ { Version = 1, WriteBlock = writeBlock };
        try
        {
            if (fpdf_save.FPDF_SaveAsCopy(doc, fileWrite, 2u /* FPDF_NO_INCREMENTAL */) == 0)
            {
                throw new PdfException("PDFium failed to serialise the extracted pages.");
            }
        }
        finally
        {
            GC.KeepAlive(writeBlock);
            fileWrite.Dispose();
        }

        return stream.ToArray();
    }
}

/// <summary>Page ranges as people type them ("142, 150-152") ↔ 0-based page indices.</summary>
public static class PdfPageRange
{
    /// <summary>
    /// Parse a 1-based list like "3, 7-9, 12" into sorted, distinct 0-based indices. Spaces are
    /// ignored; "–" / "—" work as dashes; a reversed range ("9-7") is accepted. Returns false with
    /// a message for anything else, or for a page outside 1…<paramref name="pageCount"/>.
    /// </summary>
    public static bool TryParse(string? text, int pageCount, out IReadOnlyList<int> pages, out string error)
    {
        pages = [];
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Enter the pages to extract, e.g. 142 or 150-152.";
            return false;
        }

        var result = new SortedSet<int>();
        foreach (string rawPart in text.Replace('–', '-').Replace('—', '-').Split(new[] { ',', ';' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string part = rawPart.Replace(" ", string.Empty);
            string[] ends = part.Split('-');
            if (ends.Length is < 1 or > 2
                || !int.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out int from)
                || (ends.Length == 2 && !int.TryParse(ends[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            {
                error = $"\"{rawPart}\" isn't a page or a range like 150-152.";
                return false;
            }

            int to = ends.Length == 2 ? int.Parse(ends[1], CultureInfo.InvariantCulture) : from;
            (from, to) = from <= to ? (from, to) : (to, from);
            if (from < 1 || to > pageCount)
            {
                error = $"Pages run from 1 to {pageCount}.";
                return false;
            }

            for (int p = from; p <= to; p++)
            {
                result.Add(p - 1);
            }
        }

        if (result.Count == 0)
        {
            error = "Enter the pages to extract, e.g. 142 or 150-152.";
            return false;
        }

        pages = [.. result];
        return true;
    }

    /// <summary>0-based indices → a compact 1-based range string ("1,3-5"), as PDFium's import takes.</summary>
    public static string Format(IEnumerable<int> pageIndices)
    {
        int[] sorted = [.. pageIndices.Distinct().Order()];
        var parts = new List<string>();
        for (int i = 0; i < sorted.Length;)
        {
            int j = i;
            while (j + 1 < sorted.Length && sorted[j + 1] == sorted[j] + 1)
            {
                j++;
            }

            parts.Add(i == j
                ? (sorted[i] + 1).ToString(CultureInfo.InvariantCulture)
                : $"{sorted[i] + 1}-{sorted[j] + 1}");
            i = j + 1;
        }

        return string.Join(",", parts);
    }
}
