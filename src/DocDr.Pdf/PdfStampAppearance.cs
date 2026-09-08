using System.Runtime.InteropServices;
using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>
/// Builds the appearance of a stamp-backed annotation (text box, callout, revision cloud) out of
/// real PDFium page objects (paths + text), appended to the annotation. PDFium wires up the font
/// resources, so these render faithfully in any viewer and in DocDr's own print path.
/// Caller holds the document lock and owns <paramref name="doc"/> / the page the annot is on.
/// </summary>
internal static class PdfStampAppearance
{
    private const int DrawStroke = 1;
    private const int DrawFillNone = 0;
    private const int DrawFillWinding = 2;
    private const int LineJoinRound = 1;
    private const double CloudBumpRadius = 8.0;

    public static void Build(
        FpdfDocumentT doc, FpdfAnnotationT annot, FpdfFontT? font, PdfAnnotation a, IImageDecoder? imageDecoder)
    {
        (uint r, uint g, uint b) = Rgb(a.ColorArgb);
        PdfRect box = Normalise(a.Box);
        float w = (float)a.EffectiveLineWidth;
        bool dashed = a.Dashed;

        switch (a.Kind)
        {
            case PdfAnnotationKind.TextBox:
                if (!a.Borderless)
                {
                    StrokeRect(annot, box, r, g, b, w, dashed: false);
                }

                DrawText(doc, annot, font, a, box);
                break;

            case PdfAnnotationKind.Callout:
                DrawLeader(annot, a.Leader, r, g, b, w, dashed: false, arrow: true);
                if (!a.Borderless)
                {
                    StrokeRect(annot, box, r, g, b, w, dashed: false);
                }

                DrawText(doc, annot, font, a, box);
                break;

            case PdfAnnotationKind.Cloud:
                DrawCloud(annot, box, r, g, b, w);
                break;

            case PdfAnnotationKind.Rectangle:
                StrokeRect(annot, box, r, g, b, w, dashed, a.FillArgb);
                break;

            case PdfAnnotationKind.Ellipse:
                DrawEllipse(annot, box, r, g, b, w, dashed, a.FillArgb);
                break;

            case PdfAnnotationKind.Line:
                DrawLeader(annot, a.Leader, r, g, b, w, dashed, arrow: false);
                break;

            case PdfAnnotationKind.Arrow:
                DrawLeader(annot, a.Leader, r, g, b, w, dashed, arrow: true);
                break;

            case PdfAnnotationKind.Image:
                DrawImage(doc, annot, a, box, imageDecoder);
                break;
        }
    }

