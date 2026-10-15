namespace ImpressionFEA.Core.ThreeD;

/// <summary>
/// Graded body-centred-cubic background lattice for isosurface stuffing (after the graded variant of Labelle &amp;
/// Shewchuk, SIGGRAPH 2007). A balanced octree covers the box: cell sizes run from hMin to hMin·2^levels, chosen by a
/// size field, and any two cells that touch differ by at most one level. Lattice vertices are the cell corners and
/// cell centres. Every tetrahedron joins the centres of two face-adjacent cells to one segment of their shared face:
/// equal cells give the regular BCC tets, a large cell next to four smaller ones gives transition tets from the large
/// centre to each small face. Shared faces are split identically from both sides (segments include the midpoints left
/// by smaller cells), so the lattice is conforming.
/// </summary>
public static class GradedLattice
{
    private readonly record struct Cell(int Level, int I, int J, int K);

    /// <summary>Builds the lattice: positions, corner/centre flags and tetrahedra (4 indices each, unoriented).</summary>
    public static (List<Vec3> Pos, bool[] IsCorner, List<int> Tets) Build(Vec3 min, Vec3 max, double hMin, int levels, Func<Vec3, double> size, Vec3 offset)
    {
        if (!(hMin > 0)) throw new ArgumentOutOfRangeException(nameof(hMin));
        levels = Math.Clamp(levels, 0, 6);
        int top = 1 << levels; // top-level cell edge in units of hMin

        // Top-level grid (in finest units) covering the box with one cell of margin.
        int i0 = (int)Math.Floor((min.X - offset.X) / (hMin * top)) - 1, i1 = (int)Math.Ceiling((max.X - offset.X) / (hMin * top)) + 1;
        int j0 = (int)Math.Floor((min.Y - offset.Y) / (hMin * top)) - 1, j1 = (int)Math.Ceiling((max.Y - offset.Y) / (hMin * top)) + 1;
        int k0 = (int)Math.Floor((min.Z - offset.Z) / (hMin * top)) - 1, k1 = (int)Math.Ceiling((max.Z - offset.Z) / (hMin * top)) + 1;
        Vec3 At(double fi, double fj, double fk) => new Vec3(fi * hMin, fj * hMin, fk * hMin) + offset;
        int Span(int level) => 1 << (levels - level); // cell edge in finest units

        // 1. Subdivide by the size field (smallest wanted size over the cell centre and corners).
        var leaves = new HashSet<Cell>();
        var stack = new Stack<Cell>();
        for (int k = k0; k < k1; k++)
        {
            for (int j = j0; j < j1; j++)
            {
                for (int i = i0; i < i1; i++) stack.Push(new Cell(0, i, j, k));
            }
        }

        while (stack.Count > 0)
        {
            var c = stack.Pop();
            int s = Span(c.Level);
            double edge = s * hMin, want = double.PositiveInfinity;
            for (int q = 0; q < 9; q++)
            {
                double fx = q == 8 ? 0.5 : q & 1, fy = q == 8 ? 0.5 : (q >> 1) & 1, fz = q == 8 ? 0.5 : (q >> 2) & 1;
                want = Math.Min(want, size(At((c.I + fx) * s, (c.J + fy) * s, (c.K + fz) * s)));
            }

            if (c.Level < levels && want < 0.999 * edge) Split(c, stack);
            else leaves.Add(c);
        }

        // 2. Balance: cells that touch differ by at most one level.
        Cell? LeafAt(int fi, int fj, int fk)
        {
            for (int l = 0; l <= levels; l++)
            {
                int s = Span(l);
                var c = new Cell(l, FloorDiv(fi, s), FloorDiv(fj, s), FloorDiv(fk, s));
                if (leaves.Contains(c)) return c;
            }

            return null;
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var c in leaves.ToList())
            {
                if (!leaves.Contains(c) || c.Level >= levels - 1) continue;
                int s = Span(c.Level);
                bool tooFine = false;
                // Finest cells in a one-cell shell around c.
                for (int a = -1; a <= s && !tooFine; a++)
                {
                    for (int b = -1; b <= s && !tooFine; b++)
                    {
                        for (int d = -1; d <= s && !tooFine; d++)
                        {
                            if (a >= 0 && a < s && b >= 0 && b < s && d >= 0 && d < s) continue;
                            if (LeafAt(c.I * s + a, c.J * s + b, c.K * s + d) is { } n && n.Level > c.Level + 1) tooFine = true;
                        }
                    }
                }

                if (!tooFine) continue;
                leaves.Remove(c);
                var kids = new Stack<Cell>();
                Split(c, kids);
                foreach (var kid in kids) leaves.Add(kid);
                changed = true;
            }
        }

