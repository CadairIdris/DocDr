using System.Reflection;
using System.Runtime.InteropServices;
using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>
/// PDFium's ink-stroke functions take a C array of <c>FS_POINTF</c> (two floats each). PDFiumCore
/// only exposes them as a single <see cref="FS_POINTF_"/> wrapper whose <c>__Instance</c> is the
/// array's base pointer, and the wrapper's pointer-based factory is <c>internal</c> — so we reach
/// it once by reflection and marshal the array by hand.
/// </summary>
internal static class PdfInkInterop
{
    private const int PointBytes = 8; // float x + float y

    private static readonly MethodInfo CreateWrapper = typeof(FS_POINTF_).GetMethod(
        "__CreateInstance",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        types: [typeof(IntPtr), typeof(bool)],
        modifiers: null)
        ?? throw new PdfException("PDFiumCore FS_POINTF_.__CreateInstance not found — package layout changed.");

    /// <summary>Wrap a native <c>FS_POINTF[]</c> base pointer as the argument PDFium's ink APIs expect.</summary>
    private static FS_POINTF_ Wrap(IntPtr basePointer) =>
        (FS_POINTF_)CreateWrapper.Invoke(null, [basePointer, false])!;

    /// <summary>Add one polyline (unrotated page-space points) to an ink annotation.</summary>
    public static void AddStroke(FpdfAnnotationT annot, IReadOnlyList<PdfPoint> stroke)
    {
        if (stroke.Count < 2)
        {
            return;
        }

        IntPtr buffer = Marshal.AllocHGlobal(stroke.Count * PointBytes);
        try
        {
            for (int i = 0; i < stroke.Count; i++)
            {
                Marshal.WriteInt32(buffer + (i * PointBytes), BitConverter.SingleToInt32Bits((float)stroke[i].X));
                Marshal.WriteInt32(buffer + (i * PointBytes) + 4, BitConverter.SingleToInt32Bits((float)stroke[i].Y));
            }

            fpdf_annot.FPDFAnnotAddInkStroke(annot, Wrap(buffer), (ulong)stroke.Count);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Read every ink polyline off an annotation, in unrotated page space.</summary>
    public static IReadOnlyList<IReadOnlyList<PdfPoint>> ReadStrokes(FpdfAnnotationT annot)
    {
        uint pathCount = fpdf_annot.FPDFAnnotGetInkListCount(annot);
        if (pathCount == 0)
        {
            return [];
        }

        var strokes = new List<IReadOnlyList<PdfPoint>>((int)pathCount);
        for (uint path = 0; path < pathCount; path++)
        {
            uint pointCount = fpdf_annot.FPDFAnnotGetInkListPath(annot, path, null, 0);
            if (pointCount == 0)
            {
                continue;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)pointCount * PointBytes);
            try
            {
                fpdf_annot.FPDFAnnotGetInkListPath(annot, path, Wrap(buffer), pointCount);
                var points = new PdfPoint[pointCount];
                for (int i = 0; i < pointCount; i++)
                {
                    float x = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(buffer + (i * PointBytes)));
                    float y = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(buffer + (i * PointBytes) + 4));
                    points[i] = new PdfPoint(x, y);
                }

                strokes.Add(points);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return strokes;
    }
}