    private static void DrawImage(
        FpdfDocumentT doc, FpdfAnnotationT annot, PdfAnnotation a, PdfRect box, IImageDecoder? decoder)
    {
        if (decoder is null || a.ImageData is null || a.ImageData.Length == 0
            || box.Width < 1 || box.Height < 1)
        {
            return;
        }

        (int w, int h, int stride, byte[] bgra) = decoder.DecodeToBgra(a.ImageData);
        if (w <= 0 || h <= 0 || bgra.Length < stride * h)
        {
            return;
        }

        GCHandle pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            FpdfBitmapT bitmap = fpdfview.FPDFBitmapCreateEx(
                w, h, (int)PdfiumBitmapFormat.Bgra, pin.AddrOfPinnedObject(), stride);
            FpdfPageobjectT image = fpdf_edit.FPDFPageObjNewImageObj(doc);
            fpdf_edit.FPDFImageObjSetBitmap(null, 0, image, bitmap);
            // Scale the unit image to the box; PDFium's image space has its origin bottom-left.
            fpdf_edit.FPDFImageObjSetMatrix(
                image, box.Width, 0, 0, box.Height, box.Left, box.Bottom);
            fpdf_annot.FPDFAnnotAppendObject(annot, image);
            fpdfview.FPDFBitmapDestroy(bitmap);
        }
        finally
        {
            pin.Free();
        }
    }

    private static void DrawEllipse(
        FpdfAnnotationT annot, PdfRect box, uint r, uint g, uint b, float width, bool dashed, uint? fill)
    {
        const double kappa = 0.5522847498;
        double cx = (box.Left + box.Right) / 2, cy = (box.Top + box.Bottom) / 2;
        double rx = (box.Right - box.Left) / 2, ry = (box.Top - box.Bottom) / 2;

        if (fill is { } f || !dashed)
        {
            double ox = rx * kappa, oy = ry * kappa;
            FpdfPageobjectT path = fpdf_edit.FPDFPageObjCreateNewPath((float)(cx - rx), (float)cy);
            fpdf_edit.FPDFPathBezierTo(path, (float)(cx - rx), (float)(cy + oy), (float)(cx - ox), (float)(cy + ry), (float)cx, (float)(cy + ry));
            fpdf_edit.FPDFPathBezierTo(path, (float)(cx + ox), (float)(cy + ry), (float)(cx + rx), (float)(cy + oy), (float)(cx + rx), (float)cy);
            fpdf_edit.FPDFPathBezierTo(path, (float)(cx + rx), (float)(cy - oy), (float)(cx + ox), (float)(cy - ry), (float)cx, (float)(cy - ry));
            fpdf_edit.FPDFPathBezierTo(path, (float)(cx - ox), (float)(cy - ry), (float)(cx - rx), (float)(cy - oy), (float)(cx - rx), (float)cy);
            fpdf_edit.FPDFPathClose(path);
            StrokePath(annot, path, r, g, b, width, stroke: !dashed, fill);
        }

        if (dashed)
        {
            var pts = new List<PdfPoint>(97);
            for (int i = 0; i <= 96; i++)
            {
                double t = i / 96.0 * 2 * Math.PI;
                pts.Add(new PdfPoint(cx + (rx * Math.Cos(t)), cy + (ry * Math.Sin(t))));
            }

            DashPolyline(annot, pts, r, g, b, width);
        }
    }

    private static void StrokeRect(
        FpdfAnnotationT annot, PdfRect box, uint r, uint g, uint b, float width, bool dashed, uint? fill = null)
    {
        ReadOnlySpan<PdfPoint> corners =
        [
            new(box.Left, box.Bottom), new(box.Right, box.Bottom),
            new(box.Right, box.Top), new(box.Left, box.Top), new(box.Left, box.Bottom),
        ];

        if (fill is not null || !dashed)
        {
            FpdfPageobjectT path = fpdf_edit.FPDFPageObjCreateNewPath((float)box.Left, (float)box.Bottom);
            fpdf_edit.FPDFPathLineTo(path, (float)box.Right, (float)box.Bottom);
            fpdf_edit.FPDFPathLineTo(path, (float)box.Right, (float)box.Top);
            fpdf_edit.FPDFPathLineTo(path, (float)box.Left, (float)box.Top);
            fpdf_edit.FPDFPathClose(path);
            StrokePath(annot, path, r, g, b, width, stroke: !dashed, fill);
        }

        if (dashed)
        {
            DashPolyline(annot, corners.ToArray(), r, g, b, width);
        }
    }

    /// <summary>Stroke a polyline as an even dash pattern (PDFium doesn't honour a dash array on a
    /// stamp appearance path in this build, so the gaps are drawn geometrically).</summary>
    private static void DashPolyline(
        FpdfAnnotationT annot, IReadOnlyList<PdfPoint> pts, uint r, uint g, uint b, float width)
    {
        double on = Math.Max(3, 3 * width), off = Math.Max(2, 2.2 * width);
        double period = on + off;
        const double eps = 1e-4;
        double carry = 0; // distance already consumed of the current dash cell

        for (int i = 0; i + 1 < pts.Count; i++)
        {
            PdfPoint a = pts[i], c = pts[i + 1];
            double segLen = Math.Sqrt(((c.X - a.X) * (c.X - a.X)) + ((c.Y - a.Y) * (c.Y - a.Y)));
            if (segLen < eps)
            {
                continue;
            }

            double ux = (c.X - a.X) / segLen, uy = (c.Y - a.Y) / segLen;
            double pos = 0;
            while (pos < segLen - eps)
            {
                double cell = carry % period;
                double step = Math.Min(cell < on ? on - cell : period - cell, segLen - pos);
                if (step < eps)
                {
                    step = eps; // never stall on a rounding sliver
                }

                if (cell < on)
                {
                    FpdfPageobjectT seg = fpdf_edit.FPDFPageObjCreateNewPath(
                        (float)(a.X + (ux * pos)), (float)(a.Y + (uy * pos)));
                    fpdf_edit.FPDFPathLineTo(seg,
                        (float)(a.X + (ux * (pos + step))), (float)(a.Y + (uy * (pos + step))));
                    StrokePath(annot, seg, r, g, b, width, stroke: true, fill: null);
                }

                pos += step;
                carry += step;
            }
        }
    }

    private static void DrawLeader(
        FpdfAnnotationT annot, IReadOnlyList<PdfPoint> leader, uint r, uint g, uint b,
        float width, bool dashed, bool arrow)
    {
        if (leader.Count < 2)
        {
            return;
        }

        if (dashed)
        {
            DashPolyline(annot, leader.ToArray(), r, g, b, width);
        }
        else
        {
            FpdfPageobjectT path = fpdf_edit.FPDFPageObjCreateNewPath((float)leader[0].X, (float)leader[0].Y);
            for (int i = 1; i < leader.Count; i++)
            {
                fpdf_edit.FPDFPathLineTo(path, (float)leader[i].X, (float)leader[i].Y);
            }

            StrokePath(annot, path, r, g, b, width, stroke: true, fill: null);
        }

        // Arrowhead at the tip (leader[0]), aimed along the first segment — a separate solid
        // path so a dashed line still gets a clean head.
        PdfPoint tip = leader[0];
        PdfPoint next = leader[1];
        double dx = next.X - tip.X, dy = next.Y - tip.Y;
        double len = Math.Sqrt((dx * dx) + (dy * dy));
        if (arrow && len > 0.01)
        {
            dx /= len;
            dy /= len;
            const double h = 9.0;   // arrowhead length
            const double hw = 3.2;  // half-width
            double bx = tip.X + (dx * h), by = tip.Y + (dy * h);
            FpdfPageobjectT head = fpdf_edit.FPDFPageObjCreateNewPath((float)(bx - (dy * hw)), (float)(by + (dx * hw)));
            fpdf_edit.FPDFPathLineTo(head, (float)tip.X, (float)tip.Y);
            fpdf_edit.FPDFPathLineTo(head, (float)(bx + (dy * hw)), (float)(by - (dx * hw)));
            StrokePath(annot, head, r, g, b, width, stroke: true, fill: null);
        }
    }

    private static void DrawCloud(FpdfAnnotationT annot, PdfRect box, uint r, uint g, uint b, float width)
    {
        // A closed path of outward semicircular bumps along each edge of the box.
        ReadOnlySpan<PdfPoint> corners =
        [
            new(box.Left, box.Bottom), new(box.Right, box.Bottom),
            new(box.Right, box.Top), new(box.Left, box.Top),
        ];
        ReadOnlySpan<PdfPoint> outward =
        [
            new(0, -1), new(1, 0), new(0, 1), new(-1, 0),
        ];

        FpdfPageobjectT path = fpdf_edit.FPDFPageObjCreateNewPath((float)corners[0].X, (float)corners[0].Y);
        const double kappa = 0.5522847498; // circle → cubic-bezier constant

        for (int e = 0; e < 4; e++)
        {
            PdfPoint a = corners[e];
            PdfPoint c = corners[(e + 1) % 4];
            double ex = c.X - a.X, ey = c.Y - a.Y;
            double edge = Math.Sqrt((ex * ex) + (ey * ey));
            int bumps = Math.Max(1, (int)Math.Round(edge / (2 * CloudBumpRadius)));
            double ux = ex / edge, uy = ey / edge;              // along the edge
            (double nx, double ny) = (outward[e].X, outward[e].Y); // outward normal
            double seg = edge / bumps;
            double bulge = seg / 2 * kappa;

            for (int i = 0; i < bumps; i++)
            {
                double s0 = i * seg, s1 = (i + 1) * seg, mid = (s0 + s1) / 2;
                PdfPoint p0 = new(a.X + (ux * s0), a.Y + (uy * s0));
                PdfPoint p1 = new(a.X + (ux * s1), a.Y + (uy * s1));
                PdfPoint apex = new(a.X + (ux * mid) + (nx * (seg / 2)), a.Y + (uy * mid) + (ny * (seg / 2)));

                fpdf_edit.FPDFPathBezierTo(path,
                    (float)(p0.X + (nx * bulge)), (float)(p0.Y + (ny * bulge)),
                    (float)(apex.X - (ux * bulge)), (float)(apex.Y - (uy * bulge)),
                    (float)apex.X, (float)apex.Y);
                fpdf_edit.FPDFPathBezierTo(path,
                    (float)(apex.X + (ux * bulge)), (float)(apex.Y + (uy * bulge)),
                    (float)(p1.X + (nx * bulge)), (float)(p1.Y + (ny * bulge)),
                    (float)p1.X, (float)p1.Y);
            }
        }

        fpdf_edit.FPDFPathClose(path);
        StrokePath(annot, path, r, g, b, width, stroke: true, fill: null);
    }

    private static void DrawText(
        FpdfDocumentT doc, FpdfAnnotationT annot, FpdfFontT? font, PdfAnnotation a, PdfRect box)
    {
        if (font is null || font.__Instance == IntPtr.Zero || string.IsNullOrWhiteSpace(a.Contents))
        {
            return;
        }

        (uint r, uint g, uint b) = Rgb(a.TextColorArgb ?? 0xFF000000);

        double size = a.FontSize > 0 ? a.FontSize : PdfAnnotation.DefaultFontSize;
        double lineHeight = size * PdfTextWrap.LineHeightFactor;
        double maxWidth = Math.Max(4, box.Width - (2 * PdfTextWrap.Inset));
        double y = box.Top - PdfTextWrap.Inset - size;
        double minY = box.Bottom + PdfTextWrap.Inset - lineHeight;

        foreach (string line in PdfTextWrap.Wrap(a.Contents, maxWidth, size))
        {
            if (y < minY)
            {
                break;
            }

            if (line.Length > 0)
            {
                FpdfPageobjectT text = fpdf_edit.FPDFPageObjCreateTextObj(doc, font, (float)size);
                ushort[] wide = PdfTextExtractor.ToWideString(line);
                fpdf_edit.FPDFTextSetText(text, ref wide[0]);
                fpdf_edit.FPDFPageObjSetFillColor(text, r, g, b, 255);
                fpdf_edit.FPDFPageObjTransform(text, 1, 0, 0, 1, box.Left + PdfTextWrap.Inset, y);
                fpdf_annot.FPDFAnnotAppendObject(annot, text);
            }

            y -= lineHeight;
        }
    }

    private static void StrokePath(
        FpdfAnnotationT annot, FpdfPageobjectT path, uint r, uint g, uint b, float width,
        bool stroke, uint? fill)
    {
        if (stroke)
        {
            fpdf_edit.FPDFPageObjSetStrokeColor(path, r, g, b, 255);
            fpdf_edit.FPDFPageObjSetStrokeWidth(path, width);
            fpdf_edit.FPDFPageObjSetLineJoin(path, LineJoinRound);
        }

        if (fill is { } f)
        {
            (uint fr, uint fg, uint fb) = Rgb(f);
            fpdf_edit.FPDFPageObjSetFillColor(path, fr, fg, fb, (f >> 24) & 0xFF);
            fpdf_edit.FPDFPathSetDrawMode(path, DrawFillWinding, stroke ? DrawStroke : DrawFillNone);
        }
        else
        {
            fpdf_edit.FPDFPathSetDrawMode(path, DrawFillNone, DrawStroke);
        }

        fpdf_annot.FPDFAnnotAppendObject(annot, path);
    }

    private static (uint R, uint G, uint B) Rgb(uint argb) =>
        ((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);

    private static PdfRect Normalise(PdfRect r) => new(
        Math.Min(r.Left, r.Right), Math.Max(r.Top, r.Bottom),
        Math.Max(r.Left, r.Right), Math.Min(r.Top, r.Bottom));
}
