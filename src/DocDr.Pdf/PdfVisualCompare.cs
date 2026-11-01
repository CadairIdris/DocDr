namespace DocDr.Pdf;

/// <summary>Tuning for <see cref="PdfVisualCompare"/>.</summary>
public sealed record VisualCompareOptions
{
    /// <summary>Render resolution for a page comparison. Default 150 DPI (capped so a large sheet
    /// stays under <see cref="MaxPixels"/>).</summary>
    public double Dpi { get; init; } = 150;

    /// <summary>Upper bound on the rendered page's pixel count; a bigger sheet renders at a lower DPI.</summary>
    public long MaxPixels { get; init; } = 24_000_000;

    /// <summary>A pixel only counts as changed when the other version has no ink within this many
    /// pixels. Default 0 (exact): both pages go through the same renderer, so unchanged content is
    /// pixel-identical, and even one pixel of tolerance hides a changed digit in small text
    /// (305 → 306 at 110 DPI). Set 1–2 for scans or re-exported drawings that drift by a pixel.</summary>
    public int TolerancePixels { get; init; }

    /// <summary>Largest whole-page shift (fraction of the page's width / height) the alignment step
    /// searches, for a revision whose drawing moved slightly on the sheet. Default 2 %.</summary>
    public double MaxShiftFraction { get; init; } = 0.02;

    /// <summary>A pixel is ink when its darkest channel is below this. Default 200 (catches
    /// coloured linework as well as black).</summary>
    public int InkThreshold { get; init; } = 200;

    /// <summary>Changes smaller than this many pixels are noise, not a change. Default 6.</summary>
    public int MinRegionPixels { get; init; } = 6;
}

/// <summary>A cluster of changes on the comparison image, in its pixels (top-left origin).</summary>
public readonly record struct VisualDiffRegion(int X, int Y, int Width, int Height, int RemovedPixels, int AddedPixels);

/// <summary>
/// The result of comparing one page with another: the old page, the new page aligned onto it, and an
/// overlay image — ink in both versions faint grey, ink only in the old version <b>red</b> (removed),
/// ink only in the new version <b>blue</b> (added) — all BGRx at the same size, plus the change regions.
/// </summary>
public sealed class VisualDiff
{
    internal VisualDiff(int width, int height, byte[] overlay, byte[] oldImage, byte[] newImage,
        IReadOnlyList<VisualDiffRegion> regions, int offsetX, int offsetY, int removed, int added,
        bool sizesDiffer, double dpi)
    {
        Width = width;
        Height = height;
        Overlay = overlay;
        Old = oldImage;
        New = newImage;
        Regions = regions;
        OffsetX = offsetX;
        OffsetY = offsetY;
        RemovedPixels = removed;
        AddedPixels = added;
        SizesDiffer = sizesDiffer;
        Dpi = dpi;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Row length in bytes of every image (Width × 4).</summary>
    public int Stride => Width * 4;

    /// <summary>BGRx: grey = unchanged, red = removed, blue = added.</summary>
    public byte[] Overlay { get; }

    /// <summary>BGRx render of the old page.</summary>
    public byte[] Old { get; }

    /// <summary>BGRx render of the new page, shifted by the alignment offset (for onion-skin blending).</summary>
    public byte[] New { get; }

    public IReadOnlyList<VisualDiffRegion> Regions { get; }

    /// <summary>How far (pixels) the new page was moved to line up with the old one.</summary>
    public int OffsetX { get; }

    public int OffsetY { get; }

    public int RemovedPixels { get; }

    public int AddedPixels { get; }

    /// <summary>The pages are different sizes; the new one was scaled to the old one's size.</summary>
    public bool SizesDiffer { get; }

    public double Dpi { get; }

    public bool HasChanges => Regions.Count > 0;

    internal PageMap? OldMap { get; private set; }

    internal PageMap? NewMap { get; private set; }

    internal VisualDiff WithMaps(PageMap oldMap, PageMap newMap)
    {
        OldMap = oldMap;
        NewMap = newMap;
        return this;
    }

    /// <summary>A box on the old page (unrotated page space, as text / annotation APIs report it) in
    /// this comparison's pixels; null for a diff not made from PDF pages.</summary>
    public DeviceRect? MapOld(PdfRect box) => OldMap?.Map(box, 0, 0);

    /// <summary>A box on the new page in this comparison's pixels — scaled like the new page was and
    /// moved by the alignment offset, so it lines up with <see cref="Overlay"/>.</summary>
    public DeviceRect? MapNew(PdfRect box) => NewMap?.Map(box, OffsetX, OffsetY);

    /// <summary>How a page's space maps onto the comparison image (it may be stretched non-uniformly).</summary>
    internal sealed record PageMap(PdfSize Unrotated, PdfRotation Rotation, PdfPoint CropOrigin, double ScaleX, double ScaleY)
    {
        public DeviceRect Map(PdfRect box, int dx, int dy)
        {
            DeviceRect r = PdfCoordinates.PageToDevice(box, Unrotated, Rotation, 1, CropOrigin);
            return new DeviceRect((r.X * ScaleX) - dx, (r.Y * ScaleY) - dy, r.Width * ScaleX, r.Height * ScaleY);
        }
    }
}

public enum PageCompareStatus
{
    Same,
    Changed,

