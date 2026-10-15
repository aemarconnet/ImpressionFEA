using ImpressionFEA.Core.Geometry;

namespace ImpressionFEA.Core.ThreeD;

/// <summary>
/// Rigid 3D impression die as a height field z = h(x, y) sampled on a regular grid (bilinear between samples,
/// triangulated for closest-point queries). Closed-die impressions have no undercuts — the part must release in the
/// press direction — so a height field is an exact description. The bottom die occupies z &lt; h; the top die
/// z &gt; h + offset.
/// </summary>
public sealed class HeightFieldDie : IRigidDie
{
    private readonly double[] _h;

    private HeightFieldDie(DieSide side, double x0, double y0, double dx, int nx, int ny, double[] h)
    {
        Side = side;
        X0 = x0;
        Y0 = y0;
        Dx = dx;
        Nx = nx;
        Ny = ny;
        _h = h;
        BoundsMin = new Vec3(x0, y0, h.Min());
        BoundsMax = new Vec3(x0 + nx * dx, y0 + ny * dx, h.Max());
    }

    public DieSide Side { get; }

    public Vec3 BoundsMin { get; }

    public Vec3 BoundsMax { get; }

    public double SurfaceZ(double x, double y) => HeightAt(x, y);

    public double X0 { get; }

    public double Y0 { get; }

    public double Dx { get; }

    /// <summary>Cells in x / y (samples are Nx+1 × Ny+1).</summary>
    public int Nx { get; }

    public int Ny { get; }

    public double X1 => X0 + Nx * Dx;

    public double Y1 => Y0 + Ny * Dx;

    public double Sample(int i, int j) => _h[Math.Clamp(j, 0, Ny) * (Nx + 1) + Math.Clamp(i, 0, Nx)];

    /// <summary>Samples a height function over [x0,x1]×[y0,y1] and optionally blurs it (Gaussian, σ = blur/2) to round edges into fillets.</summary>
    public static HeightFieldDie Create(DieSide side, Func<double, double, double> height, double x0, double x1, double y0, double y1, double dx, double blur)
    {
        ArgumentNullException.ThrowIfNull(height);
        int nx = (int)Math.Ceiling((x1 - x0) / dx), ny = (int)Math.Ceiling((y1 - y0) / dx);
        var h = new double[(nx + 1) * (ny + 1)];
        for (int j = 0; j <= ny; j++)
        {
            for (int i = 0; i <= nx; i++) h[j * (nx + 1) + i] = height(x0 + i * dx, y0 + j * dx);
        }

        if (blur > 0) h = Blur(h, nx + 1, ny + 1, blur / 2 / dx);
        return new HeightFieldDie(side, x0, y0, dx, nx, ny, h);
    }

    private static double[] Blur(double[] h, int w, int hgt, double sigma)
    {
        int r = (int)Math.Ceiling(2.5 * sigma);
        var k = new double[2 * r + 1];
        double sum = 0;
        for (int i = -r; i <= r; i++) sum += k[i + r] = Math.Exp(-0.5 * i * i / (sigma * sigma));
        for (int i = 0; i < k.Length; i++) k[i] /= sum;
        var tmp = new double[h.Length];
        var res = new double[h.Length];
        for (int j = 0; j < hgt; j++)
        {
            for (int i = 0; i < w; i++)
            {
                double s = 0;
                for (int q = -r; q <= r; q++) s += k[q + r] * h[j * w + Math.Clamp(i + q, 0, w - 1)];
                tmp[j * w + i] = s;
            }
        }

        for (int j = 0; j < hgt; j++)
        {
            for (int i = 0; i < w; i++)
            {
                double s = 0;
                for (int q = -r; q <= r; q++) s += k[q + r] * tmp[Math.Clamp(j + q, 0, hgt - 1) * w + i];
                res[j * w + i] = s;
            }
        }

        return res;
    }

    public double HeightAt(double x, double y)
    {
        double fx = Math.Clamp((x - X0) / Dx, 0, Nx), fy = Math.Clamp((y - Y0) / Dx, 0, Ny);
        int i = Math.Min((int)fx, Nx - 1), j = Math.Min((int)fy, Ny - 1);
        double u = fx - i, v = fy - j;
        int w = Nx + 1;
        return (1 - u) * (1 - v) * _h[j * w + i] + u * (1 - v) * _h[j * w + i + 1] + (1 - u) * v * _h[(j + 1) * w + i] + u * v * _h[(j + 1) * w + i + 1];
    }

