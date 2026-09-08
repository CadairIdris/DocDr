namespace DocDr.Pdf;

/// <summary>
/// Decodes an encoded image (PNG / JPEG bytes) to a tightly-packed BGRA32 buffer. Implemented in
/// the app (WPF) so <c>DocDr.Pdf</c> stays free of image-codec dependencies; set on a
/// <see cref="PdfDocument"/> via <see cref="PdfDocument.ImageDecoder"/>.
/// </summary>
public interface IImageDecoder
{
    /// <summary>Decode <paramref name="encoded"/> to <c>(width, height, stride, BGRA pixels top-row-first)</c>.</summary>
    (int Width, int Height, int Stride, byte[] Bgra) DecodeToBgra(byte[] encoded);
}