    /// <summary>The old document has this page, the new one doesn't.</summary>
    OnlyInOld,

    /// <summary>The new document has this page, the old one doesn't.</summary>
    OnlyInNew,
}

/// <summary>One page pair in a whole-document overview (0-based pages; null where that document has no page).</summary>
public sealed record PageCompareSummary(int? OldPage, int? NewPage, PageCompareStatus Status, int Regions);

/// <summary>
/// Visual (pixel) comparison of PDF pages — for drawing revisions, marked-up sets and scans, where
/// what matters is what's drawn rather than the text. Each page pair is rendered at the same
/// resolution, the new page is aligned onto the old one (a small whole-page shift, found from the
/// row / column ink profiles), and pixels with ink in only one version are the changes, grouped into
/// regions for next / previous navigation.
/// </summary>
public static class PdfVisualCompare
{
    private const int RegionCell = 8;

    /// <summary>Compare page <paramref name="pageA"/> of the old document with page
    /// <paramref name="pageB"/> of the new one.</summary>
    public static VisualDiff ComparePages(PdfDocument oldDoc, int pageA, PdfDocument newDoc, int pageB,
        VisualCompareOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldDoc);
        ArgumentNullException.ThrowIfNull(newDoc);
        options ??= new VisualCompareOptions();
        PdfSize sizeA = oldDoc.GetPageSize(pageA);
        double scale = options.Dpi / PdfCoordinates.PointsPerInch;
        double area = Math.Max(1, sizeA.Width * sizeA.Height);
        if (area * scale * scale > options.MaxPixels)
        {
            scale = Math.Sqrt(options.MaxPixels / area);
        }

        PdfSize sizeB = newDoc.GetPageSize(pageB);
        int w = Math.Clamp((int)Math.Ceiling(sizeA.Width * scale), 1, 8000);
        int h = Math.Clamp((int)Math.Ceiling(sizeA.Height * scale), 1, 8000);