    /// <summary>Grid points where the surface normal turns by more than <paramref name="minAngleDeg"/> to a neighbour (die edges).</summary>
    public IReadOnlyList<Vec3> FeaturePoints(double spacing, double minAngleDeg)
    {
        double cosMax = Math.Cos(minAngleDeg * Math.PI / 180);
        Vec3 N(int i, int j) => new Vec3(-(Sample(i + 1, j) - Sample(i - 1, j)) / (2 * Dx), -(Sample(i, j + 1) - Sample(i, j - 1)) / (2 * Dx), 1).Normalized();
        int step = Math.Max(1, (int)Math.Round(spacing / Dx));
        var pts = new List<Vec3>();
        for (int j = 0; j <= Ny; j += step)
        {
            for (int i = 0; i <= Nx; i += step)
            {
                var n = N(i, j);
                if (n.Dot(N(i + 1, j)) > cosMax && n.Dot(N(i, j + 1)) > cosMax && n.Dot(N(i - 1, j)) > cosMax && n.Dot(N(i, j - 1)) > cosMax) continue;
                pts.Add(new Vec3(X0 + i * Dx, Y0 + j * Dx, Sample(i, j)));
            }
        }

        return pts;
    }

    /// <summary>Vertical clearance: positive in the open cavity, negative when penetrating.</summary>
    public double Clearance(Vec3 p, double offset)
    {
        double h = HeightAt(p.X, p.Y) + offset;
        return Side == DieSide.Bottom ? p.Z - h : h - p.Z;
    }

    public (double Min, double Max) Range(double x0, double x1, double y0, double y1)
    {
        double min = double.MaxValue, max = double.MinValue;
        int i0 = Math.Clamp((int)Math.Floor((x0 - X0) / Dx), 0, Nx), i1 = Math.Clamp((int)Math.Ceiling((x1 - X0) / Dx), 0, Nx);
        int j0 = Math.Clamp((int)Math.Floor((y0 - Y0) / Dx), 0, Ny), j1 = Math.Clamp((int)Math.Ceiling((y1 - Y0) / Dx), 0, Ny);
        for (int j = j0; j <= j1; j++)
        {
            for (int i = i0; i <= i1; i++)
            {
                double v = _h[j * (Nx + 1) + i];
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
        }

        return (min, max);
    }

    private Vec3 Vertex(int i, int j, double offset) => new(X0 + i * Dx, Y0 + j * Dx, _h[j * (Nx + 1) + i] + offset);

    /// <summary>Closest point on the (offset) die surface and its normal pointing out of the die (into the workpiece).</summary>
    public Vec3 Project(Vec3 p, double offset, out Vec3 normal)
    {
        double reach = Math.Abs(Clearance(p, offset)) + 1.5 * Dx;
        int i0 = Math.Clamp((int)Math.Floor((p.X - reach - X0) / Dx), 0, Nx - 1), i1 = Math.Clamp((int)Math.Floor((p.X + reach - X0) / Dx), 0, Nx - 1);
        int j0 = Math.Clamp((int)Math.Floor((p.Y - reach - Y0) / Dx), 0, Ny - 1), j1 = Math.Clamp((int)Math.Floor((p.Y + reach - Y0) / Dx), 0, Ny - 1);
        double best = double.MaxValue;
        Vec3 bestQ = p;
        Vec3 bestN = Side == DieSide.Bottom ? Vec3.UnitZ : -Vec3.UnitZ;
        for (int j = j0; j <= j1; j++)
        {
            for (int i = i0; i <= i1; i++)
            {
                var a = Vertex(i, j, offset);
                var b = Vertex(i + 1, j, offset);
                var c = Vertex(i + 1, j + 1, offset);
                var d = Vertex(i, j + 1, offset);
                Test(a, b, c);
                Test(a, c, d);
            }
        }

        normal = bestN;
        return bestQ;

        void Test(Vec3 a, Vec3 b, Vec3 c)
        {
            var q = Vec3.ClosestOnTriangle(p, a, b, c);
            double dd = (q - p).LengthSquared;
            if (dd >= best) return;
            best = dd;
            bestQ = q;
            var n = (b - a).Cross(c - a).Normalized(); // +z for counter-clockwise (x, y) ordering
            bestN = Side == DieSide.Bottom ? n : -n;
        }
    }
}
