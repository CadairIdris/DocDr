using System.Runtime.InteropServices;
using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>PDF Info-dictionary fields. Empty string where a field is absent.</summary>
public sealed record PdfDocumentInfo(
    string Title,
    string Author,
    string Subject,
    string Keywords,
    string Creator,
    string Producer,
    string CreationDate,
    string ModificationDate);

/// <summary>
/// Reads standard Info-dictionary metadata via PDFium. Read-only in Stage 1; writing is a
/// Stage 4 question (PDFium's public API has no setter and the SQLite catalog is the source
/// of truth for user-managed fields regardless).
/// </summary>
public static class PdfMetadata
{
    public static PdfDocumentInfo GetInfo(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return document.Locked(() => new PdfDocumentInfo(
            Read(document.Handle, "Title"),
            Read(document.Handle, "Author"),
            Read(document.Handle, "Subject"),
            Read(document.Handle, "Keywords"),
            Read(document.Handle, "Creator"),
            Read(document.Handle, "Producer"),
            Read(document.Handle, "CreationDate"),
            Read(document.Handle, "ModDate")));
    }

    private static string Read(FpdfDocumentT handle, string key)
    {
        // First call sizes the buffer; returned length includes the UTF-16 NUL terminator.
        uint byteLength = fpdf_doc.FPDF_GetMetaText(handle, key, IntPtr.Zero, 0);
        if (byteLength <= 2)
        {
            return string.Empty;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)byteLength);
        try
        {
            fpdf_doc.FPDF_GetMetaText(handle, key, buffer, byteLength);
            return Marshal.PtrToStringUni(buffer)?.TrimEnd('\0') ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
