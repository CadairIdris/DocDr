using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PDFiumCore;

namespace DocDr.Pdf;

public enum WatermarkKind
{
    /// <summary>A run of text repeated on most pages (a stamp / "uncontrolled copy" notice).</summary>
    Text,

    /// <summary>A near-full-page image repeated on most pages (a diagonal overlay etc.).</summary>
    Image,
}

/// <summary>
/// A piece of content that repeats across most pages and is likely a watermark. Carry the whole
/// record into <see cref="PdfDocument.RemoveWatermarks"/> to strip it.
/// </summary>
public sealed record WatermarkCandidate(
    WatermarkKind Kind,
    string Label,
    int PageCount,
    int TotalPages)
{
    /// <summary>True when removing this leaves every carrying page with other content (won't blank a page).</summary>
    public bool SafeToRemove { get; init; } = true;

    internal WatermarkSignature Signature { get; init; } = new();

    public bool OnEveryPage => PageCount >= TotalPages;
}

/// <summary>Opaque match token for a candidate — a normalised string or an image-bytes hash.</summary>
internal sealed class WatermarkSignature
{
    public WatermarkKind Kind { get; init; }
    public string? Text { get; init; }
    public string? ImageHash { get; init; }
}

/// <summary>
/// Finds content repeated across most pages (repeated text runs, near-full-page repeated images).
/// Read-only; all PDFium work runs under the document lock. Mirrors <see cref="PdfBookmarks"/>.
/// </summary>
public static class PdfWatermarks
{
    private const int TypeText = 1;
    private const int TypePath = 2;
    private const int TypeImage = 3;
    private const int MinTextLength = 4;
    private const double FullPageFraction = 0.88;
    private const double MinPageFraction = 0.8;

