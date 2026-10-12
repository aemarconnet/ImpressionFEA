using ImpressionFEA.Core.Geometry;

namespace ImpressionFEA.Core.Simulation;

/// <summary>Direction of the bar-stock fibre (grain) in the starting billet of a 2D section.</summary>
public enum FiberDirection
{
    /// <summary>From the billet: along a horizontal bar's axis, otherwise along the press axis.</summary>
    Auto,

    /// <summary>Along the press axis (a bar standing upright: axisymmetric upsetting).</summary>
    Vertical,

    /// <summary>Across the press axis (radial in axisymmetry, a bar lying in the section plane in plane strain; along model x in 3D).</summary>
    Horizontal,
}

/// <summary>
/// Grain flow lines of a 2D forging (post-processing only): material lines seeded along the fibre direction of the
/// billet are carried through every increment with the mesh motion, like the flow lines of an etched macro-section.
/// A point keeps its barycentric position in its (split-quad) triangle from one increment to the next; across a
/// remesh, where nodes are not shared, the start of the increment is recovered as x − Δt·v on the new mesh. Lines
/// are refined where they stretch. Results are computed incrementally, so a live run only traces its new steps.
/// </summary>
public sealed class GrainFlowTracer
{
    private const int MaxPointsPerLine = 6000;

    private readonly List<StepSnapshot> _snaps = [];
    private readonly List<Vec2[][]> _lines = [];
    private double _maxSegment;

    public GrainFlowTracer(FiberDirection direction, int lineCount)
    {
        Direction = direction;
        LineCount = Math.Clamp(lineCount, 2, 200);
    }

    public FiberDirection Direction { get; }

    public int LineCount { get; }

    /// <summary>
    /// Flow lines at increment <paramref name="index"/> of <paramref name="snapshots"/> (index 0 = the billet),
    /// tracing any increments not done yet. Returns null when the run is not a 2D result. A different run (other
    /// snapshot objects) restarts the trace.
    /// </summary>
    public IReadOnlyList<Vec2[]>? LinesAt(IReadOnlyList<ForgingSnapshot> snapshots, int index)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (index < 0 || index >= snapshots.Count) return null;
        if (_snaps.Count > 0 && !ReferenceEquals(_snaps[0], snapshots[0])) Reset();
        for (int k = 0; k < Math.Min(_snaps.Count, index + 1); k++)
        {
            if (ReferenceEquals(_snaps[k], snapshots[k])) continue;
            _snaps.RemoveRange(k, _snaps.Count - k);
            _lines.RemoveRange(k, _lines.Count - k);
            break;
        }

        while (_snaps.Count <= index)
        {
            if (snapshots[_snaps.Count] is not StepSnapshot s) return null;
            if (_snaps.Count == 0) Seed(s);
            else Advance(s);
        }