        // Same sheet size (within 1 %) → render the new page at the same DPI; otherwise stretch it
        // onto the old page's pixels so the drawings still overlay.
        bool sameSize = Math.Abs(sizeB.Width - sizeA.Width) <= sizeA.Width * 0.01
            && Math.Abs(sizeB.Height - sizeA.Height) <= sizeA.Height * 0.01;
        var renderer = new PageRenderer();
        RenderedPage a = renderer.Render(oldDoc, pageA, w, h, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        RenderedPage b = renderer.Render(newDoc, pageB, w, h, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        VisualDiff diff = Diff(a.Pixels, b.Pixels, w, h, a.Stride, options with { Dpi = scale * PdfCoordinates.PointsPerInch }, sizesDiffer: !sameSize);
        return diff.WithMaps(
            new VisualDiff.PageMap(oldDoc.GetUnrotatedPageSize(pageA), oldDoc.GetPageRotation(pageA), oldDoc.GetCropOrigin(pageA), w / sizeA.Width, h / sizeA.Height),
            new VisualDiff.PageMap(newDoc.GetUnrotatedPageSize(pageB), newDoc.GetPageRotation(pageB), newDoc.GetCropOrigin(pageB), w / sizeB.Width, h / sizeB.Height));
    }

    /// <summary>
    /// A quick overview of every page pair at a low resolution — which pages changed, and which exist
    /// in only one document. Old page <c>i</c> is paired with new page <c>i + newPageOffset</c> (a
    /// revision with a page inserted at the front compares with offset 1). <paramref name="pagesDone"/>
    /// reports pairs done; the total is <see cref="PairCount"/>.
    /// </summary>
    public static IReadOnlyList<PageCompareSummary> CompareDocuments(PdfDocument oldDoc, PdfDocument newDoc,
        int newPageOffset = 0, double dpi = 40, IProgress<int>? pagesDone = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldDoc);
        ArgumentNullException.ThrowIfNull(newDoc);
        var options = new VisualCompareOptions { Dpi = dpi, MinRegionPixels = 3 };
        var result = new List<PageCompareSummary>();
        int first = Math.Min(0, -newPageOffset);
        int end = Math.Max(oldDoc.PageCount, newDoc.PageCount - newPageOffset);
        for (int i = first; i < end; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int j = i + newPageOffset;
            bool hasOld = i >= 0 && i < oldDoc.PageCount;
            bool hasNew = j >= 0 && j < newDoc.PageCount;
            if (hasOld && hasNew)
            {
                VisualDiff d = ComparePages(oldDoc, i, newDoc, j, options, cancellationToken);
                result.Add(new PageCompareSummary(i, j, d.HasChanges ? PageCompareStatus.Changed : PageCompareStatus.Same, d.Regions.Count));
            }
            else
            {
                result.Add(new PageCompareSummary(hasOld ? i : null, hasNew ? j : null,
                    hasOld ? PageCompareStatus.OnlyInOld : PageCompareStatus.OnlyInNew, 0));
            }

            pagesDone?.Report(i - first + 1);
        }

        return result;
    }

    /// <summary>How many rows <see cref="CompareDocuments"/> produces for these page counts.</summary>
    public static int PairCount(int oldPages, int newPages, int newPageOffset) =>
        Math.Max(oldPages, newPages - newPageOffset) - Math.Min(0, -newPageOffset);

    /// <summary>The comparison itself, on two BGRx buffers of the same size. Pure — tested directly.</summary>
    internal static VisualDiff Diff(byte[] a, byte[] b, int w, int h, int stride, VisualCompareOptions options, bool sizesDiffer = false)
    {
        bool[] inkA = Ink(a, w, h, stride, options.InkThreshold);
        bool[] inkB0 = Ink(b, w, h, stride, options.InkThreshold);

        int maxDx = Math.Max(0, (int)(w * options.MaxShiftFraction));
        int maxDy = Math.Max(0, (int)(h * options.MaxShiftFraction));
        (int dx, int dy) = Align(inkA, inkB0, w, h, maxDx, maxDy);

        // The new page moved by (dx, dy) so it lines up: new'(x, y) = new(x + dx, y + dy).
        bool[] inkB = Shift(inkB0, w, h, dx, dy);
        byte[] shiftedB = ShiftPixels(b, w, h, stride, dx, dy);

        int r = Math.Max(0, options.TolerancePixels);
        bool[] nearA = Dilate(inkA, w, h, r);
        bool[] nearB = Dilate(inkB, w, h, r);

        var overlay = new byte[stride * h];
        var changed = new byte[w * h]; // 0 none, 1 removed, 2 added
        int removed = 0, added = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w) + x;
                int o = (y * stride) + (x * 4);
                bool rem = inkA[i] && !nearB[i];
                bool add = inkB[i] && !nearA[i];
                if (rem)
                {
                    (overlay[o], overlay[o + 1], overlay[o + 2]) = ((byte)40, (byte)40, (byte)220); // red
                    changed[i] = 1;
                    removed++;
                }
                else if (add)
                {
                    (overlay[o], overlay[o + 1], overlay[o + 2]) = ((byte)230, (byte)110, (byte)20); // blue
                    changed[i] = 2;
                    added++;
                }
                else if (inkA[i] || inkB[i])
                {
                    // Unchanged ink, lightened so the changes stand out.
                    int lum = Math.Min(Lum(a, o), inkB[i] ? Lum(shiftedB, o) : 255);
                    byte v = (byte)(255 - ((255 - lum) * 0.35));
                    (overlay[o], overlay[o + 1], overlay[o + 2]) = (v, v, v);
                }
                else
                {
                    (overlay[o], overlay[o + 1], overlay[o + 2]) = ((byte)255, (byte)255, (byte)255);
                }

