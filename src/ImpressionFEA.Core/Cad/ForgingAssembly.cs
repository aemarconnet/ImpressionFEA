using ImpressionFEA.Core.Geometry;
using ImpressionFEA.Core.ThreeD;

namespace ImpressionFEA.Core.Cad;

/// <summary>
/// A die / workpiece / die stack read from CAD. The three centres of gravity lie on the press axis: the body whose
/// COG is between the other two is the workpiece, the outer two are the rigid dies. Which die moves (the top die) is
/// chosen by the user; the model is then rotated so the press axis points from the bottom die to the top die (+z)
/// and centred on the workpiece.
/// </summary>
public sealed class ForgingAssembly
{
    /// <summary>Press axes within this angle of a coordinate axis are snapped to it (CAD models are usually aligned).</summary>
    private const double SnapDegrees = 3;

    private ForgingAssembly(string name, IReadOnlyList<CadBody> bodies, int workpiece, int dieA, int dieB, double offAxis)
    {
        Name = name;
        Bodies = bodies;
        Workpiece = bodies[workpiece];
        DieA = bodies[dieA];
        DieB = bodies[dieB];
        OffAxisPercent = offAxis;
    }

    public string Name { get; }

    public IReadOnlyList<CadBody> Bodies { get; }

    public CadBody Workpiece { get; }

    /// <summary>The two outer bodies (rigid dies), in file order.</summary>
    public CadBody DieA { get; }

    public CadBody DieB { get; }

    /// <summary>Distance of the workpiece COG from the line through the die COGs, % of the die COG spacing.</summary>
    public double OffAxisPercent { get; }

    /// <summary>
    /// Default top die: the die that is higher along the file coordinate axis the dies are mostly separated along
    /// (+Z for Z-up CAD, +Y for Y-up CAD such as SolidWorks).
    /// </summary>
    public CadBody SuggestedTop
    {
        get
        {
            var d = DieB.CenterOfGravity - DieA.CenterOfGravity;
            double c = Math.Abs(d.Z) >= Math.Max(Math.Abs(d.X), Math.Abs(d.Y)) ? d.Z : Math.Abs(d.Y) >= Math.Abs(d.X) ? d.Y : d.X;
            return c > 0 ? DieB : DieA;
        }
    }

    /// <summary>File axis the dies are separated along, for the dialog hint ("+Z", "+Y", "+X").</summary>
    public string SuggestedAxisName
    {
        get
        {
            var d = DieB.CenterOfGravity - DieA.CenterOfGravity;
            return Math.Abs(d.Z) >= Math.Max(Math.Abs(d.X), Math.Abs(d.Y)) ? "+Z" : Math.Abs(d.Y) >= Math.Abs(d.X) ? "+Y" : "+X";
        }
    }

    /// <summary>Classifies exactly three solids by their centres of gravity.</summary>
    public static ForgingAssembly Classify(string name, IReadOnlyList<CadBody> bodies)
    {
        ArgumentNullException.ThrowIfNull(bodies);
        if (bodies.Count != 3)
            throw new InvalidDataException($"The STEP file contains {bodies.Count} solid bodies; exactly 3 are needed (bottom die, workpiece, top die).");
        int mid = 0;
        double best = double.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            var c = bodies[i].CenterOfGravity;
            double worst = Math.Max((bodies[(i + 1) % 3].CenterOfGravity - c).Length, (bodies[(i + 2) % 3].CenterOfGravity - c).Length);
            if (worst < best) { best = worst; mid = i; }
        }

