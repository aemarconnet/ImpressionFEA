namespace ImpressionFEA.Core.ThreeD;

/// <summary>
/// Closed triangulated solid (e.g. a tessellated CAD body) with spatial acceleration: inside test by ray parity along
/// +z on an xy bucket grid, closest-point queries on a 3D bucket grid, and vertical ray hits for surface heights.
/// Triangles are oriented outward.
/// </summary>
public sealed class TriMeshSolid
{
    private const double JitterX = 1.2345e-7, JitterY = 2.3456e-7;
    private readonly int[][] _xy;
    private readonly int _nx2, _ny2;
    private readonly double _c2;
    private readonly int[][] _g3;
    private readonly int _gx, _gy, _gz;
    private readonly double _c3;

    public TriMeshSolid(Vec3[] vertices, int[] triangles)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(triangles);
        Vertices = vertices;
        Triangles = triangles;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var p in vertices)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
        }

        Min = new Vec3(minX, minY, minZ);
        Max = new Vec3(maxX, maxY, maxZ);
        int nt = triangles.Length / 3;
        double ext = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
        // Bucket size from the bounding box and triangle count (≈ a few triangles per bucket).
        _c2 = Math.Max(ext / 200, Math.Sqrt((maxX - minX + 1e-9) * (maxY - minY + 1e-9) / Math.Max(nt, 1)) * 2);
        _nx2 = Math.Max(1, (int)Math.Ceiling((maxX - minX) / _c2) + 1);
        _ny2 = Math.Max(1, (int)Math.Ceiling((maxY - minY) / _c2) + 1);
        _c3 = Math.Max(ext / 120, Math.Cbrt((maxX - minX + 1e-9) * (maxY - minY + 1e-9) * (maxZ - minZ + 1e-9) / Math.Max(nt, 1)) * 2);
        _gx = Math.Max(1, (int)Math.Ceiling((maxX - minX) / _c3) + 1);
        _gy = Math.Max(1, (int)Math.Ceiling((maxY - minY) / _c3) + 1);
        _gz = Math.Max(1, (int)Math.Ceiling((maxZ - minZ) / _c3) + 1);
        var xy = new List<int>[_nx2 * _ny2];
        var g3 = new List<int>[_gx * _gy * _gz];
        double area = 0, vol6 = 0;
        for (int t = 0; t < nt; t++)
        {
            var a = vertices[triangles[3 * t]];
            var b = vertices[triangles[3 * t + 1]];
            var c = vertices[triangles[3 * t + 2]];
            area += 0.5 * (b - a).Cross(c - a).Length;
            vol6 += a.Dot(b.Cross(c));
            double lx = Math.Min(a.X, Math.Min(b.X, c.X)), hx = Math.Max(a.X, Math.Max(b.X, c.X));
            double ly = Math.Min(a.Y, Math.Min(b.Y, c.Y)), hy = Math.Max(a.Y, Math.Max(b.Y, c.Y));
            double lz = Math.Min(a.Z, Math.Min(b.Z, c.Z)), hz = Math.Max(a.Z, Math.Max(b.Z, c.Z));
            for (int j = Cy2(ly); j <= Cy2(hy); j++)
            {
                for (int i = Cx2(lx); i <= Cx2(hx); i++) (xy[j * _nx2 + i] ??= []).Add(t);
            }

            for (int k = C3(lz, minZ, _gz); k <= C3(hz, minZ, _gz); k++)
            {
                for (int j = C3(ly, minY, _gy); j <= C3(hy, minY, _gy); j++)
                {
                    for (int i = C3(lx, minX, _gx); i <= C3(hx, minX, _gx); i++) (g3[(k * _gy + j) * _gx + i] ??= []).Add(t);
                }
            }
        }

        _xy = xy.Select(l => l?.ToArray() ?? []).ToArray();
        _g3 = g3.Select(l => l?.ToArray() ?? []).ToArray();
        SurfaceArea = area;
        Volume = vol6 / 6;
    }

    public Vec3[] Vertices { get; }

    public int[] Triangles { get; }

    public Vec3 Min { get; }

    public Vec3 Max { get; }

    public double Volume { get; }

    public double SurfaceArea { get; }

    private int Cx2(double x) => Math.Clamp((int)((x - Min.X) / _c2), 0, _nx2 - 1);

    private int Cy2(double y) => Math.Clamp((int)((y - Min.Y) / _c2), 0, _ny2 - 1);

    private int C3(double v, double lo, int n) => Math.Clamp((int)((v - lo) / _c3), 0, n - 1);

    /// <summary>z of every intersection of the vertical line through (x, y) with the surface.</summary>
    public void VerticalHits(double x, double y, List<double> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        hits.Clear();
        if (x < Min.X || x > Max.X || y < Min.Y || y > Max.Y) return;
        x += JitterX;
        y += JitterY;
        foreach (int t in _xy[Cy2(y) * _nx2 + Cx2(x)])
        {
            var a = Vertices[Triangles[3 * t]];
            var b = Vertices[Triangles[3 * t + 1]];
            var c = Vertices[Triangles[3 * t + 2]];
            double d = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
            if (Math.Abs(d) < 1e-300) continue;
            double u = ((x - a.X) * (c.Y - a.Y) - (c.X - a.X) * (y - a.Y)) / d;
            double v = ((b.X - a.X) * (y - a.Y) - (x - a.X) * (b.Y - a.Y)) / d;
            if (u < 0 || v < 0 || u + v > 1) continue;
            hits.Add(a.Z + u * (b.Z - a.Z) + v * (c.Z - a.Z));
        }
    }

    [ThreadStatic]
    private static List<double>? _scratch;

    /// <summary>Point inside the solid (odd number of surface crossings above it).</summary>
    public bool Contains(Vec3 p)
    {
        if (p.X < Min.X || p.X > Max.X || p.Y < Min.Y || p.Y > Max.Y || p.Z < Min.Z || p.Z > Max.Z) return false;
        var hits = _scratch ??= [];
        VerticalHits(p.X, p.Y, hits);
        int above = 0;
        foreach (double z in hits)
        {
            if (z > p.Z) above++;
        }

        return (above & 1) == 1;
    }

    /// <summary>Closest surface point within <paramref name="maxDistance"/>; returns false if none.</summary>
    public bool Closest(Vec3 p, double maxDistance, out Vec3 point, out Vec3 normal, out double distance)
    {
        point = p;
        normal = Vec3.UnitZ;
        distance = double.PositiveInfinity;
        int ci = (int)Math.Floor((p.X - Min.X) / _c3), cj = (int)Math.Floor((p.Y - Min.Y) / _c3), ck = (int)Math.Floor((p.Z - Min.Z) / _c3);
        int maxRing = (int)Math.Ceiling(maxDistance / _c3) + 1;
        double best = maxDistance * maxDistance;
        for (int ring = 0; ring <= maxRing; ring++)
        {
            for (int k = ck - ring; k <= ck + ring; k++)
            {
                if (k < 0 || k >= _gz) continue;
                for (int j = cj - ring; j <= cj + ring; j++)
                {
                    if (j < 0 || j >= _gy) continue;
                    // Only the shell of the ring: interior (j, k) rows contribute their two end cells.
                    bool shellRow = Math.Abs(j - cj) == ring || Math.Abs(k - ck) == ring;
                    int iStep = shellRow || ring == 0 ? 1 : 2 * ring;
                    for (int i = ci - ring; i <= ci + ring; i += iStep)
                    {
                        if (i < 0 || i >= _gx) continue;
                        foreach (int t in _g3[(k * _gy + j) * _gx + i])
                        {
                            var a = Vertices[Triangles[3 * t]];
                            var b = Vertices[Triangles[3 * t + 1]];
                            var c = Vertices[Triangles[3 * t + 2]];
                            var q = Vec3.ClosestOnTriangle(p, a, b, c);
                            double dd = (q - p).LengthSquared;
                            if (dd >= best) continue;
                            best = dd;
                            point = q;
                            normal = (b - a).Cross(c - a).Normalized();
                        }
                    }
                }
            }

            // Everything in later rings is at least ring·cell away.
            if (best < double.MaxValue && Math.Sqrt(best) <= ring * _c3) break;
        }

        distance = Math.Sqrt(best);
        return distance < maxDistance;
    }

    /// <summary>New solid transformed by p' = R·p + t.</summary>
    public TriMeshSolid Transformed(Func<Vec3, Vec3> map) => new(Vertices.Select(map).ToArray(), Triangles);
}

