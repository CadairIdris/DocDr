using System.Buffers.Binary;
using System.IO.Compression;

namespace DocDr.Pdf;

/// <summary>
/// Minimal PNG encoder for <see cref="RenderedPage"/> buffers (BGRx), so <c>DocDr.Pdf</c> can write
/// images without WPF. Greyscale when every pixel is grey (most of a standard — a third the size),
/// else 8-bit RGB; each row uses the Up filter, which suits line art and text well.
/// </summary>
internal static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] EncodeBgrx(int width, int height, int stride, byte[] bgrx)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (stride < width * 4 || bgrx.Length < (long)stride * height)
        {
            throw new ArgumentException("Buffer too small for the image size.", nameof(bgrx));
        }

        bool grey = true;
        for (int y = 0; y < height && grey; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int o = row + (x * 4);
                if (bgrx[o] != bgrx[o + 1] || bgrx[o + 1] != bgrx[o + 2])
                {
                    grey = false;
                    break;
                }
            }
        }

        int channels = grey ? 1 : 3;
        int rowBytes = width * channels;
        var raw = new byte[(rowBytes + 1) * height];
        var previous = new byte[rowBytes];
        var current = new byte[rowBytes];
        for (int y = 0; y < height; y++)
        {
            int src = y * stride;
            for (int x = 0; x < width; x++)
            {
                int o = src + (x * 4);
                if (grey)
                {
                    current[x] = bgrx[o];
                }
                else
                {
                    current[(x * 3) + 0] = bgrx[o + 2]; // R
                    current[(x * 3) + 1] = bgrx[o + 1]; // G
                    current[(x * 3) + 2] = bgrx[o];     // B
                }
            }

            int dst = y * (rowBytes + 1);
            raw[dst] = 2; // Up filter
            for (int i = 0; i < rowBytes; i++)
            {
                raw[dst + 1 + i] = (byte)(current[i] - previous[i]);
            }

            (previous, current) = (current, previous);
        }

        byte[] compressed;
        using (var z = new MemoryStream())
        {
            using (var zlib = new ZLibStream(z, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            compressed = z.ToArray();
        }

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;                    // bit depth
        ihdr[9] = (byte)(grey ? 0 : 2); // colour type: greyscale / truecolour
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", compressed);
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    internal static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
