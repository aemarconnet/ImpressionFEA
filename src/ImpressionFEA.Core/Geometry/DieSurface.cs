namespace ImpressionFEA.Core.Geometry;

public enum DieSide
{
    Bottom,
    Top,
}

/// <summary>
/// Rigid die working surface described as a piecewise-linear height function z = h(x), x strictly increasing.
/// Real impression dies always carry a draft angle on their walls, so a height function is sufficient and
/// makes penetration detection unambiguous. The bottom die occupies z &lt; h(x); the top die occupies z &gt; h(x) + offset.
/// </summary>
public sealed class DieSurface
{
    private readonly Vec2[] _p;

    public DieSurface(DieSide side, IEnumerable<Vec2> points, double extendTo = 1000.0)
    {
        ArgumentNullException.ThrowIfNull(points);
        var list = points.ToList();
        if (list.Count < 2)
            throw new ArgumentException("A die profile needs at least two points.", nameof(points));
        for (int i = 1; i < list.Count; i++)
        {
            if (!(list[i].X > list[i - 1].X))
                throw new ArgumentException(
                    $"Die profile x-coordinates must be strictly increasing (point {i}: x={list[i].X:F3}). Use a draft angle on walls.",
                    nameof(points));
        }

        if (list[^1].X < extendTo)
            list.Add(new Vec2(extendTo, list[^1].Y));
        Side = side;
        _p = list.ToArray();
    }

    public DieSide Side { get; }

    private Vec2[]? _corners;

    /// <summary>
    /// Feature corners of the profile (fillet centres included): vertices where the turning angle summed over a ±2
    /// vertex window exceeds 30° and is a local maximum. Remeshing pins nodes to these points.
    /// </summary>
    public IReadOnlyList<Vec2> Corners => _corners ??= FindCorners();

    private Vec2[] FindCorners()
    {
        int n = _p.Length;
        var turn = new double[n];
        for (int i = 1; i < n - 1; i++)
        {
            var a = (_p[i] - _p[i - 1]).Normalized();
            var b = (_p[i + 1] - _p[i]).Normalized();
            turn[i] = Math.Abs(Math.Atan2(a.Cross(b), a.Dot(b)));
        }

        var win = new double[n];
        for (int i = 0; i < n; i++)
        {
            for (int k = Math.Max(0, i - 2); k <= Math.Min(n - 1, i + 2); k++) win[i] += turn[k];
        }

        var list = new List<Vec2>();
        for (int i = 1; i < n - 1; i++)
        {
            if (win[i] < 30 * Math.PI / 180 || turn[i] <= 0) continue;
            bool max = true;
            for (int k = Math.Max(1, i - 2); k <= Math.Min(n - 2, i + 2) && max; k++)
                max = k == i || win[k] < win[i] || (win[k] == win[i] && k > i);
            if (max) list.Add(_p[i]);
        }

        return list.ToArray();
    }

    public IReadOnlyList<Vec2> Points => _p;

    public double MinX => _p[0].X;

    public double MaxX => _p[^1].X;

    /// <summary>Die surface height at abscissa x (constant extension outside the defined range).</summary>
    public double HeightAt(double x)
    {
        if (x <= _p[0].X) return _p[0].Y;
        if (x >= _p[^1].X) return _p[^1].Y;
        int lo = 0, hi = _p.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (_p[mid].X <= x) lo = mid; else hi = mid;
        }

