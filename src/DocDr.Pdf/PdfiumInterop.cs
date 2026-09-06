namespace DocDr.Pdf;

/// <summary>
/// PDFium bitmap pixel formats (subset). Values mirror the native <c>FPDFBitmap_*</c> constants.
/// </summary>
internal enum PdfiumBitmapFormat
{
    Gray = 1,
    Bgr = 2,
    Bgrx = 3,
    Bgra = 4,
}

/// <summary>
/// Flags for <c>FPDF_RenderPageBitmap</c>. Values mirror the native <c>FPDF_*</c> render flags.
/// </summary>
[Flags]
internal enum PdfiumRenderFlags
{
    None = 0,
    Annotations = 0x01,
    LcdText = 0x02,
    Grayscale = 0x08,
    LimitImageCacheSize = 0x200,
    ForceHalftone = 0x400,
    Printing = 0x800,
}

/// <summary>
/// Search flags for <c>FPDFText_FindStart</c>.
/// </summary>
[Flags]
public enum PdfSearchOptions
{
    None = 0,
    MatchCase = 0x01,
    MatchWholeWord = 0x02,
    ConsecutiveMatches = 0x04,
}

/// <summary>
/// Error codes returned by <c>FPDF_GetLastError</c>.
/// </summary>
public enum PdfiumError
{
    Success = 0,
    Unknown = 1,
    FileNotFoundOrUnavailable = 2,
    InvalidFormat = 3,
    PasswordRequiredOrIncorrect = 4,
    UnsupportedSecurityScheme = 5,
    PageNotFoundOrContentError = 6,
}
