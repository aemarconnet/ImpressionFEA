using ImpressionFEA.Core.Geometry;

namespace ImpressionFEA.Core.Mesh;

/// <summary>
/// Immutable 4-node quadrilateral mesh topology of the billet half-section (structured at start, unstructured after
/// remeshing). Elements are counter-clockwise. Nodes on the symmetry axis (x = 0) are flagged. Equations are numbered
/// by reverse Cuthill–McKee to keep the band narrow.
/// </summary>
public sealed class QuadMesh
{
    public QuadMesh(int nodeCount, int[] connectivity, bool[] isAxis)
    {
        ArgumentNullException.ThrowIfNull(connectivity);
        ArgumentNullException.ThrowIfNull(isAxis);
        if (connectivity.Length % 4 != 0) throw new ArgumentException("Connectivity must hold 4 nodes per element.", nameof(connectivity));
        NodeCount = nodeCount;
        ElementCount = connectivity.Length / 4;
        Connectivity = connectivity;
        IsAxis = isAxis;
        (NodeEquation, HalfBandwidth) = NumberEquations();
        (BoundaryLoop, BoundaryEdgeOwner) = TraceBoundary();
    }

    public int NodeCount { get; }

    public int ElementCount { get; }

    public int DofCount => 2 * NodeCount;

    /// <summary>4 node ids per element, counter-clockwise.</summary>
    public int[] Connectivity { get; }

    public bool[] IsAxis { get; }

    /// <summary>Equation block of each node; DOF = 2·eq + component.</summary>
    public int[] NodeEquation { get; }

    /// <summary>Half-bandwidth in DOFs.</summary>
    public int HalfBandwidth { get; }

    /// <summary>Closed counter-clockwise loop of boundary nodes, starting at an axis node.</summary>
    public int[] BoundaryLoop { get; }

    /// <summary>Element owning edge BoundaryLoop[k] → BoundaryLoop[k+1 mod n].</summary>
    public int[] BoundaryEdgeOwner { get; }

    public bool IsAxisNode(int n) => IsAxis[n];

    /// <summary>Structured nx × ny grid over [0, r] × [y0, y0 + h].</summary>
    public static (QuadMesh Mesh, Vec2[] Positions) Structured(int nx, int ny, double r, double y0, double h)
    {
        int Node(int i, int j) => j * (nx + 1) + i;
        int nn = (nx + 1) * (ny + 1);
        var x = new Vec2[nn];
        var axis = new bool[nn];
        for (int j = 0; j <= ny; j++)
        {
            for (int i = 0; i <= nx; i++)
            {
                x[Node(i, j)] = new Vec2(r * i / nx, y0 + h * j / ny);
                axis[Node(i, j)] = i == 0;
            }
        }

        var conn = new int[nx * ny * 4];
        int e = 0;
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                conn[e++] = Node(i, j);
                conn[e++] = Node(i + 1, j);
                conn[e++] = Node(i + 1, j + 1);
                conn[e++] = Node(i, j + 1);
            }
        }

        return (new QuadMesh(nn, conn, axis), x);
    }

    private (int[] Eq, int Band) NumberEquations()
    {
        var adj = new HashSet<int>[NodeCount];
        for (int n = 0; n < NodeCount; n++) adj[n] = [];
        for (int e = 0; e < ElementCount; e++)
        {
            for (int a = 0; a < 4; a++)
            {
                for (int b = 0; b < 4; b++)
                {
                    if (a != b) adj[Connectivity[4 * e + a]].Add(Connectivity[4 * e + b]);
                }
            }
        }

        // Cuthill–McKee from a pseudo-peripheral node, then reversed.
        var order = new List<int>(NodeCount);
        var seen = new bool[NodeCount];
        while (order.Count < NodeCount)
        {
            int start = -1;
            for (int n = 0; n < NodeCount; n++)
            {
                if (!seen[n] && (start < 0 || adj[n].Count < adj[start].Count)) start = n;
            }

            start = Peripheral(start, adj);
            var queue = new Queue<int>();
            queue.Enqueue(start);
            seen[start] = true;
            while (queue.Count > 0)
            {
                int n = queue.Dequeue();
                order.Add(n);
                foreach (int m in adj[n].Where(m => !seen[m]).OrderBy(m => adj[m].Count))
                {
                    seen[m] = true;
                    queue.Enqueue(m);
                }
            }
        }

        var eq = new int[NodeCount];
        for (int k = 0; k < NodeCount; k++) eq[order[NodeCount - 1 - k]] = k;
        int band = 1;
        for (int e = 0; e < ElementCount; e++)
        {
            int lo = int.MaxValue, hi = int.MinValue;
            for (int a = 0; a < 4; a++)
            {
                int q = eq[Connectivity[4 * e + a]];
                lo = Math.Min(lo, q);
                hi = Math.Max(hi, q);
            }

            band = Math.Max(band, 2 * (hi - lo) + 1);
        }

        return (eq, band);
    }

    private static int Peripheral(int start, HashSet<int>[] adj)
    {
        int node = start;
        int bestDepth = -1;
        for (int pass = 0; pass < 4; pass++)
        {
            var depth = new Dictionary<int, int> { [node] = 0 };
            var q = new Queue<int>();
            q.Enqueue(node);
            int last = node;
            while (q.Count > 0)
            {
                int n = q.Dequeue();
                last = n;
                foreach (int m in adj[n])
                {
                    if (depth.ContainsKey(m)) continue;
                    depth[m] = depth[n] + 1;
                    q.Enqueue(m);
                }
            }

            if (depth[last] <= bestDepth) break;
            bestDepth = depth[last];
            node = last;
        }

        return node;
    }

    private (int[] Loop, int[] Owner) TraceBoundary()
    {
        var count = new Dictionary<(int, int), int>();
        for (int e = 0; e < ElementCount; e++)
        {
            for (int a = 0; a < 4; a++)
            {
                int p = Connectivity[4 * e + a], q = Connectivity[4 * e + (a + 1) % 4];
                var key = p < q ? (p, q) : (q, p);
                count[key] = count.TryGetValue(key, out int c) ? c + 1 : 1;
            }
        }

        var next = new Dictionary<int, (int To, int Elem)>();
        for (int e = 0; e < ElementCount; e++)
        {
            for (int a = 0; a < 4; a++)
            {
                int p = Connectivity[4 * e + a], q = Connectivity[4 * e + (a + 1) % 4];
                if (count[p < q ? (p, q) : (q, p)] != 1) continue;
                if (!next.TryAdd(p, (q, e)))
                    throw new InvalidOperationException("Mesh boundary is not a simple closed loop (pinched node).");
            }
        }

        if (next.Count == 0) throw new InvalidOperationException("Mesh has no boundary.");
        // Start at the boundary node with the smallest y among axis nodes (or any node if none is on the axis).
        int start = next.Keys.First();
        foreach (int n in next.Keys)
        {
            if (IsAxis[n] && (!IsAxis[start] || n < start)) start = n;
        }

        var loop = new List<int>();
        var owner = new List<int>();
        int cur = start;
        do
        {
            loop.Add(cur);
            var (to, el) = next[cur];
            owner.Add(el);
            cur = to;
            if (loop.Count > next.Count) throw new InvalidOperationException("Mesh boundary consists of several loops.");
        }
        while (cur != start);

        if (loop.Count != next.Count) throw new InvalidOperationException("Mesh boundary consists of several loops.");
        return (loop.ToArray(), owner.ToArray());
    }
}