/// <summary>Common contract of rigid dies used by the 3D solver (height fields and imported CAD solids).</summary>
public interface IRigidDie
{
    Geometry.DieSide Side { get; }

    Vec3 BoundsMin { get; }

    Vec3 BoundsMax { get; }

    /// <summary>Signed clearance of p from the die moved by <paramref name="offset"/> along z: &gt; 0 outside the die, &lt; 0 penetrating.</summary>
    double Clearance(Vec3 p, double offset);

    /// <summary>Closest point on the (offset) die surface; normal points out of the die (into the workpiece).</summary>
    Vec3 Project(Vec3 p, double offset, out Vec3 normal);

    /// <summary>Working-surface height at (x, y): highest die point (bottom die) or lowest (top die); NaN where the die is absent.</summary>
    double SurfaceZ(double x, double y);
}

/// <summary>Rigid die given as a closed triangulated solid (imported from CAD).</summary>
public sealed class TriMeshDie : IRigidDie
{
    /// <summary>mm searched for closest points; beyond this the clearance is reported as ±Reach (the solver only needs it near contact).</summary>
    private const double Reach = 6;

    public TriMeshDie(Geometry.DieSide side, TriMeshSolid solid)
    {
        Side = side;
        Solid = solid;
    }

    public Geometry.DieSide Side { get; }

    public TriMeshSolid Solid { get; }

    public Vec3 BoundsMin => Solid.Min;

    public Vec3 BoundsMax => Solid.Max;

    public double Clearance(Vec3 p, double offset)
    {
        var q = new Vec3(p.X, p.Y, p.Z - offset);
        var lo = Solid.Min;
        var hi = Solid.Max;
        if (q.X < lo.X - Reach || q.X > hi.X + Reach || q.Y < lo.Y - Reach || q.Y > hi.Y + Reach || q.Z < lo.Z - Reach || q.Z > hi.Z + Reach) return Reach;
        bool inside = Solid.Contains(q);
        double d = Solid.Closest(q, Reach, out _, out _, out double dist) ? dist : Reach;
        return inside ? -d : d;
    }

    public Vec3 Project(Vec3 p, double offset, out Vec3 normal)
    {
        var q = new Vec3(p.X, p.Y, p.Z - offset);
        if (!Solid.Closest(q, 4 * Reach, out var c, out normal, out _)) normal = Side == Geometry.DieSide.Bottom ? Vec3.UnitZ : -Vec3.UnitZ;
        return new Vec3(c.X, c.Y, c.Z + offset);
    }

    [ThreadStatic]
    private static List<double>? _hits;

    public double SurfaceZ(double x, double y)
    {
        var hits = _hits ??= [];
        Solid.VerticalHits(x, y, hits);
        if (hits.Count == 0) return double.NaN;
        return Side == Geometry.DieSide.Bottom ? hits.Max() : hits.Min();
    }
}