        double t = (x - _p[lo].X) / (_p[hi].X - _p[lo].X);
        return _p[lo].Y + t * (_p[hi].Y - _p[lo].Y);
    }

    /// <summary>Minimum / maximum surface height over [x0, x1].</summary>
    public (double Min, double Max) HeightRange(double x0, double x1)
    {
        double min = Math.Min(HeightAt(x0), HeightAt(x1));
        double max = Math.Max(HeightAt(x0), HeightAt(x1));
        foreach (var p in _p)
        {
            if (p.X <= x0 || p.X >= x1) continue;
            min = Math.Min(min, p.Y);
            max = Math.Max(max, p.Y);
        }

        return (min, max);
    }

    /// <summary>Vertical clearance of a point from the die: positive in the open cavity, negative when penetrating.</summary>
    public double Clearance(Vec2 p, double offset)
    {
        double h = HeightAt(p.X) + offset;
        return Side == DieSide.Bottom ? p.Y - h : h - p.Y;
    }

    /// <summary>Unit normal of segment k pointing out of the die, i.e. into the workpiece.</summary>
    public Vec2 SegmentNormal(int k)
    {
        var d = (_p[k + 1] - _p[k]).Normalized();
        return Side == DieSide.Bottom ? new Vec2(-d.Y, d.X) : new Vec2(d.Y, -d.X);
    }

    /// <summary>Closest point on the (vertically offset) die surface, the outward normal there and the distance.</summary>
    public Vec2 Project(Vec2 p, double offset, out Vec2 normal, out double distance)
    {
        var shift = new Vec2(0, offset);
        double best = double.MaxValue;
        int bestK = 0;
        double bestT = 0;
        Vec2 bestQ = default;
        for (int k = 0; k < _p.Length - 1; k++)
        {
            var a = _p[k] + shift;
            var ab = _p[k + 1] - _p[k];
            double t = Math.Clamp((p - a).Dot(ab) / ab.Dot(ab), 0.0, 1.0);
            var q = a + t * ab;
            double d = (p - q).Dot(p - q);
            if (d < best)
            {
                best = d;
                bestK = k;
                bestT = t;
                bestQ = q;
            }
        }

        normal = SegmentNormal(bestK);
        if (bestT < 1e-9 && bestK > 0)
            normal = (normal + SegmentNormal(bestK - 1)).Normalized();
        else if (bestT > 1 - 1e-9 && bestK < _p.Length - 2)
            normal = (normal + SegmentNormal(bestK + 1)).Normalized();
        distance = Math.Sqrt(best);
        return bestQ;
    }

    /// <summary>Rounds the interior corners of a polyline with circular fillets (keeps x monotonic).</summary>
    public static List<Vec2> Fillet(IReadOnlyList<Vec2> pts, double radius, int segments = 5)
    {
        ArgumentNullException.ThrowIfNull(pts);
        var outPts = new List<Vec2> { pts[0] };
        for (int i = 1; i < pts.Count - 1; i++)
        {
            var b = pts[i];
            var u = (pts[i - 1] - b).Normalized();
            var w = (pts[i + 1] - b).Normalized();
            double cos = Math.Clamp(u.Dot(w), -1, 1);
            double theta = Math.Acos(cos);
            if (radius <= 0 || theta > Math.PI * 0.995)
            {
                outPts.Add(b);
                continue;
            }

            double half = theta / 2;
            double td = radius / Math.Tan(half);
            double maxTd = 0.45 * Math.Min((pts[i - 1] - b).Length, (pts[i + 1] - b).Length);
            td = Math.Min(td, maxTd);
            double r = td * Math.Tan(half);
            var p1 = b + td * u;
            var p2 = b + td * w;
            var bis = (u + w).Normalized();
            var c = b + (r / Math.Sin(half)) * bis;
            double a1 = Math.Atan2(p1.Y - c.Y, p1.X - c.X);
            double a2 = Math.Atan2(p2.Y - c.Y, p2.X - c.X);
            double da = a2 - a1;
            while (da > Math.PI) da -= 2 * Math.PI;
            while (da < -Math.PI) da += 2 * Math.PI;
            outPts.Add(p1);
            for (int s = 1; s < segments; s++)
            {
                double a = a1 + da * s / segments;
                outPts.Add(new Vec2(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a)));
            }

            outPts.Add(p2);
        }

        outPts.Add(pts[^1]);
        return outPts;
    }
}
