using ImpressionFEA.Core.Geometry;

namespace ImpressionFEA.Core.ThreeD;

/// <summary>Built-in 3D closed-die impressions (closed position, mm). Height fields are sampled at 0.5 mm.</summary>
public static class DiePresets3D
{
    private static readonly double CotDraft = 1 / Math.Tan(7.0 * Math.PI / 180.0);

    private static readonly Lazy<IReadOnlyList<DieSet3D>> Lazy = new(() => [ConnectingRod(), GearBlank(), SimpleUpsetBlock(), HexBoltHead()]);

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
            Description = "2D gear-blank impression revolved into 3D dies (flash 3 mm with a 9 mm land, w/t 3), quarter model. Cross-check against the 2D axisymmetric solver.",
            Flash = g.Flash,
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
    /// 1"-8 hex bolt head upset from 1" bar, quarter model (the hexagon, bore and bar are symmetric about x and y). Top die: hex pocket 1-1/2" across flats (flats
    /// parallel to x) × 39/64" deep with drafted walls, a flash land around it (1 mm edge radius, then flat), a 45° gutter step and
    /// a flat outer face. Bottom die: flat face (the parting plane, z = 0) with a Ø1" shank bore 1-1/2" deep and a 45° entry
    /// chamfer. Both dies are exact closed solids made of flat faces and long cylindrical facets (no height-field
    /// sampling). Dimensions in mm; <paramref name="flashThickness"/> is the closed gap over the land and
    /// <paramref name="flashWidth"/> the land from the hex wall to the gutter, including the 1 mm edge radius.
    /// <para>
    /// Flash design: the forging weighs about 0.31 kg in steel, for which the ASM survey gives t ≈ 1.4 mm and w/t ≈ 3.8.
    /// The default is the thick side (see <see cref="FlashDesign"/>): t = 2 mm (≈ 5/64", 1.4×) on a 1/4" land, 5.8 mm of it flat
    /// (w/t 2.9).
    /// </para>
    /// </summary>
    public static DieSet3D HexBoltHead(
        double flashThickness = 2.0, double acrossFlats = 38.1, double headHeight = 15.5, double shankDiameter = 25.4,
        double shankLength = 38.1, double flashWidth = 6.35, double draftDeg = 3)
    {
        double s = acrossFlats / 2, tf = flashThickness;
        double rBore = shankDiameter / 2 + 0.3; // radial clearance so the bar drops into the bore
        const double chamfer = 1.0, gutterDepth = 5, back = 20, segments = 120;
        double sTop = s - (headHeight - tf) * Math.Tan(draftDeg * Math.PI / 180); // hex at the head top (drafted walls)
        double sOut = s + flashWidth + gutterDepth + 12; // outer boundary of both dies (hexagonal plan)
        double c30 = Math.Cos(Math.PI / 6), s30 = Math.Sin(Math.PI / 6);

        // Plan outline: signed distance to the hexagon at the parting plane (negative inside).
        double Hex(double x, double y) => Math.Max(Math.Abs(y), Math.Max(Math.Abs(x * c30 + y * s30), Math.Abs(x * c30 - y * s30))) - s;

        // Hexagon (vertex on +x, so flats are parallel to x) of half across-flats h, at height z.
        static Vec3[] HexRing(double h, double z) => [.. Enumerable.Range(0, 6).Select(k =>
        {
            double a = k * Math.PI / 3, r = h / Math.Cos(Math.PI / 6);
            return new Vec3(r * Math.Cos(a), r * Math.Sin(a), z);
        })];

        // n points around a circle, or on a hexagon boundary at the same angles (so rings pair up into planar quads).
        Vec3[] CircleRing(double r, double z) => [.. Enumerable.Range(0, (int)segments).Select(k =>
        {
            double a = 2 * Math.PI * k / segments;
            return new Vec3(r * Math.Cos(a), r * Math.Sin(a), z);
        })];
        Vec3[] HexBoundaryRing(double h, double z) => [.. Enumerable.Range(0, (int)segments).Select(k =>
        {
            double a = 2 * Math.PI * k / segments;
            double flat = Math.PI / 6 + Math.PI / 3 * Math.Floor(a / (Math.PI / 3)); // normal of the flat this ray meets
            double r = h / Math.Cos(a - flat);
            return new Vec3(r * Math.Cos(a), r * Math.Sin(a), z);
        })];

        // Fillet on the die edge where the cavity wall meets the flash land (the metal turns into the flash here): a sharp
        // edge lets element faces cut through it and causes most contact cutbacks. Radius 1 mm, at most a third of the land.
        double rf = Math.Min(1.0, flashWidth / 3);
        double landFlat = flashWidth - rf + Math.Sqrt(0.1 * rf * tf - 0.0025 * tf * tf); // flat land as FlashDesign measures it: plus the part of the radius within 5 % of t
        var edge = Enumerable.Range(0, 5).Select(k =>
        {
            double a = Math.PI + k * Math.PI / 8; // 180° (on the wall) … 270° (on the land), centre (s + rf, tf + rf)
            return HexRing(s + rf + rf * Math.Cos(a), tf + rf + rf * Math.Sin(a));
        });
        var top = Stack(
            new Vec3(0, 0, headHeight),
            [HexRing(sTop, headHeight), .. edge, HexRing(s + flashWidth, tf), HexRing(s + flashWidth + gutterDepth, tf + gutterDepth),
             HexRing(sOut, tf + gutterDepth), HexRing(sOut, tf + gutterDepth + back)],
            new Vec3(0, 0, tf + gutterDepth + back));
        var bottom = Stack(
            new Vec3(0, 0, -shankLength),
            [CircleRing(rBore, -shankLength), CircleRing(rBore, -chamfer), CircleRing(rBore + chamfer, 0),
             HexBoundaryRing(sOut, 0), HexBoundaryRing(sOut, -shankLength - back)],
            new Vec3(0, 0, -shankLength - back));

        double ext = sOut / c30;
        return new DieSet3D
        {
            Name = "Hex bolt head 1\" (3D)",
            Description = FormattableString.Invariant(
                $"1\"-8 hex bolt: head {acrossFlats / 25.4:0.###}\" across flats × {headHeight / 25.4:0.###}\" ({draftDeg:0.#}° draft) in the top die, Ø{shankDiameter / 25.4:0.###}\" × {shankLength / 25.4:0.##}\" shank in the bottom-die bore, flash {tf / 25.4:0.###}\" thick with a {landFlat / 25.4:0.###}\" flat land (w/t {landFlat / tf:0.0}). Quarter model, flat and cylindrical die faces, upset from bar."),
            Bottom = new TriMeshDie(DieSide.Bottom, bottom),
            TopClosed = new TriMeshDie(DieSide.Top, top),
            Symmetry = Symmetry3D.Quarter,
            Billet = BilletShape.VerticalCylinder,
            Outline = Hex,
            XMin = -ext,
            XMax = ext,
            YMax = ext,
            RecommendedRadius = shankDiameter / 2,
            VolumeFactor = 1.08,
            RecommendedElementSize = 3.0,
            RecommendedStrokeIncrement = 0.5,
            FlashThickness = tf,
            Flash = new FlashLand(tf, landFlat),
            WithFlashThickness = t => HexBoltHead(t, acrossFlats, headHeight, shankDiameter, shankLength, flashWidth, draftDeg),
        };
    }

