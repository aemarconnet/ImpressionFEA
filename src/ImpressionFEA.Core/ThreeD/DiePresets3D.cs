using ImpressionFEA.Core.Geometry;

namespace ImpressionFEA.Core.ThreeD;

/// <summary>Built-in 3D closed-die impressions (closed position, mm). Height fields are sampled at 0.5 mm.</summary>
public static class DiePresets3D
{
    private static readonly double CotDraft = 1 / Math.Tan(7.0 * Math.PI / 180.0);

    private static readonly Lazy<IReadOnlyList<DieSet3D>> Lazy = new(() => [ConnectingRod(), GearBlank(), SimpleUpsetBlock()]);

    public static IReadOnlyList<DieSet3D> All => Lazy.Value;

    /// <summary>Flat dies, 20 mm shut height — 3D counterpart of the 2D upset benchmark.</summary>
    public static DieSet3D SimpleUpsetBlock() => new()
    {
        Name = "Simple upset block (3D)",
        Description = "Flat dies, quarter model. Benchmark against the analytic load and the 2D axisymmetric run.",
        Bottom = HeightFieldDie.Create(DieSide.Bottom, (_, _) => 0, -60, 60, -60, 60, 1.0, 0),
        TopClosed = HeightFieldDie.Create(DieSide.Top, (_, _) => 20, -60, 60, -60, 60, 1.0, 0),
        Symmetry = Symmetry3D.Quarter,
        Billet = BilletShape.VerticalCylinder,
        Outline = (x, y) => Math.Sqrt(x * x + y * y) - 32,
        XMin = -46,
        XMax = 46,
        YMax = 46,
        RecommendedRadius = 20,
        VolumeFactor = 1.0,
        RecommendedElementSize = 3.0,
        RecommendedStrokeIncrement = 0.5,
    };

    /// <summary>The 2D gear-blank impression revolved into a 3D height field (quarter model) — cross-checks 3D against 2D.</summary>
    public static DieSet3D GearBlank()
    {
        var g = DiePresets.GearBlank();
        return new DieSet3D
        {
            Name = "Gear blank (3D)",
            Description = "2D gear-blank impression revolved into 3D dies, quarter model. Cross-check against the 2D axisymmetric solver.",
            Bottom = HeightFieldDie.Create(DieSide.Bottom, (x, y) => g.Bottom.HeightAt(Math.Sqrt(x * x + y * y)), -80, 80, -80, 80, 0.5, 0),
            TopClosed = HeightFieldDie.Create(DieSide.Top, (x, y) => g.TopClosed.HeightAt(Math.Sqrt(x * x + y * y)), -80, 80, -80, 80, 0.5, 0),
            Symmetry = Symmetry3D.Quarter,
            Billet = BilletShape.VerticalCylinder,
            Outline = (x, y) => Math.Sqrt(x * x + y * y) - g.CavityOuterRadius,
            XMin = -76,
            XMax = 76,
            YMax = 76,
            RecommendedRadius = 28,
            VolumeFactor = 1.10,
            RecommendedElementSize = 3.0,
            RecommendedStrokeIncrement = 0.6,
        };
    }

    /// <summary>
    /// Connecting rod: big-end boss Ø52 × 26 with Ø28 bore web, small-end boss Ø30 × 20 with Ø16 bore web, 110 mm
    /// centres, tapered I-beam shank (web 6, ribs 16), 1.8 mm flash land and gutter, 7° draft, ~1.5 mm fillets.
    /// Half model about the rod axis (y ≥ 0), forged from a straight round bar.
    /// </summary>
    public static DieSet3D ConnectingRod()
    {
        const double lc = 110, rb = 26, rbBore = 14, rs = 15, rsBore = 8;
        const double dBig = 13, dSmall = 10, dWeb = 2, dShankWeb = 3, dRib = 8, ribW = 4.5;
        const double land = 0.9, landW = 8, gutter = 5;
        static double ShankHalfWidth(double x) => 11 - 4 * Math.Clamp((x - 20) / 75, 0, 1);

        double Outline(double x, double y)
        {
            double big = Math.Sqrt(x * x + y * y) - rb;
            double small = Math.Sqrt((x - lc) * (x - lc) + y * y) - rs;
            double shank = Math.Max(Math.Abs(y) - ShankHalfWidth(x), Math.Max(-x, x - lc));
            return Math.Min(big, Math.Min(small, shank));
        }

        double Depth(double x, double y)
        {
            double s = Outline(x, y);
            if (s >= 0)
                return s < landW ? land : Math.Min(gutter, land + (s - landW) * 0.8);

            double rBig = Math.Sqrt(x * x + y * y), rSmall = Math.Sqrt((x - lc) * (x - lc) + y * y);
            double feature = double.MinValue;
            if (rBig < rb) feature = Math.Max(feature, rBig < rbBore ? Math.Max(dWeb, dBig - (rbBore - rBig) * CotDraft) : dBig);
            if (rSmall < rs) feature = Math.Max(feature, rSmall < rsBore ? Math.Max(dWeb, dSmall - (rsBore - rSmall) * CotDraft) : dSmall);
            double w = ShankHalfWidth(x);
            if (x > 0 && x < lc && Math.Abs(y) < w)
                feature = Math.Max(feature, Math.Min(dRib, dShankWeb + Math.Max(0, Math.Abs(y) - (w - ribW)) * CotDraft));
            if (feature == double.MinValue) feature = dShankWeb;
            return Math.Min(feature, land + (-s) * CotDraft);
        }

        return new DieSet3D
        {
            Name = "Connecting rod (3D)",
            Description = "Big end Ø52×26, small end Ø30×20, 110 mm centres, I-beam shank, 1.8 mm flash. Half model, round-bar billet.",
            Bottom = HeightFieldDie.Create(DieSide.Bottom, (x, y) => -Depth(x, y), -45, 145, -45, 45, 0.5, 1.5),
            TopClosed = HeightFieldDie.Create(DieSide.Top, (x, y) => Depth(x, y), -45, 145, -45, 45, 0.5, 1.5),
            Symmetry = Symmetry3D.Half,
            Billet = BilletShape.HorizontalBar,
            Outline = Outline,
            XMin = -40,
            XMax = 140,
            YMax = 40,
            BilletCenterX = (-rb + lc + rs) / 2,
            RecommendedRadius = 12.5,
            RecommendedLength = lc + rb + rs - 6,
            VolumeFactor = 1.10,
            RecommendedElementSize = 3.0,
            RecommendedStrokeIncrement = 0.3,
        };
    }
}
