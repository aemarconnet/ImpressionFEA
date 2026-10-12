using System.Windows.Media.Media3D;
using ImpressionFEA.Core.Geometry;
using ImpressionFEA.Core.Simulation;
using ImpressionFEA.Core.ThreeD;

namespace ImpressionFEA.App.Controls;

/// <summary>Grain flow in the 3D view: flat-cut section of a tet result with fibre lines, and 2D flow lines on cut faces.</summary>
public sealed partial class SolidViewport3D
{
    private static readonly (int A, int B)[] TetEdges = [(0, 1), (0, 2), (0, 3), (1, 2), (1, 3), (2, 3)];

    /// <summary>
    /// 2D flow lines on the open faces of the revolved part (the two cut-away planes) or of the extruded part (both
    /// ends, mirrored), lifted by <paramref name="eps"/> off the face so they draw on top; view coordinates, point pairs.
    /// </summary>
    private static List<Point3D> ProfileFlowLines(IReadOnlyList<Vec2[]> lines, bool plane, double a0, double a1, double length, double eps)
    {
        var segs = new List<Point3D>();
        void Add(Point3D p, Point3D q)
        {
            segs.Add(p);
            segs.Add(q);
        }

        foreach (var line in lines)
        {
            for (int i = 0; i + 1 < line.Length; i++)
            {
                Vec2 p = line[i], q = line[i + 1];
                if (plane)
                {
                    foreach (double z in new[] { -length / 2 - eps, length / 2 + eps })
                    {
                        foreach (double m in new[] { 1.0, -1.0 }) Add(new Point3D(m * p.X, p.Y, z), new Point3D(m * q.X, q.Y, z));
                    }
                }
                else
                {
                    // The removed wedge lies between a1 and a0 + 2π: lift each face towards it.
                    foreach (var (a, n) in new[] { (a0, new Vector3D(Math.Sin(a0), 0, -Math.Cos(a0))), (a1, new Vector3D(-Math.Sin(a1), 0, Math.Cos(a1))) })
                    {
                        double c = Math.Cos(a), s = Math.Sin(a);
                        Add(new Point3D(p.X * c, p.Y, p.X * s) + eps * n, new Point3D(q.X * c, q.Y, q.X * s) + eps * n);
                    }
                }
            }
        }

        return segs;
    }

    /// <summary>
    /// Section polygons of every tet (all mirror copies) crossing the cut plane, model coordinates, with a nodal value
    /// interpolated to the polygon corners (0 when <paramref name="value"/> is null; value(node, sx, sy)).
    /// </summary>
    private static List<(Vec3[] P, double[] V)> SliceTets(Snapshot3D s, (int Sx, int Sy)[] copies, (bool OnX, double At, double Side) pl, Func<int, int, int, double>? value)
    {
        var res = new List<(Vec3[], double[])>();
        var tets = s.Mesh.Tets;
        var p = new Vec3[4];
        var d = new double[4];
        var v = new double[4];
        var pts = new List<Vec3>(4);
        var vals = new List<double>(4);
        foreach (var (sx, sy) in copies)
        {
            for (int e = 0; e < s.Mesh.ElementCount; e++)
            {
                bool below = false, above = false;
                for (int i = 0; i < 4; i++)
                {
                    int n = tets[4 * e + i];
                    p[i] = new Vec3(sx * s.X[n], sy * s.Y[n], s.Z[n]);
                    d[i] = pl.Side * ((pl.OnX ? p[i].X : p[i].Y) - pl.At);
                    if (d[i] < 0) below = true;
                    else above = true;
                }

                if (!below || !above) continue;
                pts.Clear();
                vals.Clear();
                for (int i = 0; i < 4; i++) v[i] = value?.Invoke(tets[4 * e + i], sx, sy) ?? 0;
                foreach (var (a, b) in TetEdges)
                {
                    if ((d[a] < 0) == (d[b] < 0)) continue;
                    double t = d[a] / (d[a] - d[b]);
                    pts.Add(Vec3.Lerp(p[a], p[b], t));
                    vals.Add(v[a] + t * (v[b] - v[a]));
                }

                if (pts.Count < 3) continue;
                if (pts.Count == 4)
                {
                    // Order the quad around its centre in the plane.
                    double cu = 0, cv = 0;
                    foreach (var q in pts)
                    {
                        cu += pl.OnX ? q.Y : q.X;
                        cv += q.Z;
                    }

                    cu /= 4;
                    cv /= 4;
                    var order = Enumerable.Range(0, 4).OrderBy(k => Math.Atan2(pts[k].Z - cv, (pl.OnX ? pts[k].Y : pts[k].X) - cu)).ToArray();
                    res.Add((order.Select(k => pts[k]).ToArray(), order.Select(k => vals[k]).ToArray()));
                }
                else
                {
                    res.Add(([.. pts], [.. vals]));
                }
            }
        }

        return res;
    }