    /// <summary>
    /// Closed solid from a stack of rings (same vertex count each) between two poles, like a surface of revolution:
    /// fans at the poles, planar quads between rings. Oriented outward (flipped if the signed volume is negative).
    /// </summary>
    private static TriMeshSolid Stack(Vec3 poleStart, IReadOnlyList<Vec3[]> rings, Vec3 poleEnd)
    {
        int n = rings[0].Length;
        var v = new List<Vec3> { poleStart };
        foreach (var ring in rings) v.AddRange(ring);
        v.Add(poleEnd);
        int last = v.Count - 1;
        int R(int ring, int k) => 1 + ring * n + (k % n);
        var t = new List<int>();
        for (int k = 0; k < n; k++) t.AddRange([0, R(0, k), R(0, k + 1)]);
        for (int i = 0; i + 1 < rings.Count; i++)
        {
            for (int k = 0; k < n; k++)
            {
                int a = R(i, k), b = R(i + 1, k), c = R(i + 1, k + 1), d = R(i, k + 1);
                t.AddRange([a, b, c, a, c, d]);
            }
        }

        int lr = rings.Count - 1;
        for (int k = 0; k < n; k++) t.AddRange([R(lr, k), last, R(lr, k + 1)]);

        double vol6 = 0;
        for (int i = 0; i < t.Count; i += 3) vol6 += v[t[i]].Dot(v[t[i + 1]].Cross(v[t[i + 2]]));
        if (vol6 < 0)
        {
            for (int i = 0; i < t.Count; i += 3) (t[i + 1], t[i + 2]) = (t[i + 2], t[i + 1]);
        }

        return new TriMeshSolid([.. v], [.. t]);
    }

