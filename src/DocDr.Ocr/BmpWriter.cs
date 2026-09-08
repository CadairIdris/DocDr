using DocDr.Pdf;

namespace DocDr.Ocr;

/// <summary>Encodes a <see cref="RenderedPage"/>'s BGRA buffer as an uncompressed 24-bit BMP —
/// the simplest format Leptonica reads from memory, and enough for an opaque scan.</summary>
internal static class BmpWriter
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;

    internal static byte[] FromRendered(RenderedPage page, int dpi)
    {
        int width = page.PixelWidth;
        int height = page.PixelHeight;
        int srcStride = page.Stride;
        int dstStride = ((width * 3) + 3) & ~3; // rows padded to a 4-byte boundary
        int pixelBytes = dstStride * height;
        int fileSize = FileHeaderSize + InfoHeaderSize + pixelBytes;
        int ppm = (int)Math.Round(dpi / 0.0254); // pixels per metre

        var buffer = new byte[fileSize];
        int p = 0;

        // BITMAPFILEHEADER
        buffer[p++] = (byte)'B';
        buffer[p++] = (byte)'M';
        WriteI32(buffer, ref p, fileSize);
        WriteI32(buffer, ref p, 0); // reserved
        WriteI32(buffer, ref p, FileHeaderSize + InfoHeaderSize); // pixel data offset

        // BITMAPINFOHEADER
        WriteI32(buffer, ref p, InfoHeaderSize);
        WriteI32(buffer, ref p, width);
        WriteI32(buffer, ref p, height); // positive => bottom-up
        WriteI16(buffer, ref p, 1);      // planes
        WriteI16(buffer, ref p, 24);     // bpp
        WriteI32(buffer, ref p, 0);      // BI_RGB
        WriteI32(buffer, ref p, pixelBytes);
        WriteI32(buffer, ref p, ppm);
        WriteI32(buffer, ref p, ppm);
        WriteI32(buffer, ref p, 0);      // colours used
        WriteI32(buffer, ref p, 0);      // colours important

        byte[] pixels = page.Pixels;
        for (int y = 0; y < height; y++)
        {
            int srcRow = (height - 1 - y) * srcStride; // BMP rows run bottom-to-top
            int dstRow = FileHeaderSize + InfoHeaderSize + (y * dstStride);
            for (int x = 0; x < width; x++)
            {
                int s = srcRow + (x * 4);
                int d = dstRow + (x * 3);
                buffer[d] = pixels[s];         // B
                buffer[d + 1] = pixels[s + 1]; // G
                buffer[d + 2] = pixels[s + 2]; // R
            }
        }

        return buffer;
    }

    private static void WriteI32(byte[] b, ref int p, int value)
    {
        b[p++] = (byte)value;
        b[p++] = (byte)(value >> 8);
        b[p++] = (byte)(value >> 16);
        b[p++] = (byte)(value >> 24);
    }

    private static void WriteI16(byte[] b, ref int p, int value)
    {
        b[p++] = (byte)value;
        b[p++] = (byte)(value >> 8);
    }
}
