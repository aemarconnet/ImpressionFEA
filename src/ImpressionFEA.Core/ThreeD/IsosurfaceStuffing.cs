namespace ImpressionFEA.Core.ThreeD;

/// <summary>
/// Tetrahedral mesher after Labelle &amp; Shewchuk, "Isosurface Stuffing" (SIGGRAPH 2007): a body-centred-cubic lattice
/// is cut by the domain, lattice vertices too close to a cut point are warped onto the surface (α_long = 0.24999,
/// α_short = 0.41189), and every lattice tet that reaches into the domain is clipped and tetrahedralised. Clipped
/// polyhedra are coned from their smallest-id vertex with face diagonals chosen by the same global rule, so
/// neighbouring tets always agree on shared faces. Symmetry planes x = 0 / y = 0 are lattice planes and come out flat.
/// </summary>
public static class IsosurfaceStuffing
{
    private const double AlphaLong = 0.24999;
    private const double AlphaShort = 0.41189;

    public sealed record Result(Vec3[] Points, int[] Tets);

    public static Result Stuff(Func<Vec3, bool> inside, Vec3 min, Vec3 max, double h, bool symX, bool symY)
    {
        ArgumentNullException.ThrowIfNull(inside);
        double eps = 1e-7 * h;
        bool In(Vec3 p)
        {
            if ((symX && p.X < 0) || (symY && p.Y < 0)) return false;
            return inside(new Vec3(symX ? Math.Max(p.X, eps) : p.X, symY ? Math.Max(p.Y, eps) : p.Y, p.Z));
        }

        // Lattice offset so that no lattice vertex lies on a symmetry plane: the planes are then ordinary parts of the
        // cut surface (flattened by cut points and warping), which keeps the plane/surface creases filled.
        var off = new Vec3(0.31 * h, 0.37 * h, 0.17 * h);
        int i0 = (int)Math.Floor(min.X / h) - 1, i1 = (int)Math.Ceiling(max.X / h) + 1;
        int j0 = (int)Math.Floor(min.Y / h) - 1, j1 = (int)Math.Ceiling(max.Y / h) + 1;
        int k0 = (int)Math.Floor(min.Z / h) - 1, k1 = (int)Math.Ceiling(max.Z / h) + 1;
        int ni = i1 - i0, nj = j1 - j0, nk = k1 - k0; // cubes per axis
        int nCorner = (ni + 1) * (nj + 1) * (nk + 1);
        int nCenter = ni * nj * nk;
        int Corner(int i, int j, int k) => ((k - k0) * (nj + 1) + (j - j0)) * (ni + 1) + (i - i0);
        int Center(int i, int j, int k) => nCorner + ((k - k0) * nj + (j - j0)) * ni + (i - i0);

        var pos = new List<Vec3>(nCorner + nCenter);

        for (int k = k0; k <= k1; k++)
        {
            for (int j = j0; j <= j1; j++)
            {
                for (int i = i0; i <= i1; i++) pos.Add(new Vec3(i * h, j * h, k * h) + off);
            }
        }

        for (int k = k0; k < k1; k++)
        {
            for (int j = j0; j < j1; j++)
            {
                for (int i = i0; i < i1; i++) pos.Add(new Vec3((i + 0.5) * h, (j + 0.5) * h, (k + 0.5) * h) + off);
            }
        }

        // BCC tets: two face-adjacent cube centres + one edge of the shared face.
        var lattice = new List<int>();
        void AddPair(int c1, int c2, int a, int b, int c, int d)
        {
            lattice.AddRange([c1, c2, a, b]);
            lattice.AddRange([c1, c2, b, c]);
            lattice.AddRange([c1, c2, c, d]);
            lattice.AddRange([c1, c2, d, a]);
        }

        for (int k = k0; k < k1; k++)
        {
            for (int j = j0; j < j1; j++)
            {
                for (int i = i0; i < i1; i++)
                {
                    int c = Center(i, j, k);
                    if (i + 1 < i1)
                        AddPair(c, Center(i + 1, j, k), Corner(i + 1, j, k), Corner(i + 1, j + 1, k), Corner(i + 1, j + 1, k + 1), Corner(i + 1, j, k + 1));
                    if (j + 1 < j1)
                        AddPair(c, Center(i, j + 1, k), Corner(i, j + 1, k), Corner(i + 1, j + 1, k), Corner(i + 1, j + 1, k + 1), Corner(i, j + 1, k + 1));
                    if (k + 1 < k1)
                        AddPair(c, Center(i, j, k + 1), Corner(i, j, k + 1), Corner(i + 1, j, k + 1), Corner(i + 1, j + 1, k + 1), Corner(i, j + 1, k + 1));
                }
            }
        }

        var isCorner = new bool[pos.Count];
        for (int v = 0; v < nCorner; v++) isCorner[v] = true;
        return StuffLattice(In, pos, isCorner, lattice, h, symX, symY);
    }