        return _lines[index];
    }

    private void Reset()
    {
        _snaps.Clear();
        _lines.Clear();
    }

    // ------------------------------------------------------------------ seeding

    private void Seed(StepSnapshot s)
    {
        var loop = s.Mesh.BoundaryLoop;
        double xMin = double.MaxValue, xMax = double.MinValue, yMin = double.MaxValue, yMax = double.MinValue;
        foreach (int n in loop)
        {
            xMin = Math.Min(xMin, s.X[n]);
            xMax = Math.Max(xMax, s.X[n]);
            yMin = Math.Min(yMin, s.Y[n]);
            yMax = Math.Max(yMax, s.Y[n]);
        }

        bool vertical = Direction != FiberDirection.Horizontal;
        double across = vertical ? xMax - xMin : yMax - yMin;
        double along = vertical ? yMax - yMin : xMax - xMin;
        double spacing = across / LineCount;
        double ds = Math.Max(Math.Min(across, along) / 120, 1e-6);
        _maxSegment = 2 * ds;
        int count = Math.Max(2, (int)Math.Ceiling(along / ds) + 1);

        var loc = new Locator(s, s.X, s.Y);
        var lines = new List<Vec2[]>();
        for (int i = 0; i < LineCount; i++)
        {
            double c = (vertical ? xMin : yMin) + (i + 0.5) * spacing;
            var run = new List<Vec2>();
            for (int j = 0; j < count; j++)
            {
                double a = (vertical ? yMin : xMin) + along * j / (count - 1);
                var p = vertical ? new Vec2(c, a) : new Vec2(a, c);
                if (loc.Inside(p)) run.Add(p);
                else Flush(run, lines);
            }

            Flush(run, lines);
        }

        _snaps.Add(s);
        _lines.Add([.. lines]);
    }

    private static void Flush(List<Vec2> run, List<Vec2[]> lines)
    {
        if (run.Count >= 2) lines.Add([.. run]);
        run.Clear();
    }

    // ------------------------------------------------------------------ one increment

    private void Advance(StepSnapshot cur)
    {
        var prev = _snaps[^1];
        float[] sx, sy;
        if (ReferenceEquals(cur.Mesh, prev.Mesh) && prev.X.Length == cur.X.Length)
        {
            // Same topology: the previous configuration is exactly the start of this increment.
            sx = prev.X;
            sy = prev.Y;
        }
        else
        {
            double dt = cur.Time - prev.Time;
            sx = new float[cur.NodeCount];
            sy = new float[cur.NodeCount];
            bool v = cur.HasVelocity && cur.VelocityX.Length == cur.NodeCount;
            for (int n = 0; n < cur.NodeCount; n++)
            {
                sx[n] = v ? (float)(cur.X[n] - dt * cur.VelocityX[n]) : cur.X[n];
                sy[n] = v ? (float)(cur.Y[n] - dt * cur.VelocityY[n]) : cur.Y[n];
            }
        }

        var loc = new Locator(cur, sx, sy);
        var lines = _lines[^1];
        var moved = new Vec2[lines.Length][];
        for (int l = 0; l < lines.Length; l++)
        {
            var src = lines[l];
            var dst = new List<Vec2>(src.Length + 8);
            Vec2 last = default;
            for (int i = 0; i < src.Length; i++)
            {
                var q = loc.Map(src[i], cur);
                if (i > 0 && dst.Count < MaxPointsPerLine)
                {
                    // Refine a stretched segment: insert chord points (mapped from the start configuration).
                    double len = (q - last).Length;
                    int extra = Math.Min((int)(len / _maxSegment), 16);
                    for (int k = 1; k <= extra; k++) dst.Add(loc.Map(Vec2.Lerp(src[i - 1], src[i], k / (extra + 1.0)), cur));
                }

                dst.Add(q);
                last = q;
            }

            moved[l] = [.. dst];
        }

        _snaps.Add(cur);
        _lines.Add(moved);
    }

    // ------------------------------------------------------------------ point location (triangles of the split quads)

    /// <summary>Bucket grid over the triangles (a, b, c), (a, c, d) of each quad in a given node configuration.</summary>
    private sealed class Locator
    {
        private readonly int[] _tri;
        private readonly float[] _x;
        private readonly float[] _y;
        private readonly double _x0;
        private readonly double _y0;
        private readonly double _cell;
        private readonly int _nx;
        private readonly int _ny;
        private readonly List<int>[] _buckets;

        public Locator(StepSnapshot s, float[] x, float[] y)
        {
            _x = x;
            _y = y;
            var conn = s.Mesh.Connectivity;
            int ne = s.Mesh.ElementCount;
            _tri = new int[6 * ne];
            for (int e = 0; e < ne; e++)
            {
                int a = conn[4 * e], b = conn[4 * e + 1], c = conn[4 * e + 2], d = conn[4 * e + 3];
                _tri[6 * e] = a; _tri[6 * e + 1] = b; _tri[6 * e + 2] = c;
                _tri[6 * e + 3] = a; _tri[6 * e + 4] = c; _tri[6 * e + 5] = d;
            }

            double xMin = double.MaxValue, xMax = double.MinValue, yMin = double.MaxValue, yMax = double.MinValue;
            for (int n = 0; n < x.Length; n++)
            {
                xMin = Math.Min(xMin, x[n]); xMax = Math.Max(xMax, x[n]);
                yMin = Math.Min(yMin, y[n]); yMax = Math.Max(yMax, y[n]);
            }

            double area = Math.Max((xMax - xMin) * (yMax - yMin), 1e-12);
            _cell = Math.Max(Math.Sqrt(area / Math.Max(ne, 1)) * 1.5, 1e-6);
            _x0 = xMin - _cell;
            _y0 = yMin - _cell;
            _nx = Math.Clamp((int)((xMax - _x0) / _cell) + 2, 1, 2048);
            _ny = Math.Clamp((int)((yMax - _y0) / _cell) + 2, 1, 2048);
            _cell = Math.Max((xMax - _x0) / (_nx - 1), (yMax - _y0) / (_ny - 1));
            _buckets = new List<int>[_nx * _ny];
            for (int t = 0; t < _tri.Length / 3; t++)
            {
                double tx0 = double.MaxValue, tx1 = double.MinValue, ty0 = double.MaxValue, ty1 = double.MinValue;
                for (int k = 0; k < 3; k++)
                {
                    int n = _tri[3 * t + k];
                    tx0 = Math.Min(tx0, x[n]); tx1 = Math.Max(tx1, x[n]);
                    ty0 = Math.Min(ty0, y[n]); ty1 = Math.Max(ty1, y[n]);
                }

                for (int j = Cy(ty0); j <= Cy(ty1); j++)
                {
                    for (int i = Cx(tx0); i <= Cx(tx1); i++) (_buckets[j * _nx + i] ??= []).Add(t);
                }
            }
        }

        private int Cx(double v) => Math.Clamp((int)((v - _x0) / _cell), 0, _nx - 1);

        private int Cy(double v) => Math.Clamp((int)((v - _y0) / _cell), 0, _ny - 1);

        public bool Inside(Vec2 p) => Find(p, out _, out _, out _, exact: true) >= 0;

        /// <summary>Barycentric transfer of p from this configuration to the nodes of <paramref name="target"/>.</summary>
        public Vec2 Map(Vec2 p, StepSnapshot target)
        {
            int t = Find(p, out double w0, out double w1, out double w2, exact: false);
            if (t < 0) return p;
            int a = _tri[3 * t], b = _tri[3 * t + 1], c = _tri[3 * t + 2];
            return new Vec2(w0 * target.X[a] + w1 * target.X[b] + w2 * target.X[c], w0 * target.Y[a] + w1 * target.Y[b] + w2 * target.Y[c]);
        }

        /// <summary>
        /// Triangle containing p with its barycentric weights; when none does (and not <paramref name="exact"/>), the
        /// nearest triangle in the surrounding cells with clamped weights, so a point never leaves the part.
        /// </summary>
        private int Find(Vec2 p, out double w0, out double w1, out double w2, bool exact)
        {
            w0 = w1 = w2 = 0;
            int ci = Cx(p.X), cj = Cy(p.Y);
            int best = -1;
            double bestD = double.MaxValue;
            for (int ring = 0; ring <= (exact ? 0 : 3); ring++)
            {
                for (int j = cj - ring; j <= cj + ring; j++)
                {
                    for (int i = ci - ring; i <= ci + ring; i++)
                    {
                        if (i < 0 || j < 0 || i >= _nx || j >= _ny) continue;
                        if (Math.Max(Math.Abs(i - ci), Math.Abs(j - cj)) != ring) continue;
                        var list = _buckets[j * _nx + i];
                        if (list is null) continue;
                        foreach (int t in list)
                        {
                            Weights(t, p, out double a, out double b, out double c);
                            if (a >= -1e-9 && b >= -1e-9 && c >= -1e-9)
                            {
                                (w0, w1, w2) = (a, b, c);
                                return t;
                            }

                            if (exact) continue;
                            double d = Distance(t, p);
                            if (d < bestD)
                            {
                                bestD = d;
                                best = t;
                            }
                        }
                    }
                }

                if (best >= 0 && ring >= 1) break; // nearest found in the first ring around the cell
            }

            if (best < 0) return -1;
            ClampedWeights(best, p, out w0, out w1, out w2);
            return best;
        }

        private Vec2 P(int t, int k) => new(_x[_tri[3 * t + k]], _y[_tri[3 * t + k]]);

        private void Weights(int t, Vec2 p, out double a, out double b, out double c)
        {
            var pa = P(t, 0);
            var pb = P(t, 1);
            var pc = P(t, 2);
            double det = (pb - pa).Cross(pc - pa);
            if (Math.Abs(det) < 1e-18)
            {
                a = b = c = -1;
                return;
            }

            b = (p - pa).Cross(pc - pa) / det;
            c = (pb - pa).Cross(p - pa) / det;
            a = 1 - b - c;
        }

        private double Distance(int t, Vec2 p)
        {
            ClampedWeights(t, p, out double a, out double b, out double c);
            var q = a * P(t, 0) + b * P(t, 1) + c * P(t, 2);
            return (q - p).Length;
        }

        /// <summary>Weights of the closest point of triangle t to p.</summary>
        private void ClampedWeights(int t, Vec2 p, out double a, out double b, out double c)
        {
            Weights(t, p, out a, out b, out c);
            if (a >= 0 && b >= 0 && c >= 0) return;
            var v = new[] { P(t, 0), P(t, 1), P(t, 2) };
            double bestD = double.MaxValue;
            (a, b, c) = (1, 0, 0);
            for (int k = 0; k < 3; k++)
            {
                var e0 = v[k];
                var e1 = v[(k + 1) % 3];
                var ab = e1 - e0;
                double s = Math.Clamp((p - e0).Dot(ab) / Math.Max(ab.Dot(ab), 1e-30), 0, 1);
                double d = (e0 + s * ab - p).Length;
                if (d >= bestD) continue;
                bestD = d;
                var w = new double[3];
                w[k] = 1 - s;
                w[(k + 1) % 3] = s;
                (a, b, c) = (w[0], w[1], w[2]);
            }
        }
    }
}
