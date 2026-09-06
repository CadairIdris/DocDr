namespace DocDr.Pdf;

/// <summary>
/// Raised when a PDFium operation fails. Carries the <see cref="PdfiumError"/> reported by
/// <c>FPDF_GetLastError</c> where one is available.
/// </summary>
public sealed class PdfException : Exception
{
    public PdfException(string message) : base(message)
    {
        Error = PdfiumError.Unknown;
    }

    public PdfException(PdfiumError error, string message) : base(message)
    {
        Error = error;
    }

    public PdfiumError Error { get; }
}
