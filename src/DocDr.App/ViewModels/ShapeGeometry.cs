using System;
using System.Windows;
using System.Windows.Media;

namespace DocDr.App.ViewModels;

/// <summary>
/// Builds the on-screen geometry for the stamp-backed shape annotations. The revision-cloud
/// scallops mirror <c>PdfStampAppearance.DrawCloud</c> in DocDr.Pdf (kept in step by eye) so the
/// overlay and the saved appearance look the same.
/// </summary>
public static class ShapeGeometry
{
    private const double Kappa = 0.5522847498; // circle → cubic-bezier control-point factor

    /// <summary>A closed scalloped outline around <paramref name="box"/> (device/DIP space, y-down).</summary>
    public static Geometry Cloud(Rect box, double bumpRadius)
    {
        bumpRadius = Math.Max(3, bumpRadius);

        // Corners clockwise on screen (y-down): TL, TR, BR, BL; outward normal per following edge.
        Point[] corners =
        [
            new(box.Left, box.Top), new(box.Right, box.Top),
            new(box.Right, box.Bottom), new(box.Left, box.Bottom),
        ];
        (double X, double Y)[] outward = [(0, -1), (1, 0), (0, 1), (-1, 0)];

        var geo = new StreamGeometry();
        using (StreamGeometryContext ctx = geo.Open())
        {
            ctx.BeginFigure(corners[0], isFilled: false, isClosed: true);

            for (int e = 0; e < 4; e++)
            {
                Point a = corners[e];
                Point c = corners[(e + 1) % 4];
                double ex = c.X - a.X, ey = c.Y - a.Y;
                double edge = Math.Sqrt((ex * ex) + (ey * ey));
                if (edge < 1)
                {
                    continue;
                }

                int bumps = Math.Max(1, (int)Math.Round(edge / (2 * bumpRadius)));
                double ux = ex / edge, uy = ey / edge;
                (double nx, double ny) = outward[e];
                double seg = edge / bumps;
                double bulge = seg / 2 * Kappa;

                for (int i = 0; i < bumps; i++)
                {
                    double s0 = i * seg, s1 = (i + 1) * seg, mid = (s0 + s1) / 2;
                    var p0 = new Point(a.X + (ux * s0), a.Y + (uy * s0));
                    var p1 = new Point(a.X + (ux * s1), a.Y + (uy * s1));
                    var apex = new Point(a.X + (ux * mid) + (nx * (seg / 2)), a.Y + (uy * mid) + (ny * (seg / 2)));

                    ctx.BezierTo(
                        new Point(p0.X + (nx * bulge), p0.Y + (ny * bulge)),
                        new Point(apex.X - (ux * bulge), apex.Y - (uy * bulge)),
                        apex, isStroked: true, isSmoothJoin: false);
                    ctx.BezierTo(
                        new Point(apex.X + (ux * bulge), apex.Y + (uy * bulge)),
                        new Point(p1.X + (nx * bulge), p1.Y + (ny * bulge)),
                        p1, isStroked: true, isSmoothJoin: false);
                }
            }
        }

        geo.Freeze();
        return geo;
    }
}