                overlay[o + 3] = 255;
            }
        }

        IReadOnlyList<VisualDiffRegion> regions = Regions(changed, w, h, options.MinRegionPixels);
        return new VisualDiff(w, h, overlay, a, shiftedB, regions, dx, dy, removed, added, sizesDiffer, options.Dpi);
    }

    private static int Lum(byte[] px, int o) => Math.Min(px[o], Math.Min(px[o + 1], px[o + 2]));

    private static bool[] Ink(byte[] px, int w, int h, int stride, int threshold)
    {
        var ink = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                ink[(y * w) + x] = Lum(px, (y * stride) + (x * 4)) < threshold;
            }
        }

        return ink;
    }

    /// <summary>
    /// The whole-page shift that best lines the new page up with the old: maximise the correlation of
    /// the row-ink profiles (for dy) and column-ink profiles (for dx). A shift is only taken when it
    /// beats no shift by 5 % — a page of evenly spaced text lines correlates almost as well one line
    /// pitch off, and a false shift would turn every line into a change.
    /// </summary>
    internal static (int Dx, int Dy) Align(bool[] inkA, bool[] inkB, int w, int h, int maxDx, int maxDy)
    {
        var rowsA = new int[h];
        var rowsB = new int[h];
        var colsA = new int[w];
        var colsB = new int[w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w) + x;
                if (inkA[i])
                {
                    rowsA[y]++;
                    colsA[x]++;
                }

                if (inkB[i])
                {
                    rowsB[y]++;
                    colsB[x]++;
                }
            }
        }

        return (BestShift(colsA, colsB, maxDx), BestShift(rowsA, rowsB, maxDy));
    }

    private static int BestShift(int[] a, int[] b, int max)
    {
        long Score(int s)
        {
            long sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int j = i + s;
                if (j >= 0 && j < b.Length)
                {
                    sum += (long)a[i] * b[j];
                }
            }

            return sum;
        }

        long zero = Score(0);
        long best = zero;
        int bestShift = 0;
        for (int s = 1; s <= max; s++)
        {
            foreach (int c in new[] { s, -s })
            {
                long score = Score(c);
                if (score > best)
                {
                    best = score;
                    bestShift = c;
                }
            }
        }

        return best > zero * 1.05 ? bestShift : 0;
    }

    private static bool[] Shift(bool[] ink, int w, int h, int dx, int dy)
    {
        if (dx == 0 && dy == 0)
        {
            return ink;
        }

        var result = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            int sy = y + dy;
            if (sy < 0 || sy >= h)
            {
                continue;
            }

            for (int x = 0; x < w; x++)
            {
                int sx = x + dx;
                if (sx >= 0 && sx < w)
                {
                    result[(y * w) + x] = ink[(sy * w) + sx];
                }
            }
        }

        return result;
    }

    private static byte[] ShiftPixels(byte[] px, int w, int h, int stride, int dx, int dy)
    {
        if (dx == 0 && dy == 0)
        {
            return px;
        }

        var result = new byte[stride * h];
        Array.Fill(result, (byte)255);
        for (int y = 0; y < h; y++)
        {
            int sy = y + dy;
            if (sy < 0 || sy >= h)
            {
                continue;
            }

            for (int x = 0; x < w; x++)
            {
                int sx = x + dx;
                if (sx >= 0 && sx < w)
                {
                    Buffer.BlockCopy(px, (sy * stride) + (sx * 4), result, (y * stride) + (x * 4), 4);
                }
            }
        }

        return result;
    }

    /// <summary>Square dilation by <paramref name="r"/> pixels (separable max filter).</summary>
    private static bool[] Dilate(bool[] ink, int w, int h, int r)
    {
        if (r == 0)
        {
            return ink;
        }

        var horizontal = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            int run = -1; // last ink x seen
            for (int x = 0; x < w + r; x++)
            {
                if (x < w && ink[(y * w) + x])
                {
                    run = x;
                }

                int target = x - r;
                if (target >= 0 && run >= 0 && run >= target - r)
                {
                    horizontal[(y * w) + target] = true;
                }
            }
        }

        var result = new bool[w * h];
        for (int x = 0; x < w; x++)
        {
            int run = -1;
            for (int y = 0; y < h + r; y++)
            {
                if (y < h && horizontal[(y * w) + x])
                {
                    run = y;
                }

                int target = y - r;
                if (target >= 0 && run >= 0 && run >= target - r)
                {
                    result[(target * w) + x] = true;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Changed pixels grouped into regions: count them per 8-px cell, join cells that touch (or are
    /// one cell apart — a change to a word is several separate letters), and box each group. Groups
    /// under <paramref name="minPixels"/> changed pixels are noise. Top-to-bottom, then left-to-right.
    /// </summary>
    internal static IReadOnlyList<VisualDiffRegion> Regions(byte[] changed, int w, int h, int minPixels)
    {
        int cw = (w + RegionCell - 1) / RegionCell, ch = (h + RegionCell - 1) / RegionCell;
        var removedIn = new int[cw * ch];
        var addedIn = new int[cw * ch];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte c = changed[(y * w) + x];
                if (c != 0)
                {
                    int cell = ((y / RegionCell) * cw) + (x / RegionCell);
                    if (c == 1)
                    {
                        removedIn[cell]++;
                    }
                    else
                    {
                        addedIn[cell]++;
                    }
                }
            }
        }

        var seen = new bool[cw * ch];
        var regions = new List<VisualDiffRegion>();
        var stack = new Stack<int>();
        for (int start = 0; start < cw * ch; start++)
        {
            if (seen[start] || removedIn[start] + addedIn[start] == 0)
            {
                continue;
            }

            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, rem = 0, add = 0;
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int cell = stack.Pop();
                int cx = cell % cw, cy = cell / cw;
                minX = Math.Min(minX, cx);
                minY = Math.Min(minY, cy);
                maxX = Math.Max(maxX, cx);
                maxY = Math.Max(maxY, cy);
                rem += removedIn[cell];
                add += addedIn[cell];
                for (int ny = cy - 2; ny <= cy + 2; ny++)
                {
                    for (int nx = cx - 2; nx <= cx + 2; nx++)
                    {
                        if (nx < 0 || ny < 0 || nx >= cw || ny >= ch)
                        {
                            continue;
                        }

                        int n = (ny * cw) + nx;
                        if (!seen[n] && removedIn[n] + addedIn[n] > 0)
                        {
                            seen[n] = true;
                            stack.Push(n);
                        }
                    }
                }
            }

            if (rem + add < minPixels)
            {
                continue;
            }

            int px = minX * RegionCell, py = minY * RegionCell;
            regions.Add(new VisualDiffRegion(px, py,
                Math.Min(w, (maxX + 1) * RegionCell) - px, Math.Min(h, (maxY + 1) * RegionCell) - py, rem, add));
        }

        return [.. regions.OrderBy(r => r.Y).ThenBy(r => r.X)];
    }
}
