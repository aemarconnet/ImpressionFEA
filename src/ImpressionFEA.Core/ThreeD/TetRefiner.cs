namespace ImpressionFEA.Core.ThreeD;

/// <summary>
/// Local refinement of a tetrahedral mesh by longest-edge bisection. Splitting an edge splits every tet around it,
/// so the mesh stays conforming without closure rules, and longest-edge bisection keeps element shape bounded
/// (Rivara). Midpoints of edges on symmetry planes stay exactly on the plane; midpoints of surface edges lie on the
/// (faceted) surface.
/// </summary>
public static class TetRefiner
{
    /// <summary>
    /// Bisects edges until every tet's longest edge is at most <paramref name="targetSize"/> at its centroid, or the
    /// node budget is reached. Tets stay positively oriented.
    /// </summary>
    public static (Vec3[] Points, int[] Tets, int Splits) Refine(Vec3[] points, int[] tets, Func<Vec3, double> targetSize, int maxNodes)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(tets);
        ArgumentNullException.ThrowIfNull(targetSize);
        var pts = new List<Vec3>(points);
        var tl = new List<int>(tets);
        var edges = new Dictionary<long, List<int>>();
        int nt = tl.Count / 4;
        for (int e = 0; e < nt; e++) AddEdges(e);
        int splits = 0;

        for (int pass = 0; pass < 12 && pts.Count < maxNodes; pass++)
        {
            var todo = new List<(double Len, int A, int B)>();
            for (int e = 0; e < tl.Count / 4; e++)
            {
                var (len, a, b) = Longest(e);
                var c = 0.25 * (pts[tl[4 * e]] + pts[tl[4 * e + 1]] + pts[tl[4 * e + 2]] + pts[tl[4 * e + 3]]);
                if (len > 1.05 * targetSize(c)) todo.Add((len, a, b));
            }

            if (todo.Count == 0) break;
            todo.Sort((x, y) => y.Len.CompareTo(x.Len));
            int done = 0;
            foreach (var (_, a, b) in todo)
            {
                if (pts.Count >= maxNodes) break;
                if (!edges.TryGetValue(Key(a, b), out var around) || around.Count == 0) continue; // already split
                done += RefineEdge(a, b);
            }

            splits += done;
            if (done == 0) break;
        }

        return (pts.ToArray(), tl.ToArray(), splits);

        (double Len, int A, int B) Longest(int e)
        {
            double best = -1;
            int ba = 0, bb = 0;
            for (int i = 0; i < 4; i++)
            {
                for (int j = i + 1; j < 4; j++)
                {
                    int a = tl[4 * e + i], b = tl[4 * e + j];
                    double l = (pts[a] - pts[b]).LengthSquared;
                    // Deterministic tie-break on node ids keeps neighbouring tets choosing the same edge.
                    if (l > best * (1 + 1e-12) || (Math.Abs(l - best) <= best * 1e-12 && Key(a, b) < Key(ba, bb)))
                    {
                        best = l;
                        ba = a;
                        bb = b;
                    }
                }
            }

            return (Math.Sqrt(best), ba, bb);
        }

        // Longest-edge propagation path (Rivara): an edge is bisected only once it is the longest edge of every tet
        // around it; otherwise the neighbours' longest edges are bisected first. This bounds element degradation.
        int RefineEdge(int a0, int b0)
        {
            var stack = new Stack<(int, int)>();
            stack.Push((a0, b0));
            int count = 0;
            while (stack.Count > 0 && pts.Count < maxNodes && stack.Count < 64)
            {
                var (a, b) = stack.Peek();
                if (!edges.TryGetValue(Key(a, b), out var around) || around.Count == 0)
                {
                    stack.Pop();
                    continue;
                }

                bool deferred = false;
                foreach (int f in around)
                {
                    var (_, fa, fb) = Longest(f);
                    if (Key(fa, fb) == Key(a, b)) continue;
                    stack.Push((fa, fb));
                    deferred = true;
                    break;
                }

                if (deferred) continue;
                stack.Pop();
                Split(a, b, around);
                count++;
            }

            return count;
        }

        void Split(int a, int b, List<int> around)
        {
            int m = pts.Count;
            pts.Add(0.5 * (pts[a] + pts[b]));
            foreach (int e in around.ToArray())
            {
                RemoveEdges(e);
                int newTet = tl.Count / 4;
                // Copy, then replace b by m in the original and a by m in the copy (orientation is preserved).
                for (int i = 0; i < 4; i++) tl.Add(tl[4 * e + i]);
                for (int i = 0; i < 4; i++)
                {
                    if (tl[4 * e + i] == b) tl[4 * e + i] = m;
                    if (tl[4 * newTet + i] == a) tl[4 * newTet + i] = m;
                }

                AddEdges(e);
                AddEdges(newTet);
            }

            edges.Remove(Key(a, b));
        }

        void AddEdges(int e)
        {
            for (int i = 0; i < 4; i++)
            {
                for (int j = i + 1; j < 4; j++)
                {
                    long k = Key(tl[4 * e + i], tl[4 * e + j]);
                    if (!edges.TryGetValue(k, out var list)) edges[k] = list = new List<int>(6);
                    list.Add(e);
                }
            }
        }

        void RemoveEdges(int e)
        {
            for (int i = 0; i < 4; i++)
            {
                for (int j = i + 1; j < 4; j++)
                {
                    if (edges.TryGetValue(Key(tl[4 * e + i], tl[4 * e + j]), out var list)) list.Remove(e);
                }
            }
        }
    }

    private static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
}