    /// <summary>
    /// Part surface cut exactly on the plane (outer faces clipped, symmetry faces left out) plus the flat section cap,
    /// with face normals; the plain backdrop for fibre lines.
    /// </summary>
    private static MeshGeometry3D BuildPartFlatCut(Snapshot3D s, (int Sx, int Sy)[] copies, (bool OnX, double At, double Side) pl)
    {
        var g = new MeshGeometry3D();
        var mesh = s.Mesh;
        var f = mesh.BoundaryFaces;
        void AddPolygon(IReadOnlyList<Vec3> poly, Vector3D n)
        {
            int i0 = g.Positions.Count;
            foreach (var q in poly)
            {
                g.Positions.Add(Map(q.X, q.Y, q.Z));
                g.Normals.Add(n);
            }

            for (int k = 1; k + 1 < poly.Count; k++) AddTri(g, i0, i0 + k, i0 + k + 1);
        }

        foreach (var (sx, sy) in copies)
        {
            bool flip = sx * sy < 0;
            for (int k = 0; k < mesh.FaceIsSymmetry.Length; k++)
            {
                if (mesh.FaceIsSymmetry[k]) continue;
                int a = f[3 * k], b = f[3 * k + 1], c = f[3 * k + 2];
                var va = new Vec3(sx * s.X[a], sy * s.Y[a], s.Z[a]);
                var vb = new Vec3(sx * s.X[b], sy * s.Y[b], s.Z[b]);
                var vc = new Vec3(sx * s.X[c], sy * s.Y[c], s.Z[c]);
                if (flip) (vb, vc) = (vc, vb);
                var n = (vb - va).Cross(vc - va);
                if (n.LengthSquared == 0) continue;
                var poly = ClipToPlane(va, vb, vc, pl.OnX, pl.At, pl.Side);
                if (poly.Count < 3) continue;
                var nv = MapN(n.X, n.Y, n.Z);
                nv.Normalize();
                AddPolygon(poly, nv);
            }
        }

        // Cap facing the removed side.
        var capModel = pl.OnX ? new Vec3(-pl.Side, 0, 0) : new Vec3(0, -pl.Side, 0);
        var capN = MapN(capModel.X, capModel.Y, capModel.Z);
        foreach (var (poly, _) in SliceTets(s, copies, pl, null))
        {
            var pp = poly;
            if ((pp[1] - pp[0]).Cross(pp[2] - pp[0]).Dot(capModel) < 0) pp = [.. pp.Reverse()];
            AddPolygon(pp, capN);
        }

        g.Freeze();
        return g;
    }

    /// <summary>
    /// Fibre lines on the section: iso-lines of the reference coordinate across the fibre and in the plane
    /// (t = plane normal × fibre), <paramref name="count"/> levels evenly across the billet, lifted by
    /// <paramref name="eps"/> off the cap; view coordinates, point pairs. Empty when the plane is across the fibre.
    /// </summary>
    private static List<Point3D> SectionFlowLines(Snapshot3D s, GrainReference3D r, (int Sx, int Sy)[] copies, (bool OnX, double At, double Side) pl,
        FiberDirection fiber, int count, double eps)
    {
        var segs = new List<Point3D>();
        var fv = fiber == FiberDirection.Horizontal ? new Vec3(1, 0, 0) : new Vec3(0, 0, 1);
        var nv = pl.OnX ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
        var tv = nv.Cross(fv);
        if (tv.LengthSquared < 0.25 || count < 1) return segs; // end grain: fibres pierce the plane

        double Value(int n, int sx, int sy) => tv.X * sx * r.X0[n] + tv.Y * sy * r.Y0[n] + tv.Z * r.Z0[n];
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var (sx, sy) in copies)
        {
            foreach (double x in new[] { r.Min.X, r.Max.X })
            {
                foreach (double y in new[] { r.Min.Y, r.Max.Y })
                {
                    foreach (double z in new[] { r.Min.Z, r.Max.Z })
                    {
                        double c = tv.X * sx * x + tv.Y * sy * y + tv.Z * z;
                        lo = Math.Min(lo, c);
                        hi = Math.Max(hi, c);
                    }
                }
            }
        }

        if (!(hi > lo)) return segs;
        double step = (hi - lo) / count;
        var lift = pl.OnX ? new Vec3(-pl.Side * eps, 0, 0) : new Vec3(0, -pl.Side * eps, 0);
        Point3D V(Vec3 p) => Map(p.X + lift.X, p.Y + lift.Y, p.Z + lift.Z);
        var cross = new List<Vec3>(3);
        foreach (var (poly, val) in SliceTets(s, copies, pl, Value))
        {
            for (int k = 1; k + 1 < poly.Length; k++)
            {
                Vec3[] tp = [poly[0], poly[k], poly[k + 1]];
                double[] tvv = [val[0], val[k], val[k + 1]];
                double vmin = Math.Min(tvv[0], Math.Min(tvv[1], tvv[2])), vmax = Math.Max(tvv[0], Math.Max(tvv[1], tvv[2]));
                int i0 = Math.Max(0, (int)Math.Ceiling((vmin - lo) / step - 0.5));
                int i1 = Math.Min(count - 1, (int)Math.Floor((vmax - lo) / step - 0.5));
                for (int i = i0; i <= i1; i++)
                {
                    double c = lo + (i + 0.5) * step;
                    cross.Clear();
                    for (int a = 0; a < 3; a++)
                    {
                        int b = (a + 1) % 3;
                        if ((tvv[a] > c) == (tvv[b] > c)) continue;
                        cross.Add(Vec3.Lerp(tp[a], tp[b], (c - tvv[a]) / (tvv[b] - tvv[a])));
                    }

                    if (cross.Count != 2) continue;
                    segs.Add(V(cross[0]));
                    segs.Add(V(cross[1]));
                }
            }
        }

        return segs;
    }
}