    /// <summary>
    /// Graded variant: the lattice comes from a balanced octree (<see cref="GradedLattice"/>), with cells from
    /// <paramref name="hMin"/> up to hMin·2^<paramref name="levels"/> sized by <paramref name="size"/> (the wanted lattice
    /// spacing at a point). Equal-size neighbours give the usual BCC tets, size changes give transition tets; the cutting
    /// and warping are the same as for the uniform lattice.
    /// </summary>
    public static Result StuffGraded(Func<Vec3, bool> inside, Vec3 min, Vec3 max, double hMin, int levels, Func<Vec3, double> size, bool symX, bool symY)
    {
        ArgumentNullException.ThrowIfNull(inside);
        ArgumentNullException.ThrowIfNull(size);
        double eps = 1e-7 * hMin;
        bool In(Vec3 p)
        {
            if ((symX && p.X < 0) || (symY && p.Y < 0)) return false;
            return inside(new Vec3(symX ? Math.Max(p.X, eps) : p.X, symY ? Math.Max(p.Y, eps) : p.Y, p.Z));
        }

        var off = new Vec3(0.31 * hMin, 0.37 * hMin, 0.17 * hMin);
        var (pos, isCorner, lattice) = GradedLattice.Build(min, max, hMin, levels, size, off);
        return StuffLattice(In, pos, isCorner, lattice, hMin, symX, symY);
    }

