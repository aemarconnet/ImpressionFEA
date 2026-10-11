using ImpressionFEA.Core.Geometry;

namespace ImpressionFEA.Core.Mesh;

/// <summary>
/// Unstructured mesher for the remeshing step: Delaunay triangulation (Bowyer–Watson) of a counter-clockwise boundary
/// polygon plus an equilateral interior lattice, boundary-edge recovery by refinement, guarded Laplacian smoothing,
/// and finally a split of every triangle into three quadrilaterals (vertex, edge midpoints, centroid) so that the
/// locking-free Q4 penalty element can be kept.
/// </summary>
public static class TriangleMesher
{
    public sealed record Result(Vec2[] Points, int[] Triangles, int BoundaryCount);

    /// <summary>
    /// Triangulates the polygon. Boundary edges missing from the Delaunay triangulation are split (midpoint inserted)
    /// and the triangulation repeated; <paramref name="boundary"/> is updated with the inserted points and
    /// <paramref name="onInsert"/> is told where (index of the new point) so attributes can be interpolated.
    /// </summary>
    public static Result Triangulate(List<Vec2> boundary, double size, Action<int>? onInsert = null)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var pts = new List<Vec2>(boundary);
            AddInterior(boundary, size, pts);
            var tris = Delaunay(pts);
            tris = KeepInside(pts, tris, boundary);
            var missing = MissingBoundaryEdges(boundary.Count, tris);
            if (missing.Count == 0)
            {
                Refine(pts, ref tris, boundary, size);
                var p = pts.ToArray();
                Smooth(p, tris, boundary.Count);
                return new Result(p, tris.ToArray(), boundary.Count);
            }

