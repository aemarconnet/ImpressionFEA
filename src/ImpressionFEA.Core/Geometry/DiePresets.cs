namespace ImpressionFEA.Core.Geometry;

/// <summary>
/// Library of closed-die impression profiles (closed position, mm). Flash lands follow the thick side of the ASM flash
/// guidance for the part's steel weight (t about 1.25–2× the survey curve, w/t about 3; see <see cref="FlashDesign"/>).
/// </summary>
public static class DiePresets
{
    private static readonly double Draft = Math.Tan(7.0 * Math.PI / 180.0);

    public static IReadOnlyList<DieSet> All { get; } =
    [
        SimpleUpsetBlock(),
        GearBlank(),
        FlangedShaft(),
        ConnectingRodShank(),
    ];

    /// <summary>Flat parallel dies: open-die upsetting of a cylinder to a fixed shut height (benchmark case).</summary>
    public static DieSet SimpleUpsetBlock()
    {
        const double shut = 20.0;
        const double radius = 20.0;
        return new DieSet
        {
            Name = "Simple upset block",
            Description = "Flat dies, axisymmetric upsetting to 20 mm shut height. Benchmark for friction / barrelling.",
            Symmetry = Symmetry.Axisymmetric,
            Bottom = new DieSurface(DieSide.Bottom, [new Vec2(0, 0), new Vec2(200, 0)]),
            TopClosed = new DieSurface(DieSide.Top, [new Vec2(0, shut), new Vec2(200, shut)]),
            CavityOuterRadius = 32.0,
            ViewExtent = 46.0,
            RecommendedBilletRadius = radius,
            VolumeFactor = 1.0,
            RecommendedElementSize = 1.25,
            RecommendedStrokeIncrement = 0.25,
        };
    }

    /// <summary>
    /// Gear blank: central hub, thin web, outer rim, flash land and gutter. Symmetric about the parting plane. About
    /// 1.4 kg in steel: ASM survey t ≈ 2.1 mm, w/t ≈ 3.1; this die uses t = 3 mm (1.4×) with a 9 mm flat land (w/t 3).
    /// </summary>
    public static DieSet GearBlank()
    {
        double[,] depth =
        {
            { 0, 20 }, { 18, 20 }, { 18 + 14 * Draft, 6 }, { 44, 6 }, { 44 + 6 * Draft, 12 },
            { 54, 12 }, { 54 + 10.5 * Draft, 1.5 }, { 68.5, 1.5 }, { 70.5, 5 }, { 110, 5 },
        };
        var (bottom, top) = Mirrored(depth, 4.0);
        return new DieSet
        {
            Name = "Gear blank",
            Description = "Hub Ø36 × 40, web 12 mm, rim Ø108 × 24, flash 3 mm with a 9 mm land (w/t 3). 7° draft.",
            Flash = new FlashLand(3.0, 9.0),
            Symmetry = Symmetry.Axisymmetric,
            Bottom = new DieSurface(DieSide.Bottom, bottom),
            TopClosed = new DieSurface(DieSide.Top, top),
            CavityOuterRadius = 54 + 10.5 * Draft,
            ViewExtent = 76.0,
            RecommendedBilletRadius = 28.0,
            VolumeFactor = 1.10,
            RecommendedElementSize = 1.5,
            RecommendedStrokeIncrement = 0.5,
        };
    }

    /// <summary>
    /// Flanged shaft: bar seated in a lower shank pocket, head upset into a flange cavity in the top die. About 0.5 kg in
    /// steel: ASM survey t ≈ 1.55 mm, w/t ≈ 3.6; this die uses t = 2 mm (1.3×) with a 6 mm flat land (w/t 3).
    /// </summary>
    public static DieSet FlangedShaft()
    {
        double wallDraft = Math.Tan(1.0 * Math.PI / 180.0);
        var bottom = DieSurface.Fillet(
            [new Vec2(0, -30), new Vec2(15.25, -30), new Vec2(15.25 + 30 * wallDraft, 0), new Vec2(140, 0)], 4.0);
        var top = DieSurface.Fillet(
            [new Vec2(0, 10), new Vec2(35, 10), new Vec2(35 + 8 * Draft, 2), new Vec2(43.8, 2), new Vec2(45.8, 6), new Vec2(140, 6)], 2.0);
        return new DieSet
        {
            Name = "Flanged shaft",
            Description = "Ø30 shank in a 30 mm lower pocket; Ø70 × 10 flange in the top die, flash 2 mm with a 6 mm land (w/t 3).",
            Flash = new FlashLand(2.0, 6.0),
            Symmetry = Symmetry.Axisymmetric,
            Bottom = new DieSurface(DieSide.Bottom, bottom),
            TopClosed = new DieSurface(DieSide.Top, top),
            CavityOuterRadius = 35 + 8 * Draft,
            ViewExtent = 50.0,
            RecommendedBilletRadius = 15.0,
            VolumeFactor = 1.06,
            RecommendedElementSize = 1.5,
            RecommendedStrokeIncrement = 0.4,
        };
    }

    /// <summary>
    /// Connecting rod shank I-section (plane strain): 6 mm web, 18 mm ribs. The flash is that of a whole rod of about
    /// 0.5 kg (ASM survey t ≈ 1.55 mm, w/t ≈ 3.6): t = 2.5 mm (1.6×) with a 7.5 mm flat land (w/t 3), as in the book's
    /// connecting-rod example (t = 0.1 in, w = 0.3 in, Table 15.2).
    /// </summary>
    public static DieSet ConnectingRodShank()
    {
        double[,] depth =
        {
            { 0, 3 }, { 7.5, 3 }, { 7.5 + 6 * Draft, 9 }, { 11, 9 }, { 11 + 7.75 * Draft, 1.25 },
            { 20.85, 1.25 }, { 22.35, 4 }, { 60, 4 },
        };
        var (bottom, top) = Mirrored(depth, 1.5);
        return new DieSet
        {
            Name = "Connecting rod (shank I-section)",
            Description = "Plane-strain I-beam shank: web 6 mm, ribs 24 × 18 mm, flash 2.5 mm with a 7.5 mm land (w/t 3). Length = shank length.",
            Flash = new FlashLand(2.5, 7.5),
            Symmetry = Symmetry.PlaneStrain,
            Bottom = new DieSurface(DieSide.Bottom, bottom),
            TopClosed = new DieSurface(DieSide.Top, top),
            CavityOuterRadius = 11 + 7.75 * Draft,
            ViewExtent = 28.0,
            RecommendedBilletRadius = 9.5,
            RecommendedLength = 120,
            VolumeFactor = 1.10,
            RecommendedElementSize = 0.5,
            RecommendedStrokeIncrement = 0.05,
        };
    }

    private static (List<Vec2> Bottom, List<Vec2> Top) Mirrored(double[,] depth, double fillet)
    {
        var bottom = new List<Vec2>();
        var top = new List<Vec2>();
        for (int i = 0; i < depth.GetLength(0); i++)
        {
            bottom.Add(new Vec2(depth[i, 0], -depth[i, 1]));
            top.Add(new Vec2(depth[i, 0], depth[i, 1]));
        }

        return (DieSurface.Fillet(bottom, fillet), DieSurface.Fillet(top, fillet));
    }
}