    public static IReadOnlyList<WatermarkCandidate> Scan(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return document.Locked(() =>
        {
            FpdfDocumentT handle = document.Handle;
            int total = document.PageCount;

            var textHits = new Dictionary<string, (string Sample, HashSet<int> Pages)>();
            var imageHits = new Dictionary<string, (int W, int H, HashSet<int> Pages, HashSet<int> BodyPages)>();

            for (int p = 0; p < total; p++)
            {
                FpdfPageT? page = fpdfview.FPDF_LoadPage(handle, p);
                if (page is null || page.__Instance == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    double pw = fpdfview.FPDF_GetPageWidth(page);
                    double ph = fpdfview.FPDF_GetPageHeight(page);
                    FpdfTextpageT? textPage = fpdf_text.FPDFTextLoadPage(page);
                    try
                    {
                        int n = fpdf_edit.FPDFPageCountObjects(page);
                        bool hasBody = false;
                        var imagesOnPage = new List<string>();

                        for (int i = 0; i < n; i++)
                        {
                            FpdfPageobjectT obj = fpdf_edit.FPDFPageGetObject(page, i);
                            int type = fpdf_edit.FPDFPageObjGetType(obj);

                            if (type == TypeText)
                            {
                                string norm = NormalizeText(ReadTextObject(obj, textPage));
                                if (norm.Length < MinTextLength)
                                {
                                    hasBody = true;
                                    continue;
                                }

                                if (!textHits.TryGetValue(norm, out (string, HashSet<int>) entry))
                                {
                                    textHits[norm] = entry = ((ReadTextObject(obj, textPage) ?? norm).Trim(), []);
                                }

                                entry.Item2.Add(p);
                            }
                            else if (type == TypeImage)
                            {
                                if (!IsFullPage(obj, pw, ph))
                                {
                                    hasBody = true;
                                    continue;
                                }

                                string? hash = HashImageObject(obj);
                                if (hash is null)
                                {
                                    continue;
                                }

                                imagesOnPage.Add(hash);
                                if (!imageHits.TryGetValue(hash, out (int, int, HashSet<int>, HashSet<int>) ie))
                                {
                                    (int w, int h) = ImagePixelSize(obj, page);
                                    imageHits[hash] = ie = (w, h, [], []);
                                }

                                ie.Item3.Add(p);
                            }
                            else if (type == TypePath)
                            {
                                hasBody = true;
                            }
                        }

                        foreach (string hash in imagesOnPage)
                        {
                            if (hasBody)
                            {
                                imageHits[hash].BodyPages.Add(p);
                            }
                        }
                    }
                    finally
                    {
                        if (textPage is not null && textPage.__Instance != IntPtr.Zero)
                        {
                            fpdf_text.FPDFTextClosePage(textPage);
                        }
                    }
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }

            int minPages = Math.Max(3, (int)Math.Ceiling(MinPageFraction * total));
            var result = new List<WatermarkCandidate>();

            foreach ((string norm, (string sample, HashSet<int> pages)) in textHits)
            {
                if (pages.Count < minPages)
                {
                    continue;
                }

                result.Add(new WatermarkCandidate(WatermarkKind.Text, Ellipsize(sample), pages.Count, total)
                {
                    Signature = new WatermarkSignature { Kind = WatermarkKind.Text, Text = norm },
                });
            }

            foreach ((string hash, (int w, int h, HashSet<int> pages, HashSet<int> bodyPages)) in imageHits)
            {
                if (pages.Count < minPages)
                {
                    continue;
                }

                result.Add(new WatermarkCandidate(
                    WatermarkKind.Image, $"Full-page image ({w}×{h})", pages.Count, total)
                {
                    SafeToRemove = bodyPages.Count == pages.Count,
                    Signature = new WatermarkSignature { Kind = WatermarkKind.Image, ImageHash = hash },
                });
            }

            return (IReadOnlyList<WatermarkCandidate>)result
                .OrderByDescending(c => c.Kind == WatermarkKind.Image)
                .ThenByDescending(c => c.PageCount)
                .ThenByDescending(c => c.Label.Length)
                .ToArray();
        });
    }

    // --- shared with PdfDocument.RemoveWatermarks --------------------------------------

    internal static string NormalizeText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(raw.Length);
        bool lastWhite = false;
        foreach (char c in raw.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWhite)
                {
                    sb.Append(' ');
                }

                lastWhite = true;
            }
            else
            {
                sb.Append(c);
                lastWhite = false;
            }
        }

        return sb.ToString();
    }

    internal static string? ReadTextObject(FpdfPageobjectT obj, FpdfTextpageT? textPage)
    {
        if (textPage is null || textPage.__Instance == IntPtr.Zero)
        {
            return null;
        }

        ushort probe = 0;
        uint bytes = fpdf_edit.FPDFTextObjGetText(obj, textPage, ref probe, 0);
        if (bytes <= 2)
        {
            return null;
        }

        var buffer = new ushort[(bytes / 2) + 1];
        fpdf_edit.FPDFTextObjGetText(obj, textPage, ref buffer[0], bytes);
        int chars = Math.Max(0, (int)(bytes / 2) - 1);
        return chars <= 0 ? null : PdfTextExtractor.Utf16(buffer, chars);
    }

    internal static string? HashImageObject(FpdfPageobjectT obj)
    {
        uint len = fpdf_edit.FPDFImageObjGetImageDataRaw(obj, IntPtr.Zero, 0);
        if (len == 0)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)len);
        try
        {
            fpdf_edit.FPDFImageObjGetImageDataRaw(obj, buffer, len);
            var bytes = new byte[len];
            Marshal.Copy(buffer, bytes, 0, (int)len);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static bool IsFullPage(FpdfPageobjectT obj, double pageWidth, double pageHeight)
    {
        float left = 0, bottom = 0, right = 0, top = 0;
        if (fpdf_edit.FPDFPageObjGetBounds(obj, ref left, ref bottom, ref right, ref top) == 0)
        {
            return false;
        }

        return (right - left) >= pageWidth * FullPageFraction
               && (top - bottom) >= pageHeight * FullPageFraction;
    }

    private static (int Width, int Height) ImagePixelSize(FpdfPageobjectT obj, FpdfPageT page)
    {
        using var md = new FPDF_IMAGEOBJ_METADATA();
        return fpdf_edit.FPDFImageObjGetImageMetadata(obj, page, md) != 0
            ? ((int)md.Width, (int)md.Height)
            : (0, 0);
    }

    private static string Ellipsize(string s) =>
        s.Length <= 72 ? s : s[..70].TrimEnd() + "…";
}
