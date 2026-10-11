namespace ImpressionFEA.Core.ThreeD;

/// <summary>
/// Immutable linear-tetrahedron mesh topology. Tets are positively oriented. Boundary faces are oriented outward;
/// faces lying on a symmetry plane are flagged (they are cut planes of the full part, not physical surface).
/// </summary>
public sealed class TetMesh
{
    public TetMesh(int nodeCount, int[] tets, bool[] onX0, bool[] onY0)
    {
        ArgumentNullException.ThrowIfNull(tets);
        NodeCount = nodeCount;
        Tets = tets;
        ElementCount = tets.Length / 4;
        OnX0 = onX0;
        OnY0 = onY0;

        var start = new int[nodeCount + 1];
        foreach (int n in tets) start[n + 1]++;
        for (int i = 0; i < nodeCount; i++) start[i + 1] += start[i];
        var fill = (int[])start.Clone();
        var list = new int[tets.Length];
        for (int e = 0; e < ElementCount; e++)
        {
            for (int a = 0; a < 4; a++) list[fill[tets[4 * e + a]]++] = e;
        }

        NodeTetStart = start;
        NodeTetList = list;

        var faces = new Dictionary<(int, int, int), (int A, int B, int C, int Owner, int Count)>();
        for (int e = 0; e < ElementCount; e++)
        {
            int a = tets[4 * e], b = tets[4 * e + 1], c = tets[4 * e + 2], d = tets[4 * e + 3];
            foreach (var (p, q, r) in new[] { (a, c, b), (a, b, d), (a, d, c), (b, c, d) })
            {
                var key = Sorted(p, q, r);
                faces[key] = faces.TryGetValue(key, out var f) ? f with { Count = f.Count + 1 } : (p, q, r, e, 1);
            }
        }

        var bf = new List<int>();
        var owner = new List<int>();
        var sym = new List<bool>();
        foreach (var f in faces.Values)
        {
            if (f.Count > 2) throw new InvalidOperationException("Tetrahedral mesh is not manifold (face shared by more than two tets).");
            if (f.Count != 1) continue;
            bf.Add(f.A);
            bf.Add(f.B);
            bf.Add(f.C);
            owner.Add(f.Owner);
            sym.Add((onX0[f.A] && onX0[f.B] && onX0[f.C]) || (onY0[f.A] && onY0[f.B] && onY0[f.C]));
        }

        BoundaryFaces = bf.ToArray();
        BoundaryFaceOwner = owner.ToArray();
        FaceIsSymmetry = sym.ToArray();
        IsSurfaceNode = new bool[nodeCount];
        for (int k = 0; k < FaceIsSymmetry.Length; k++)
        {
            if (FaceIsSymmetry[k]) continue;
            for (int a = 0; a < 3; a++) IsSurfaceNode[BoundaryFaces[3 * k + a]] = true;
        }
    }

    public int NodeCount { get; }

    public int ElementCount { get; }

    public int DofCount => 3 * NodeCount;

    /// <summary>4 node ids per tet, positively oriented.</summary>
    public int[] Tets { get; }

    public bool[] OnX0 { get; }

    public bool[] OnY0 { get; }

    public int[] NodeTetStart { get; }

    public int[] NodeTetList { get; }

    /// <summary>Boundary triangles (3 ids each), outward oriented.</summary>
    public int[] BoundaryFaces { get; }

    public int[] BoundaryFaceOwner { get; }

    public bool[] FaceIsSymmetry { get; }

    /// <summary>Node on the physical (non-symmetry) surface — candidates for die contact.</summary>
    public bool[] IsSurfaceNode { get; }

    public ReadOnlySpan<int> TetsOf(int node) => NodeTetList.AsSpan(NodeTetStart[node], NodeTetStart[node + 1] - NodeTetStart[node]);

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    public static double Volume(Vec3 a, Vec3 b, Vec3 c, Vec3 d) => (b - a).Dot((c - a).Cross(d - a)) / 6.0;

    /// <summary>Normalised quality 6√2·V / L_rms³ (1 = regular tetrahedron, ≤ 0 = inverted).</summary>
    public static double Quality(Vec3 a, Vec3 b, Vec3 c, Vec3 d)
    {
        double l2 = ((b - a).LengthSquared + (c - a).LengthSquared + (d - a).LengthSquared
                     + (c - b).LengthSquared + (d - b).LengthSquared + (d - c).LengthSquared) / 6.0;
        return l2 > 0 ? 6 * Math.Sqrt(2) * Volume(a, b, c, d) / Math.Pow(l2, 1.5) : -1;
    }
}

/// <summary>Bucket grid for point location in a tet mesh (inside tests, remapping).</summary>
public sealed class TetLocator
{
    private readonly TetMesh _mesh;
    private readonly Vec3[] _x;
    private readonly double _cell;
    private readonly Vec3 _min;
    private readonly int _nx;
    private readonly int _ny;
    private readonly int _nz;
    private readonly int[] _start;
    private readonly int[] _items;

