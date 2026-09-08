using System;
using System.IO;
using System.Windows.Media.Imaging;
using DocDr.Pdf;

namespace DocDr.App.Services;

/// <summary>Decodes PNG / JPEG bytes to BGRA32 with WPF's imaging, for <see cref="PdfDocument.ImageDecoder"/>.</summary>
public sealed class AppImageDecoder : IImageDecoder
{
    public (int Width, int Height, int Stride, byte[] Bgra) DecodeToBgra(byte[] encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);

        var decoder = BitmapDecoder.Create(
            new MemoryStream(encoded, writable: false),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];

        var bgra = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);
        return (width, height, stride, pixels);
    }
}
