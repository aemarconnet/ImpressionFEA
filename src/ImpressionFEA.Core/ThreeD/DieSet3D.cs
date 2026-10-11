using ImpressionFEA.Core.Geometry;

namespace ImpressionFEA.Core.ThreeD;

/// <summary>Which symmetry planes bound the 3D model (the full part is recovered by mirroring).</summary>
public enum Symmetry3D
{
    /// <summary>Quarter model: x ≥ 0 and y ≥ 0 (parts symmetric about both vertical planes).</summary>
    Quarter,

    /// <summary>Half model: y ≥ 0 (parts symmetric about the x–z plane, e.g. a connecting rod).</summary>
    Half,

    /// <summary>Full model, no symmetry planes (imported CAD geometry).</summary>
    None,
}

public enum BilletShape
{
    /// <summary>Round billet standing on end (axis = press axis z): radius R, height H.</summary>
    VerticalCylinder,

    /// <summary>Round bar lying along x: radius R, length L.</summary>
    HorizontalBar,

    /// <summary>Arbitrary closed solid imported from CAD (<see cref="DieSet3D.ImportedBillet"/>).</summary>
    Imported,
}

/// <summary>A 3D closed-die impression pair (closed position) with its model symmetry and billet type.</summary>
public sealed class DieSet3D
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required IRigidDie Bottom { get; init; }

    /// <summary>Top die in the closed position (touching the bottom die); during the stroke it sits higher by the die gap.</summary>
    public required IRigidDie TopClosed { get; init; }

    public required Symmetry3D Symmetry { get; init; }

    public required BilletShape Billet { get; init; }

    /// <summary>Workpiece solid as imported (only for <see cref="BilletShape.Imported"/>); it is dropped onto the bottom die before forging.</summary>
    public TriMeshSolid? ImportedBillet { get; init; }

    /// <summary>Plan-view signed distance to the impression outline (negative inside the impression, before the flash land).</summary>
    public required Func<double, double, double> Outline { get; init; }

    /// <summary>Plan-view extent of the full part used for viewing (mm): x ∈ [XMin, XMax], |y| ≤ YMax.</summary>
    public required double XMin { get; init; }

    public required double XMax { get; init; }

    public required double YMax { get; init; }

    /// <summary>Billet centre x (horizontal bar).</summary>
    public double BilletCenterX { get; init; }

    public required double RecommendedRadius { get; init; }

    /// <summary>Recommended bar length (horizontal bar) or ignored.</summary>
    public double RecommendedLength { get; init; } = 100;

    public double VolumeFactor { get; init; } = 1.1;

    public required double RecommendedElementSize { get; init; }

    public required double RecommendedStrokeIncrement { get; init; }

    public int SymmetryFactor => Symmetry switch { Symmetry3D.Quarter => 4, Symmetry3D.Half => 2, _ => 1 };

    public bool SymmetricX => Symmetry == Symmetry3D.Quarter;

    public bool SymmetricY => Symmetry != Symmetry3D.None;

    public bool IsImported => Billet == BilletShape.Imported;

    public override string ToString() => Name;

    private double? _cavity;

    /// <summary>Closed impression volume of the full part (mm³).</summary>
    public double CavityVolume()
    {
        if (_cavity is { } c) return c;
        double d = Math.Max(0.5, Math.Sqrt((XMax - XMin) * 2 * YMax / 90000));
        double v = 0;
        for (double x = XMin + d / 2; x < XMax; x += d)
        {
            for (double y = -YMax + d / 2; y < YMax; y += d)
            {
                if (Outline(x, y) >= 0) continue;
                double t = TopClosed.SurfaceZ(x, y) - Bottom.SurfaceZ(x, y);
                if (t > 0) v += t * d * d;
            }
        }

        _cavity = v;
        return v;
    }

    /// <summary>Full billet volume for the given dimensions (dimension = H for vertical cylinders, L for bars).</summary>
    public static double BilletVolume(double radius, double dimension) => Math.PI * radius * radius * dimension;

    /// <summary>Full billet volume of this die set (imported solid or analytic cylinder).</summary>
    public double BilletVolumeFor(double radius, double dimension) => ImportedBillet?.Volume ?? BilletVolume(radius, dimension);

    public double RecommendedDimension(double radius) => VolumeFactor * CavityVolume() / (Math.PI * radius * radius);

    /// <summary>
    /// Recommended billet: a vertical cylinder keeps the preset radius and gets its height from volume; a bar keeps
    /// the preset length (spanning the die) and gets its radius from volume.
    /// </summary>
    public (double Radius, double Dimension) RecommendedBillet()
    {
        if (ImportedBillet is { } b) return (0.5 * Math.Min(b.Max.X - b.Min.X, b.Max.Y - b.Min.Y), b.Max.Z - b.Min.Z);
        double v = VolumeFactor * CavityVolume();
        return Billet == BilletShape.VerticalCylinder
            ? (RecommendedRadius, v / (Math.PI * RecommendedRadius * RecommendedRadius))
            : (Math.Sqrt(v / (Math.PI * RecommendedLength)), RecommendedLength);
    }

    private (double, double)? _importPlace;

    /// <summary>
    /// Seats the billet on the bottom die and returns the vertical position (cylinder base z, bar axis z, or the
    /// vertical shift applied to an imported billet) and the initial top-die gap at which the top die just touches
    /// the billet.
    /// </summary>
    public (double Seat, double Gap) Place(double radius, double dimension)
    {
        if (ImportedBillet is { } solid) return _importPlace ??= PlaceImported(solid);
        double seat = double.MinValue, topReach = double.MinValue;
        const int n = 48;
        if (Billet == BilletShape.VerticalCylinder)
        {
            for (int a = 0; a < n; a++)
            {
                for (int k = 0; k <= n; k++)
                {
                    double r = radius * k / n, phi = 2 * Math.PI * a / n;
                    double x = r * Math.Cos(phi), y = r * Math.Sin(phi);
                    seat = Math.Max(seat, Bottom.SurfaceZ(x, y));
                }
            }

            for (int a = 0; a < n; a++)
            {
                for (int k = 0; k <= n; k++)
                {
                    double r = radius * k / n, phi = 2 * Math.PI * a / n;
                    double x = r * Math.Cos(phi), y = r * Math.Sin(phi);
                    topReach = Math.Max(topReach, seat + dimension - TopClosed.SurfaceZ(x, y));
                }
            }
        }
        else
        {
            double x0 = BilletCenterX - dimension / 2;
            for (int i = 0; i <= 4 * n; i++)
            {
                double x = x0 + dimension * i / (4 * n);
                for (int k = -n; k <= n; k++)
                {
                    double y = radius * k / n;
                    seat = Math.Max(seat, Bottom.SurfaceZ(x, y) + Math.Sqrt(Math.Max(radius * radius - y * y, 0)));
                }
            }

            for (int i = 0; i <= 4 * n; i++)
            {
                double x = x0 + dimension * i / (4 * n);
                for (int k = -n; k <= n; k++)
                {
                    double y = radius * k / n;
                    topReach = Math.Max(topReach, seat + Math.Sqrt(Math.Max(radius * radius - y * y, 0)) - TopClosed.SurfaceZ(x, y));
                }
            }
        }

        return (seat, topReach);
    }

    /// <summary>
    /// Imported billet: on a plan-view grid over its footprint compare the billet's lowest / highest surface point
    /// with the bottom die's working surface / the closed top die's, giving the drop onto the bottom die and the top
    /// die travel from first contact to closure.
    /// </summary>
    private (double Seat, double Gap) PlaceImported(TriMeshSolid b)
    {
        double ext = Math.Max(b.Max.X - b.Min.X, b.Max.Y - b.Min.Y);
        double d = Math.Max(ext / 160, 0.05);
        double drop = double.MaxValue, reach = double.MinValue;
        var hits = new List<double>();
        for (double x = b.Min.X + d / 2; x < b.Max.X; x += d)
        {
            for (double y = b.Min.Y + d / 2; y < b.Max.Y; y += d)
            {
                b.VerticalHits(x, y, hits);
                if (hits.Count < 2) continue;
                double lo = hits.Min(), hi = hits.Max();
                double zb = Bottom.SurfaceZ(x, y), zt = TopClosed.SurfaceZ(x, y);
                if (double.IsFinite(zb)) drop = Math.Min(drop, lo - zb);
                if (double.IsFinite(zt)) reach = Math.Max(reach, hi - zt);
            }
        }

        if (drop == double.MaxValue) throw new InvalidOperationException("The workpiece does not lie over the bottom die.");
        if (reach == double.MinValue) throw new InvalidOperationException("The workpiece does not lie under the top die.");
        double seat = -drop;
        return (seat, reach + seat);
    }

    /// <summary>Analytic inside test of the seated billet (full part, both signs of x and y).</summary>
    public Func<Vec3, bool> BilletInside(double radius, double dimension, double seat) => Billet switch
    {
        BilletShape.VerticalCylinder => p => p.X * p.X + p.Y * p.Y < radius * radius && p.Z > seat && p.Z < seat + dimension,
        BilletShape.HorizontalBar => p => p.Y * p.Y + (p.Z - seat) * (p.Z - seat) < radius * radius && Math.Abs(p.X - BilletCenterX) < dimension / 2,
        _ => p => ImportedBillet!.Contains(new Vec3(p.X, p.Y, p.Z - seat)),
    };

    /// <summary>Bounding box of the seated billet.</summary>
    public (Vec3 Min, Vec3 Max) BilletBounds(double radius, double dimension, double seat) => Billet switch
    {
        BilletShape.VerticalCylinder => (new Vec3(-radius, -radius, seat), new Vec3(radius, radius, seat + dimension)),
        BilletShape.HorizontalBar => (new Vec3(BilletCenterX - dimension / 2, -radius, seat - radius), new Vec3(BilletCenterX + dimension / 2, radius, seat + radius)),
        _ => (ImportedBillet!.Min + new Vec3(0, 0, seat), ImportedBillet!.Max + new Vec3(0, 0, seat)),
    };
}