    public TetLocator(TetMesh mesh, Vec3[] x, double cell)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(x);
        _mesh = mesh;
        _x = x;
        _cell = cell;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var p in x)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
        }

        _min = new Vec3(minX - cell, minY - cell, minZ - cell);
        _nx = (int)((maxX - _min.X) / cell) + 2;
        _ny = (int)((maxY - _min.Y) / cell) + 2;
        _nz = (int)((maxZ - _min.Z) / cell) + 2;
        var count = new int[_nx * _ny * _nz + 1];
        var t = mesh.Tets;
        void Each(int e, Action<int> act)
        {
            Vec3 lo = x[t[4 * e]], hi = lo;
            for (int a = 1; a < 4; a++)
            {
                var p = x[t[4 * e + a]];
                lo = new Vec3(Math.Min(lo.X, p.X), Math.Min(lo.Y, p.Y), Math.Min(lo.Z, p.Z));
                hi = new Vec3(Math.Max(hi.X, p.X), Math.Max(hi.Y, p.Y), Math.Max(hi.Z, p.Z));
            }

            int i0 = Cx(lo.X), i1 = Cx(hi.X), j0 = Cy(lo.Y), j1 = Cy(hi.Y), k0 = Cz(lo.Z), k1 = Cz(hi.Z);
            for (int k = k0; k <= k1; k++)
            {
                for (int j = j0; j <= j1; j++)
                {
                    for (int i = i0; i <= i1; i++) act((k * _ny + j) * _nx + i);
                }
            }
        }

        for (int e = 0; e < mesh.ElementCount; e++) Each(e, c => count[c + 1]++);
        for (int i = 0; i < count.Length - 1; i++) count[i + 1] += count[i];
        _start = (int[])count.Clone();
        _items = new int[count[^1]];
        var fill = (int[])count.Clone();
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            int ee = e;
            Each(e, c => _items[fill[c]++] = ee);
        }
    }

    private int Cx(double v) => Math.Clamp((int)((v - _min.X) / _cell), 0, _nx - 1);

    private int Cy(double v) => Math.Clamp((int)((v - _min.Y) / _cell), 0, _ny - 1);

    private int Cz(double v) => Math.Clamp((int)((v - _min.Z) / _cell), 0, _nz - 1);

    /// <summary>Containing tet and barycentric weights, or -1.</summary>
    public int Find(Vec3 p, out double w0, out double w1, out double w2, out double w3)
    {
        w0 = w1 = w2 = w3 = 0;
        int c = (Cz(p.Z) * _ny + Cy(p.Y)) * _nx + Cx(p.X);
        var t = _mesh.Tets;
        for (int k = _start[c]; k < _start[c + 1]; k++)
        {
            int e = _items[k];
            var a = _x[t[4 * e]];
            var b = _x[t[4 * e + 1]];
            var cc = _x[t[4 * e + 2]];
            var d = _x[t[4 * e + 3]];
            double v = TetMesh.Volume(a, b, cc, d);
            if (v <= 0) continue;
            double b0 = TetMesh.Volume(p, b, cc, d) / v;
            double b1 = TetMesh.Volume(a, p, cc, d) / v;
            double b2 = TetMesh.Volume(a, b, p, d) / v;
            double b3 = 1 - b0 - b1 - b2;
            const double tol = -1e-9;
            if (b0 >= tol && b1 >= tol && b2 >= tol && b3 >= tol)
            {
                (w0, w1, w2, w3) = (b0, b1, b2, b3);
                return e;
            }
        }

        return -1;
    }

    public bool Contains(Vec3 p) => Find(p, out _, out _, out _, out _) >= 0;

    /// <summary>Linear interpolation of a nodal field; nearest node when p is outside.</summary>
    public Vec3 Interpolate(Vec3 p, Vec3[] field)
    {
        int e = Find(p, out double w0, out double w1, out double w2, out double w3);
        var t = _mesh.Tets;
        if (e >= 0) return w0 * field[t[4 * e]] + w1 * field[t[4 * e + 1]] + w2 * field[t[4 * e + 2]] + w3 * field[t[4 * e + 3]];
        int best = 0;
        double dmin = double.MaxValue;
        for (int n = 0; n < _x.Length; n++)
        {
            double dd = (_x[n] - p).LengthSquared;
            if (dd < dmin) { dmin = dd; best = n; }
        }

        return field[best];
    }

    /// <summary>Containing tet, or the tet whose centroid is nearest (for points just outside after remeshing).</summary>
    public int FindNearest(Vec3 p)
    {
        int e = Find(p, out _, out _, out _, out _);
        if (e >= 0) return e;
        int c = (Cz(p.Z) * _ny + Cy(p.Y)) * _nx + Cx(p.X);
        var t = _mesh.Tets;
        double best = double.MaxValue;
        int bestE = -1;
        for (int k = _start[c]; k < _start[c + 1]; k++)
        {
            int ee = _items[k];
            var cen = 0.25 * (_x[t[4 * ee]] + _x[t[4 * ee + 1]] + _x[t[4 * ee + 2]] + _x[t[4 * ee + 3]]);
            double dd = (cen - p).LengthSquared;
            if (dd < best) { best = dd; bestE = ee; }
        }

        if (bestE >= 0) return bestE;
        for (int ee = 0; ee < _mesh.ElementCount; ee++)
        {
            var cen = 0.25 * (_x[t[4 * ee]] + _x[t[4 * ee + 1]] + _x[t[4 * ee + 2]] + _x[t[4 * ee + 3]]);
            double dd = (cen - p).LengthSquared;
            if (dd < best) { best = dd; bestE = ee; }
        }

        return bestE;
    }
}