        // 3. Vertices: corners at even doubled finest coordinates, centres at odd ones.
        var pos = new List<Vec3>();
        var corner = new List<bool>();
        var ids = new Dictionary<(long, long, long), int>();
        int Vertex(long di, long dj, long dk, bool isCorner)
        {
            var key = (di, dj, dk);
            if (ids.TryGetValue(key, out int id)) return id;
            id = pos.Count;
            ids[key] = id;
            pos.Add(At(di / 2.0, dj / 2.0, dk / 2.0));
            corner.Add(isCorner);
            return id;
        }

        foreach (var c in leaves)
        {
            int s = Span(c.Level);
            for (int q = 0; q < 8; q++) Vertex(2L * (c.I + (q & 1)) * s, 2L * (c.J + ((q >> 1) & 1)) * s, 2L * (c.K + ((q >> 2) & 1)) * s, true);
        }

        int Center(Cell c)
        {
            int s = Span(c.Level);
            return Vertex((2L * c.I + 1) * s, (2L * c.J + 1) * s, (2L * c.K + 1) * s, false);
        }

        // 4. Tets across every face: from the finer (or, for equal cells, the lower) side.
        var tets = new List<int>();
        int[][] axes = [[1, 2], [2, 0], [0, 1]]; // in-face axes for faces normal to x, y, z
        foreach (var c in leaves.ToList())
        {
            int s = Span(c.Level);
            int cc = Center(c);
            for (int axis = 0; axis < 3; axis++)
            {
                for (int dir = -1; dir <= 1; dir += 2)
                {
                    // A finest cell just across the face, at the face's low corner.
                    int[] lo = [c.I * s, c.J * s, c.K * s];
                    int[] probe = (int[])lo.Clone();
                    probe[axis] = dir > 0 ? lo[axis] + s : lo[axis] - 1;
                    if (LeafAt(probe[0], probe[1], probe[2]) is not { } n) continue;
                    if (n.Level < c.Level) continue; // the larger neighbour emits these
                    if (n.Level == c.Level && dir < 0) continue; // equal cells: emitted once, from the lower one

                    // Face plane coordinate (doubled finest units) and the sub-faces to fill.
                    long plane = 2L * (dir > 0 ? lo[axis] + s : lo[axis]);
                    int sub = n.Level > c.Level ? 2 : 1, ss = s / sub;
                    int ua = axes[axis][0], ub = axes[axis][1];
                    for (int p = 0; p < sub; p++)
                    {
                        for (int r = 0; r < sub; r++)
                        {
                            // The neighbour cell behind this sub-face.
                            int[] pr = (int[])probe.Clone();
                            pr[ua] = lo[ua] + p * ss;
                            pr[ub] = lo[ub] + r * ss;
                            if (LeafAt(pr[0], pr[1], pr[2]) is not { } d) continue;
                            int cd = Center(d);

                            // Face polygon: corners and, where they exist, edge midpoints, in order.
                            long a0 = 2L * (lo[ua] + p * ss), b0 = 2L * (lo[ub] + r * ss), w = 2L * ss;
                            var ring = new List<int>(8);
                            (long, long)[] path = [(a0, b0), (a0 + w / 2, b0), (a0 + w, b0), (a0 + w, b0 + w / 2), (a0 + w, b0 + w), (a0 + w / 2, b0 + w), (a0, b0 + w), (a0, b0 + w / 2)];
                            for (int q = 0; q < 8; q++)
                            {
                                var (ga, gb) = path[q];
                                long[] key = new long[3];
                                key[axis] = plane;
                                key[ua] = ga;
                                key[ub] = gb;
                                bool mid = (q & 1) == 1;
                                if (mid && (ss == 1 || !ids.TryGetValue((key[0], key[1], key[2]), out _))) continue; // no midpoint vertex
                                if (ids.TryGetValue((key[0], key[1], key[2]), out int vid)) ring.Add(vid);
                            }

                            for (int q = 0; q < ring.Count; q++) tets.AddRange([cc, cd, ring[q], ring[(q + 1) % ring.Count]]);
                        }
                    }
                }
            }
        }

        return (pos, corner.ToArray(), tets);
    }

    private static void Split(Cell c, Stack<Cell> into)
    {
        for (int q = 0; q < 8; q++) into.Push(new Cell(c.Level + 1, 2 * c.I + (q & 1), 2 * c.J + ((q >> 1) & 1), 2 * c.K + ((q >> 2) & 1)));
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
}