    /// <summary>
    /// Connecting rod: big-end boss Ø52 × 26 with Ø28 bore web, small-end boss Ø30 × 20 with Ø16 bore web, 110 mm
    /// centres, tapered I-beam shank (web 6, ribs 16), flash 2.5 mm with a 7.5 mm flat land (w/t 3; ≈ 0.5 kg in steel, ASM survey t ≈ 1.55 mm, so the thick side) and gutter, 7° draft, ~1.5 mm fillets.
    /// Half model about the rod axis (y ≥ 0), forged from a straight round bar with 30 % excess stock (it does not fill
    /// completely without a preform). <paramref name="flashThickness"/> is the closed
    /// gap over the land and <paramref name="landWidth"/> the land from the impression edge to the gutter (mm).
    /// </summary>
    public static DieSet3D ConnectingRod(double flashThickness = 2.5, double landWidth = 9.6)
    {
        const double lc = 110, rb = 26, rbBore = 14, rs = 15, rsBore = 8;
        const double dBig = 13, dSmall = 10, dWeb = 2, dShankWeb = 3, dRib = 8, ribW = 4.5;
        double land = flashThickness / 2, landW = landWidth; // landW: from the impression edge to the gutter (≈ 2 mm of it is rounded by the 1.5 mm blur)
        const double gutter = 5;
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
            Description = "Big end Ø52×26, small end Ø30×20, 110 mm centres, I-beam shank, flash 2.5 mm with a 7.5 mm land (w/t 3). Half model, round-bar billet with 30 % excess (about 91 % fill: real rods are forged from a preform).",
            Flash = new FlashLand(flashThickness, landWidth - 2.1), // the blur rounds about 2.1 mm off the flat land
            Bottom = HeightFieldDie.Create(DieSide.Bottom, (x, y) => -Depth(x, y), -45, 145, -45, 45, 0.5, 1.5),
            TopClosed = HeightFieldDie.Create(DieSide.Top, (x, y) => Depth(x, y), -45, 145, -45, 45, 0.5, 1.5),
            Symmetry = Symmetry3D.Half,
            Billet = BilletShape.HorizontalBar,
            Outline = Outline,
            XMin = -40,
            XMax = 140,
            YMax = 40,
            BilletCenterX = (-rb + lc + rs) / 2,
            RecommendedRadius = 13.6, // bar radius for the 30 % excess below (RecommendedBillet recomputes it from volume)
            RecommendedLength = lc + rb + rs - 6,
            // A straight bar puts too little metal into the ends of a connecting rod (production forges it from a preform,
            // Altan et al. §14.6), and the thick-side flash lets more escape: 30 % excess stock, the top of the usual 10–30 %
            // (Fig. 14.11), fills about 91 % of the impression at closure (10 %: 83 %).
            VolumeFactor = 1.30,
            RecommendedElementSize = 3.0,
            RecommendedStrokeIncrement = 0.3,
        };
    }
}