            foreach (int k in missing.OrderByDescending(k => k))
            {
                var mid = 0.5 * (boundary[k] + boundary[(k + 1) % boundary.Count]);
                boundary.Insert(k + 1, mid);
                onInsert?.Invoke(k + 1);
            }
        }

        throw new InvalidOperationException("Boundary could not be recovered by the Delaunay mesher.");
    }

    private static void AddInterior(List<Vec2> boundary, double size, List<Vec2> pts)
    {
        double minX = boundary.Min(p => p.X), maxX = boundary.Max(p => p.X);
        double minY = boundary.Min(p => p.Y), maxY = boundary.Max(p => p.Y);
        double dy = size * Math.Sqrt(3) / 2;
        int row = 0;
        for (double y = minY + dy / 2; y < maxY; y += dy, row++)
        {
            double shift = (row % 2) * size / 2;
            for (double x = minX + size / 2 + shift; x < maxX; x += size)
            {
                var p = new Vec2(x, y);
                if (!Inside(boundary, p)) continue;
                if (DistanceToPolygon(boundary, p) < 0.6 * size) continue;
                pts.Add(p);
            }
        }
    }

    public static bool Inside(IReadOnlyList<Vec2> poly, Vec2 p)
    {
        bool c = false;
        for (int k = 0, l = poly.Count - 1; k < poly.Count; l = k++)
        {
            var a = poly[k];
            var b = poly[l];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) c = !c;
        }

        return c;
    }

    public static double DistanceToPolygon(IReadOnlyList<Vec2> poly, Vec2 p)
    {
        double d = double.MaxValue;
        for (int k = 0; k < poly.Count; k++)
        {
            var a = poly[k];
            var ab = poly[(k + 1) % poly.Count] - a;
            double t = Math.Clamp((p - a).Dot(ab) / Math.Max(ab.Dot(ab), 1e-300), 0, 1);
            d = Math.Min(d, (p - (a + t * ab)).Length);
        }

        return d;
    }

    private static List<int> Delaunay(List<Vec2> pts)
    {
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        double span = Math.Max(maxX - minX, maxY - minY) * 20 + 1;
        var c = new Vec2((minX + maxX) / 2, (minY + maxY) / 2);
        var all = new List<Vec2>(pts)
        {
            new(c.X - span, c.Y - span), new(c.X + span, c.Y - span), new(c.X, c.Y + span),
        };
        int s0 = pts.Count;
        var tris = new List<(int A, int B, int C, Vec2 Cc, double R2)> { Make(all, s0, s0 + 1, s0 + 2) };

        for (int i = 0; i < pts.Count; i++)
        {
            var p = all[i];
            var bad = new List<int>();
            for (int t = 0; t < tris.Count; t++)
            {
                if ((p - tris[t].Cc).Dot(p - tris[t].Cc) < tris[t].R2) bad.Add(t);
            }

            var edgeCount = new Dictionary<(int, int), int>();
            foreach (int t in bad)
            {
                var (a, b, cc, _, _) = tris[t];
                foreach (var (u, v) in new[] { (a, b), (b, cc), (cc, a) })
                {
                    var key = u < v ? (u, v) : (v, u);
                    edgeCount[key] = edgeCount.TryGetValue(key, out int n) ? n + 1 : 1;
                }
            }

            var hole = new List<(int, int)>();
            foreach (int t in bad)
            {
                var (a, b, cc, _, _) = tris[t];
                foreach (var (u, v) in new[] { (a, b), (b, cc), (cc, a) })
                {
                    if (edgeCount[u < v ? (u, v) : (v, u)] == 1) hole.Add((u, v));
                }
            }

            for (int k = bad.Count - 1; k >= 0; k--)
            {
                tris[bad[k]] = tris[^1];
                tris.RemoveAt(tris.Count - 1);
            }

            foreach (var (u, v) in hole) tris.Add(Make(all, u, v, i));
        }

        var result = new List<int>();
        foreach (var (a, b, cc, _, _) in tris)
        {
            if (a >= s0 || b >= s0 || cc >= s0) continue;
            result.Add(a);
            result.Add(b);
            result.Add(cc);
        }

        return result;
    }

    private static (int, int, int, Vec2, double) Make(List<Vec2> p, int a, int b, int c)
    {
        if ((p[b] - p[a]).Cross(p[c] - p[a]) < 0) (b, c) = (c, b);
        var pa = p[a];
        var pb = p[b];
        var pc = p[c];
        double d = 2 * (pa.X * (pb.Y - pc.Y) + pb.X * (pc.Y - pa.Y) + pc.X * (pa.Y - pb.Y));
        if (Math.Abs(d) < 1e-300) return (a, b, c, pa, 0);
        double a2 = pa.Dot(pa), b2 = pb.Dot(pb), c2 = pc.Dot(pc);
        var cc = new Vec2((a2 * (pb.Y - pc.Y) + b2 * (pc.Y - pa.Y) + c2 * (pa.Y - pb.Y)) / d,
                          (a2 * (pc.X - pb.X) + b2 * (pa.X - pc.X) + c2 * (pb.X - pa.X)) / d);
        return (a, b, c, cc, (pa - cc).Dot(pa - cc));
    }

    private static List<int> KeepInside(List<Vec2> pts, List<int> tris, List<Vec2> boundary)
    {
        var keep = new List<int>();
        for (int t = 0; t < tris.Count; t += 3)
        {
            var c = (1.0 / 3) * (pts[tris[t]] + pts[tris[t + 1]] + pts[tris[t + 2]]);
            double area = (pts[tris[t + 1]] - pts[tris[t]]).Cross(pts[tris[t + 2]] - pts[tris[t]]);
            if (area <= 0 || !Inside(boundary, c)) continue;
            keep.Add(tris[t]);
            keep.Add(tris[t + 1]);
            keep.Add(tris[t + 2]);
        }

        return keep;
    }

    /// <summary>Boundary edges (k, k+1) that are not edges of a kept triangle, or that are shared by two triangles.</summary>
    private static List<int> MissingBoundaryEdges(int nb, List<int> tris)
    {
        var count = new Dictionary<(int, int), int>();
        for (int t = 0; t < tris.Count; t += 3)
        {
            for (int a = 0; a < 3; a++)
            {
                int u = tris[t + a], v = tris[t + (a + 1) % 3];
                var key = u < v ? (u, v) : (v, u);
                count[key] = count.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        }

        var missing = new List<int>();
        for (int k = 0; k < nb; k++)
        {
            int u = k, v = (k + 1) % nb;
            if (!count.TryGetValue(u < v ? (u, v) : (v, u), out int n) || n != 1) missing.Add(k);
        }

        // Every single-use edge must be a boundary edge, otherwise the kept region has holes or extra pieces.
        foreach (var (key, n) in count)
        {
            if (n != 1) continue;
            int u = key.Item1, v = key.Item2;
            bool isBoundary = u < nb && v < nb && (Math.Abs(u - v) == 1 || Math.Abs(u - v) == nb - 1);
            if (!isBoundary && u < nb && !missing.Contains(u)) missing.Add(u);
        }

        return missing;
    }

    /// <summary>
    /// Delaunay refinement (Ruppert/Chew style): circumcentres of poorly shaped triangles are inserted while they lie
    /// well inside the domain and away from existing points; a pass that would break the boundary is discarded.
    /// </summary>
    private static void Refine(List<Vec2> pts, ref List<int> tris, List<Vec2> boundary, double size)
    {
        for (int pass = 0; pass < 8; pass++)
        {
            var add = new List<Vec2>();
            for (int t = 0; t < tris.Count; t += 3)
            {
                var a = pts[tris[t]];
                var b = pts[tris[t + 1]];
                var c = pts[tris[t + 2]];
                if (TriQuality(a, b, c) >= 0.6) continue;
                var cc = Circumcentre(a, b, c);
                if (!Inside(boundary, cc) || DistanceToPolygon(boundary, cc) < 0.45 * size) continue;
                bool crowded = false;
                foreach (var q in pts.Concat(add))
                {
                    if ((q - cc).Dot(q - cc) < 0.16 * size * size) { crowded = true; break; }
                }

                if (!crowded) add.Add(cc);
            }

            if (add.Count == 0) return;
            var trial = new List<Vec2>(pts);
            trial.AddRange(add);
            var trialTris = KeepInside(trial, Delaunay(trial), boundary);
            if (MissingBoundaryEdges(boundary.Count, trialTris).Count > 0) return;
            pts.Clear();
            pts.AddRange(trial);
            tris = trialTris;
        }
    }

    private static Vec2 Circumcentre(Vec2 a, Vec2 b, Vec2 c)
    {
        double d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < 1e-300) return (1.0 / 3) * (a + b + c);
        double a2 = a.Dot(a), b2 = b.Dot(b), c2 = c.Dot(c);
        return new Vec2((a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d,
                        (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d);
    }

    private static void Smooth(Vec2[] p, List<int> tris, int nb)
    {
        var nbrs = new HashSet<int>[p.Length];
        var incident = new List<int>[p.Length];
        for (int i = 0; i < p.Length; i++)
        {
            nbrs[i] = [];
            incident[i] = [];
        }

        for (int t = 0; t < tris.Count; t += 3)
        {
            for (int a = 0; a < 3; a++)
            {
                int u = tris[t + a];
                incident[u].Add(t);
                nbrs[u].Add(tris[t + (a + 1) % 3]);
                nbrs[u].Add(tris[t + (a + 2) % 3]);
            }
        }

        double MinAngleSin(int i)
        {
            double q = double.MaxValue;
            foreach (int t in incident[i]) q = Math.Min(q, TriQuality(p[tris[t]], p[tris[t + 1]], p[tris[t + 2]]));
            return q;
        }

        for (int it = 0; it < 20; it++)
        {
            for (int i = nb; i < p.Length; i++)
            {
                if (nbrs[i].Count == 0) continue;
                var avg = new Vec2(0, 0);
                foreach (int j in nbrs[i]) avg += p[j];
                avg = (1.0 / nbrs[i].Count) * avg;
                var old = p[i];
                double before = MinAngleSin(i);
                p[i] = avg;
                if (MinAngleSin(i) < Math.Min(before, 0.5)) p[i] = old;
            }
        }
    }

    /// <summary>Normalised triangle quality (1 = equilateral, ≤ 0 = inverted).</summary>
    public static double TriQuality(Vec2 a, Vec2 b, Vec2 c)
    {
        double area2 = (b - a).Cross(c - a);
        double l2 = (b - a).Dot(b - a) + (c - b).Dot(c - b) + (a - c).Dot(a - c);
        return l2 > 0 ? 2 * Math.Sqrt(3) * area2 / l2 : -1;
    }

    /// <summary>Splits each triangle into 3 quads. Returns the quad mesh, node positions and the boundary-vertex map.</summary>
    public static (QuadMesh Mesh, Vec2[] Positions) ToQuads(Result tri, Func<Vec2, bool> isAxis)
    {
        ArgumentNullException.ThrowIfNull(tri);
        ArgumentNullException.ThrowIfNull(isAxis);
        var pos = new List<Vec2>(tri.Points);
        var mid = new Dictionary<(int, int), int>();
        int Mid(int u, int v)
        {
            var key = u < v ? (u, v) : (v, u);
            if (mid.TryGetValue(key, out int m)) return m;
            pos.Add(0.5 * (tri.Points[u] + tri.Points[v]));
            mid[key] = pos.Count - 1;
            return pos.Count - 1;
        }

        var conn = new List<int>();
        var t = tri.Triangles;
        for (int k = 0; k < t.Length; k += 3)
        {
            int a = t[k], b = t[k + 1], c = t[k + 2];
            int mab = Mid(a, b), mbc = Mid(b, c), mca = Mid(c, a);
            pos.Add((1.0 / 3) * (tri.Points[a] + tri.Points[b] + tri.Points[c]));
            int g = pos.Count - 1;
            conn.AddRange([a, mab, g, mca]);
            conn.AddRange([b, mbc, g, mab]);
            conn.AddRange([c, mca, g, mbc]);
        }

        var axis = pos.Select(isAxis).ToArray();
        return (new QuadMesh(pos.Count, conn.ToArray(), axis), pos.ToArray());
    }
}