        int a = (mid + 1) % 3, b = (mid + 2) % 3;
        if (a > b) (a, b) = (b, a);
        var ca = bodies[a].CenterOfGravity;
        var axis = bodies[b].CenterOfGravity - ca;
        double len = axis.Length;
        if (len < 1e-6) throw new InvalidDataException("The two outer bodies share the same centre of gravity; the press axis cannot be determined.");
        axis = (1 / len) * axis;
        var rel = bodies[mid].CenterOfGravity - ca;
        double along = rel.Dot(axis);
        if (along <= 0 || along >= len)
            throw new InvalidDataException("No body lies between the other two along the line through their centres of gravity; cannot tell the workpiece from the dies.");
        double off = (rel - along * axis).Length;
        return new ForgingAssembly(name, bodies, mid, a, b, 100 * off / len);
    }

    /// <summary>Unit press axis (bottom → top) in file coordinates, snapped to a coordinate axis when close.</summary>
    public Vec3 PressAxis(CadBody top, out bool snapped)
    {
        var bottom = top == DieA ? DieB : DieA;
        var d = (top.CenterOfGravity - bottom.CenterOfGravity).Normalized();
        snapped = false;
        foreach (var e in new[] { Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ })
        {
            double c = d.Dot(e);
            if (Math.Abs(c) >= Math.Cos(SnapDegrees * Math.PI / 180))
            {
                snapped = true;
                return Math.Sign(c) * e;
            }
        }

        return d;
    }

    /// <summary>
    /// Builds the 3D die set: rotates the press axis onto +z, centres the workpiece COG on the z axis, and moves the
    /// top die down to the closed position (where it first touches the bottom die).
    /// </summary>
    public DieSet3D BuildDieSet(CadBody top, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(top);
        if (top != DieA && top != DieB) throw new ArgumentException("The top die must be one of the two outer bodies.", nameof(top));
        log ??= _ => { };
        var bottom = top == DieA ? DieB : DieA;
        var axis = PressAxis(top, out bool snapped);
        var rot = RotationOnto(axis);
        var c = rot(Workpiece.CenterOfGravity);
        Vec3 Map(Vec3 p)
        {
            var q = rot(p);
            return new Vec3(q.X - c.X, q.Y - c.Y, q.Z);
        }

        var work = Workpiece.Mesh.Transformed(Map);
        var bot = new TriMeshDie(DieSide.Bottom, bottom.Mesh.Transformed(Map));
        var topOpen = top.Mesh.Transformed(Map);
        double closure = ClosureDistance(bot, new TriMeshDie(DieSide.Top, topOpen));
        if (!double.IsFinite(closure))
            throw new InvalidDataException("The top die does not overlap the bottom die in plan view along the press axis; they can never touch.");
        if (closure < -1e-6)
            throw new InvalidDataException($"The dies interpenetrate by {-closure:F3} mm in the imported position.");
        var topClosed = new TriMeshDie(DieSide.Top, topOpen.Transformed(p => new Vec3(p.X, p.Y, p.Z - closure)));

        double xMin = Math.Min(bot.BoundsMin.X, topClosed.BoundsMin.X), xMax = Math.Max(bot.BoundsMax.X, topClosed.BoundsMax.X);
        double yMax = Math.Max(Math.Max(-bot.BoundsMin.Y, bot.BoundsMax.Y), Math.Max(-topClosed.BoundsMin.Y, topClosed.BoundsMax.Y));
        var outline = ImpressionOutline(bot, topClosed, xMin, xMax, yMax);
        double thin = Math.Min(work.Max.X - work.Min.X, Math.Min(work.Max.Y - work.Min.Y, work.Max.Z - work.Min.Z));
        double hNodes = Math.Cbrt(2.2 * work.Volume / (0.75 * SimulationConfig3D.MaxNodes));
        double h = Math.Round(Math.Max(Math.Min(thin / 6, 4), hNodes) * 4) / 4;
        h = Math.Max(h, 0.25);

        var axisText = snapped ? AxisName(axis) : $"({axis.X:F3}, {axis.Y:F3}, {axis.Z:F3})";
        log($"STEP import  : {Name}: workpiece = body {Workpiece.Index + 1} ({Workpiece.Volume / 1000:F2} cm³), top die = body {top.Index + 1}, bottom die = body {bottom.Index + 1}");
        log($"Press axis   : file {axisText}{(snapped ? " (snapped)" : "")}; workpiece COG {OffAxisPercent:F1} % off the die COG line; top die closes after {closure:F3} mm travel");

        return new DieSet3D
        {
            Name = $"Imported: {Name}",
            Description = $"STEP import. Workpiece body {Workpiece.Index + 1} ({Workpiece.Volume / 1000:F1} cm³) between rigid dies; top die body {top.Index + 1} moves along {axisText} until it touches the bottom die (body {bottom.Index + 1}). Full model, no symmetry.",
            Bottom = bot,
            TopClosed = topClosed,
            Symmetry = Symmetry3D.None,
            Billet = BilletShape.Imported,
            ImportedBillet = work,
            Outline = outline,
            XMin = xMin,
            XMax = xMax,
            YMax = yMax,
            RecommendedRadius = 0.5 * Math.Min(work.Max.X - work.Min.X, work.Max.Y - work.Min.Y),
            VolumeFactor = 1.0,
            RecommendedElementSize = h,
            RecommendedStrokeIncrement = Math.Round(0.2 * h, 2),
        };
    }

    private static string AxisName(Vec3 a) => (a.X != 0 ? (a.X > 0 ? "+X" : "−X") : a.Y != 0 ? (a.Y > 0 ? "+Y" : "−Y") : a.Z > 0 ? "+Z" : "−Z");

    /// <summary>Proper rotation taking unit vector <paramref name="a"/> onto +z (Rodrigues).</summary>
    private static Func<Vec3, Vec3> RotationOnto(Vec3 a)
    {
        double c = a.Z;
        if (c > 1 - 1e-12) return p => p;
        if (c < -1 + 1e-12) return p => new Vec3(p.X, -p.Y, -p.Z); // 180° about x
        var v = a.Cross(Vec3.UnitZ);
        double k = 1 / (1 + c);
        return p => p + v.Cross(p) + k * v.Cross(v.Cross(p));
    }

    /// <summary>
    /// Vertical travel of the top die until it touches the bottom die: the minimum vertical gap between the two
    /// surfaces, evaluated at the vertices of each die (exact for faceted surfaces except at crossing edges) and on a
    /// plan-view grid (catches the edge crossings). +∞ when the dies do not overlap in plan view.
    /// </summary>
    private static double ClosureDistance(TriMeshDie bottom, TriMeshDie top)
    {
        double d = double.PositiveInfinity;
        foreach (var v in top.Solid.Vertices)
        {
            double zb = bottom.SurfaceZ(v.X, v.Y);
            if (double.IsFinite(zb)) d = Math.Min(d, v.Z - zb);
        }

        foreach (var v in bottom.Solid.Vertices)
        {
            double zt = top.SurfaceZ(v.X, v.Y);
            if (double.IsFinite(zt)) d = Math.Min(d, zt - v.Z);
        }

        double x0 = Math.Max(bottom.BoundsMin.X, top.BoundsMin.X), x1 = Math.Min(bottom.BoundsMax.X, top.BoundsMax.X);
        double y0 = Math.Max(bottom.BoundsMin.Y, top.BoundsMin.Y), y1 = Math.Min(bottom.BoundsMax.Y, top.BoundsMax.Y);
        if (x1 <= x0 || y1 <= y0) return d;
        double step = Math.Max(x1 - x0, y1 - y0) / 400;
        for (double x = x0 + step / 2; x < x1; x += step)
        {
            for (double y = y0 + step / 2; y < y1; y += step)
            {
                double zb = bottom.SurfaceZ(x, y), zt = top.SurfaceZ(x, y);
                if (double.IsFinite(zb) && double.IsFinite(zt)) d = Math.Min(d, zt - zb);
            }
        }

        return d;
    }

    /// <summary>
    /// Plan-view impression for the fill measure: where the closed dies leave more than 15 % of the deepest closed gap
    /// (thin flash lands are excluded). Rasterised once; returns −1 inside, +1 outside.
    /// </summary>
    private static Func<double, double, double> ImpressionOutline(TriMeshDie bottom, TriMeshDie top, double xMin, double xMax, double yMax)
    {
        const int n = 300;
        double dx = (xMax - xMin) / n, dy = 2 * yMax / n;
        var gap = new double[n * n];
        double gMax = 0;
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                double x = xMin + (i + 0.5) * dx, y = -yMax + (j + 0.5) * dy;
                double g = top.SurfaceZ(x, y) - bottom.SurfaceZ(x, y);
                gap[j * n + i] = double.IsFinite(g) ? g : 0;
                gMax = Math.Max(gMax, gap[j * n + i]);
            }
        }

        double thr = 0.15 * gMax;
        var inside = gap.Select(g => g > thr).ToArray();
        return (x, y) =>
        {
            int i = (int)Math.Floor((x - xMin) / dx), j = (int)Math.Floor((y + yMax) / dy);
            return i >= 0 && i < n && j >= 0 && j < n && inside[j * n + i] ? -1 : 1;
        };
    }
}