    /// <summary>
    /// Cuts, warps and clips a background tet lattice to the domain. <paramref name="isCorner"/> tells cube corners
    /// from cube centres: edges between two of the same kind are "long" (α_long), corner–centre edges "short" (α_short).
    /// </summary>
    private static Result StuffLattice(Func<Vec3, bool> In, List<Vec3> pos, bool[] isCorner, List<int> lattice, double h, bool symX, bool symY)
    {
        var sign = new List<int>(pos.Count);
        foreach (var p in pos) sign.Add(In(p) ? -1 : 1);
        bool IsCorner(int v) => v < isCorner.Length && isCorner[v];

        // Cut points on lattice edges joining an inside and an outside vertex (bisection on the inside test).
        var cuts = new Dictionary<(int, int), Vec3>();
        int[] ea = [0, 0, 0, 1, 1, 2], eb = [1, 2, 3, 2, 3, 3];
        for (int t = 0; t < lattice.Count; t += 4)
        {
            for (int q = 0; q < 6; q++)
            {
                int u = lattice[t + ea[q]], w = lattice[t + eb[q]];
                if (!((sign[u] < 0 && sign[w] > 0) || (sign[u] > 0 && sign[w] < 0))) continue;
                var key = u < w ? (u, w) : (w, u);
                if (cuts.ContainsKey(key)) continue;
                Vec3 pin = sign[u] < 0 ? pos[u] : pos[w], pout = sign[u] < 0 ? pos[w] : pos[u];
                for (int it = 0; it < 16; it++)
                {
                    var m = 0.5 * (pin + pout);
                    if (In(m)) pin = m; else pout = m;
                }

                var cp = 0.5 * (pin + pout);
                if (symX && Math.Abs(cp.X) < 1e-4 * h) cp = cp with { X = 0 };
                if (symY && Math.Abs(cp.Y) < 1e-4 * h) cp = cp with { Y = 0 };
                cuts[key] = cp;
            }
        }

        // Warp lattice vertices that lie too close to a cut point onto the surface.
        var warpTo = new Dictionary<int, (Vec3 P, double D)>();
        foreach (var ((u, w), c) in cuts)
        {
            double len = (pos[w] - pos[u]).Length;
            double alpha = IsCorner(u) == IsCorner(w) ? AlphaLong : AlphaShort;
            foreach (int v in new[] { u, w })
            {
                double d = (c - pos[v]).Length;
                if (sign[v] == 0 || d >= alpha * len) continue;
                if (!warpTo.TryGetValue(v, out var cur) || d < cur.D) warpTo[v] = (c, d);
            }
        }

        foreach (var (v, (p, _)) in warpTo)
        {
            pos[v] = p;
            sign[v] = 0;
        }

        // Clip and tetrahedralise each lattice tet.
        var cutId = new Dictionary<(int, int), int>();
        int CutVertex(int u, int w)
        {
            var key = u < w ? (u, w) : (w, u);
            if (cutId.TryGetValue(key, out int id)) return id;
            pos.Add(cuts[key]);
            sign.Add(0);
            id = pos.Count - 1;
            cutId[key] = id;
            return id;
        }

        double minVol = 1e-6 * h * h * h;
        var outTets = new List<int>();
        void Emit(int a, int b, int c, int d)
        {
            double v = TetMesh.Volume(pos[a], pos[b], pos[c], pos[d]);
            if (Math.Abs(v) < minVol) return;
            if (v < 0) (c, d) = (d, c);
            outTets.AddRange([a, b, c, d]);
        }

        int[][] faces = [[0, 1, 2], [0, 1, 3], [0, 2, 3], [1, 2, 3]];
        var poly = new List<int>();
        var face = new List<int>();
        var iface = new List<int>();
        for (int t = 0; t < lattice.Count; t += 4)
        {
            int[] v = [lattice[t], lattice[t + 1], lattice[t + 2], lattice[t + 3]];
            int neg = 0, posCount = 0;
            foreach (int x in v)
            {
                if (sign[x] < 0) neg++;
                else if (sign[x] > 0) posCount++;
            }

            if (neg == 0) continue;
            if (posCount == 0)
            {
                Emit(v[0], v[1], v[2], v[3]);
                continue;
            }

            poly.Clear();
            iface.Clear();
            foreach (int x in v)
            {
                if (sign[x] <= 0) poly.Add(x);
                if (sign[x] == 0) iface.Add(x);
            }

            for (int q = 0; q < 6; q++)
            {
                int u = v[ea[q]], w = v[eb[q]];
                if ((sign[u] < 0 && sign[w] > 0) || (sign[u] > 0 && sign[w] < 0))
                {
                    int c = CutVertex(u, w);
                    poly.Add(c);
                    iface.Add(c);
                }
            }

            int apex = poly.Min();
            foreach (var f in faces)
            {
                face.Clear();
                for (int q = 0; q < 3; q++)
                {
                    int a = v[f[q]], b = v[f[(q + 1) % 3]];
                    if (sign[a] <= 0) face.Add(a);
                    if ((sign[a] < 0 && sign[b] > 0) || (sign[a] > 0 && sign[b] < 0)) face.Add(CutVertex(a, b));
                }

                ConeFace(face, apex);
            }

            if (iface.Count >= 3)
            {
                OrderPlanar(iface, poly);
                ConeFace(iface, apex);
            }
        }

        void ConeFace(List<int> f, int apex)
        {
            if (f.Count < 3 || f.Contains(apex)) return;
            int s = f.IndexOf(f.Min());
            int n = f.Count;
            for (int q = 1; q < n - 1; q++) Emit(apex, f[s], f[(s + q) % n], f[(s + q + 1) % n]);
        }

        void OrderPlanar(List<int> f, List<int> all)
        {
            var c = new Vec3(0, 0, 0);
            foreach (int x in f) c += pos[x];
            c = (1.0 / f.Count) * c;
            var inner = new Vec3(0, 0, 0);
            int cnt = 0;
            foreach (int x in all)
            {
                if (sign[x] < 0) { inner += pos[x]; cnt++; }
            }

            var nrm = (c - (1.0 / Math.Max(cnt, 1)) * inner).Normalized();
            var u = (Math.Abs(nrm.X) < 0.9 ? Vec3.UnitX : Vec3.UnitY).Cross(nrm).Normalized();
            var w = nrm.Cross(u);
            f.Sort((a, b) => Math.Atan2((pos[a] - c).Dot(w), (pos[a] - c).Dot(u)).CompareTo(Math.Atan2((pos[b] - c).Dot(w), (pos[b] - c).Dot(u))));
        }

        // Compact.
        var map = new Dictionary<int, int>();
        var pts = new List<Vec3>();
        var tets = new int[outTets.Count];
        for (int q = 0; q < outTets.Count; q++)
        {
            int old = outTets[q];
            if (!map.TryGetValue(old, out int id))
            {
                id = pts.Count;
                map[old] = id;
                pts.Add(pos[old]);
            }

            tets[q] = id;
        }

        return new Result(pts.ToArray(), tets);
    }
}
