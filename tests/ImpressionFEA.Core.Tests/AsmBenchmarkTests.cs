using ImpressionFEA.Core.Geometry;
using ImpressionFEA.Core.Materials;
using ImpressionFEA.Core.Simulation;
using ImpressionFEA.Core.Solver;
using ImpressionFEA.Core.ThreeD;
using Xunit.Abstractions;

namespace ImpressionFEA.Core.Tests;

/// <summary>
/// Benchmarks against the worked examples and methods of Altan, Ngaile &amp; Shen, "Cold and Hot Forging:
/// Fundamentals and Applications" (ASM International, 2005): the simplified slab method of §14.5.2 (Eqs 14.8–14.11),
/// the connecting-rod forging of Chapter 14 Appendix A (Tables A.1–A.4) and its ForgePAL inputs in Chapter 15
/// (Tables 15.2–15.4). Lines starting with "BENCH" are the comparison table.
/// </summary>
public class AsmBenchmarkTests(ITestOutputHelper output)
{
    private const double Inch = 25.4;
    private const double Psi = 0.006894757; // MPa
    private const double ShortTon = 8896.443; // N (2000 lbf)
    private const double Lbf = 4.448222; // N

    /// <summary>
    /// Nearly rate-insensitive material, σ̄ = C·ε̄̇^0.02 (no hardening). A truly rate-independent material (m = 0) is
    /// singular in the rigid-viscoplastic formulation (rigid zones have no stiffness), so the slab estimate is evaluated
    /// with this law at the cavity (v/H) and flash (v/t) strain rates instead.
    /// </summary>
    private static MaterialPreset NearlyRigidPlastic(double stressAtUnitRate) =>
        new("Rigid-plastic", "test", CRef: stressAtUnitRate, N: 0, M: 0.02, Eps0: 1, TRef: 1000, Beta: 0, TMin: 0, TMax: 2000, DefaultTemperature: 1000);

    private void Bench(string label, double ours, double reference, string unit) =>
        output.WriteLine($"BENCH | {label} | ours {ours:F1} {unit} | reference {reference:F1} {unit} | {100 * (ours / reference - 1):+0.0;-0.0} %");

    // ================================================================== 1. the book's own numbers, by our equations

    /// <summary>
    /// Table 15.2 inputs (t = 0.1 in, w = 0.3 in, L = 0.5482 in, Ls = 5.113 in, H = 0.56 in, m = 0.3) with the flash
    /// flow stress of Table 15.3 (18,467 psi) reproduce the flash loads of Table 15.3: 18 tons (axisymmetric) and
    /// 47 tons (plane strain).
    /// </summary>
    [Fact]
    public void SlabEquationsReproduceTable15_3FlashLoads()
    {
        double l = 0.5482 * Inch, ls = 5.113 * Inch, h = 0.56 * Inch, t = 0.1 * Inch, w = 0.3 * Inch;
        double sc = 15298 * Psi, sf = 18467 * Psi;
        var axi = SlabLoadEstimate.Axisymmetric(l, h, t, w, 0.3, sc, sf, CavityFlow.DiskShearing);
        var ps = SlabLoadEstimate.PlaneStrain(l, h, t, w, 0.3, sc, sf, ls, CavityFlow.DiskShearing);
        var axiSliding = SlabLoadEstimate.Axisymmetric(l, h, t, w, 0.3, sc, sf);
        var psSliding = SlabLoadEstimate.PlaneStrain(l, h, t, w, 0.3, sc, sf, ls);
        Bench("Table 15.3 axisymmetric flash load (Eq 14.9 flash term)", axi.Flash / ShortTon, 18, "ton");
        Bench("Table 15.3 plane-strain flash load (Eq 14.11 flash term)", ps.Flash / ShortTon, 47, "ton");
        Bench("Table 15.3 axisymmetric cavity load, disk shearing (Table 15.1)", axi.Cavity / ShortTon, 34, "ton");
        Bench("Table 15.3 plane-strain cavity load, disk shearing (Table 15.1)", ps.Cavity / ShortTon, 241, "ton");
        Bench("Table 15.3 total load (ForgePAL 340 tons)", (axi.Total + ps.Total) / ShortTon, 340, "ton");
        output.WriteLine($"BENCH | same with sliding in the cavity (Eqs 14.9 / 14.11): cavity {axiSliding.Cavity / ShortTon:F1} + {psSliding.Cavity / ShortTon:F1} ton, total {(axiSliding.Total + psSliding.Total) / ShortTon:F1} ton");
        Assert.InRange(axi.Flash / ShortTon, 17.5, 18.5);
        Assert.InRange(ps.Flash / ShortTon, 46.5, 47.5);
        Assert.InRange(axi.Cavity / ShortTon / 34, 0.95, 1.05);
        Assert.InRange(ps.Cavity / ShortTon / 241, 0.95, 1.05);
    }

    /// <summary>
    /// Appendix A, section B-B of the blocker (plane strain, L = 0.93 in, H = 0.32 in, length 2 in, m = 0.7,
    /// σ̄ = 17,000 psi, flash land 0.1 × 0.31 in): Table A.3 gives 196,998 lb. The book used the flash thickness measured
    /// on the parts (not listed), so the land thickness is only nominal here.
    /// </summary>
    [Fact]
    public void SlabEquationsReproduceAppendixASectionBB()
    {
        double s = 17000 * Psi;
        var nominal = SlabLoadEstimate.PlaneStrain(0.93 / 2 * Inch, 0.32 * Inch, 0.1 * Inch, 0.31 * Inch, 0.7, s, s, 2 * Inch);
        Bench("Table A.3 blocker B-B, nominal flash 0.100 in", nominal.Total / Lbf, 196998, "lb");
        // Flash thickness that reproduces the book's value (the measured flash was thinner than the 0.1 in land).
        double lo = 0.05, hi = 0.1;
        for (int i = 0; i < 60; i++)
        {
            double mid = 0.5 * (lo + hi);
            double p = SlabLoadEstimate.PlaneStrain(0.93 / 2 * Inch, 0.32 * Inch, mid * Inch, 0.31 * Inch, 0.7, s, s, 2 * Inch).Total / Lbf;
            if (p > 196998) lo = mid;
            else hi = mid;
        }

        output.WriteLine($"BENCH | Table A.3 blocker B-B is matched with a flash thickness of {lo:F4} in");
        Assert.InRange(nominal.Total / Lbf / 196998, 0.90, 1.10);
    }

    /// <summary>
    /// Appendix A.2: AISI 1016 at 2130 °F (1165 °C), ε̄ = 0.85, ε̄̇ = 25.55 /s → σ̄ = 17,964 psi (124 MPa), from Table A.1.
    /// Our AISI 1020 preset is fitted to the AISI 1016 rows of Table 4.7.
    /// </summary>
    [Fact]
    public void FlowStressMatchesAppendixA2()
    {
        var m = MaterialPresets.All.First(x => x.Name.StartsWith("AISI 1020", StringComparison.Ordinal));
        double ours = m.At(1165).Stress(0.85, 25.55);
        Bench("App. A.2 flow stress AISI 1016, 1165 °C, ε 0.85, 25.55 /s", ours, 17964 * Psi, "MPa");
        Assert.InRange(ours / (17964 * Psi), 0.90, 1.10);
    }

    // ================================================================== 2. FEM vs Eqs 14.8–14.11 on the Fig. 14.15 die

    /// <summary>
    /// The idealised die of Fig. 14.15: a flat-bottomed cavity of radius (half-width) r and height H, a flash land
    /// t × w, then a deep gutter. 3° draft and 1 mm fillets so the remesher has well-defined corners.
    /// </summary>
    private static DieSet IdealDie(Symmetry symmetry, double r, double h, double t, double w)
    {
        double draft = Math.Tan(3 * Math.PI / 180);
        double rTop = r - (h - t) / 2 * draft;
        double gutter = Math.Max(h / 2, t / 2 + 6), xEnd = r + w + 3 * h + 20;
        double[,] half =
        {
            { 0, h / 2 }, { rTop, h / 2 }, { r, t / 2 }, { r + w, t / 2 }, { r + w + (gutter - t / 2) * Math.Tan(Math.PI / 6), gutter }, { xEnd, gutter },
        };
        var bottom = new List<Vec2>();
        var top = new List<Vec2>();
        for (int i = 0; i < half.GetLength(0); i++)
        {
            bottom.Add(new Vec2(half[i, 0], -half[i, 1]));
            top.Add(new Vec2(half[i, 0], half[i, 1]));
        }

        return new DieSet
        {
            Name = "Fig. 14.15 die",
            Description = "Idealised closed die with flash land",
            Symmetry = symmetry,
            Bottom = new DieSurface(DieSide.Bottom, DieSurface.Fillet(bottom, 1.0)),
            TopClosed = new DieSurface(DieSide.Top, DieSurface.Fillet(top, 1.0)),
            CavityOuterRadius = r,
            ViewExtent = xEnd,
            RecommendedBilletRadius = 0.75 * r,
            VolumeFactor = 1.10,
            RecommendedElementSize = 1,
            RecommendedStrokeIncrement = 0.1,
        };
    }

    /// <summary>Forges to die closure; returns the load at closure (the peak) and the final fill.</summary>
    private (double Load, double Fill, double PeakLoad) ForgeToClosure(SimulationConfig cfg)
    {
        var log = new List<string>();
        var solver = new ForgingSolver(cfg, log.Add);
        StepSnapshot last = solver.InitialSnapshot();
        double peak = 0;
        while (!solver.IsFinished)
        {
            last = solver.Advance(CancellationToken.None);
            peak = Math.Max(peak, last.LoadNewton);
        }

        output.WriteLine(log.LastOrDefault(l => l.StartsWith("Dies closed", StringComparison.Ordinal) || l.StartsWith("step", StringComparison.Ordinal)) ?? "");
        return (last.LoadNewton, last.FillPercent, peak);
    }

    private SimulationConfig Ideal(Symmetry symmetry, double r, double h, double t, double w, double m, MaterialPreset material, double ramSpeed = 50,
        double temperature = 1000, double billetHeightRatio = 0)
    {
        var die = IdealDie(symmetry, r, h, t, w);
        double length = 100, radius = 0.75 * r;
        double height = billetHeightRatio > 0 ? billetHeightRatio * h : die.RecommendedHeight(radius, length);
        if (billetHeightRatio > 0)
        {
            // Given billet height: radius (half-width) for 110 % of the cavity volume.
            double v = die.VolumeFactor * die.CavityVolume(length);
            radius = symmetry == Symmetry.Axisymmetric ? Math.Sqrt(v / (Math.PI * height)) : v / (2 * height * length);
        }
        return new SimulationConfig
        {
            Die = die,
            Material = material,
            Temperature = temperature,
            BilletRadius = radius,
            BilletHeight = height,
            BilletLength = length,
            FrictionFactor = m,
            ElementSize = Math.Min(1.0, t / 2),
            StrokeIncrement = Math.Min(0.1, t / 10),
            RamSpeed = ramSpeed,
        };
    }

    [Theory]
    [Trait("Category", "Slow")]
    [InlineData("axisymmetric", 30, 20, 2, 8, 0.3)]
    [InlineData("axisymmetric", 30, 20, 2, 8, 0.1)]
    [InlineData("axisymmetric", 30, 20, 2, 4, 0.3)]
    [InlineData("plane strain", 30, 20, 2, 8, 0.3)]
    [InlineData("plane strain", 30, 20, 2, 8, 0.1)]
    public void FemMatchesSlabMethodOnTheIdealDie(string kind, double r, double h, double t, double w, double m)
    {
        var symmetry = kind == "axisymmetric" ? Symmetry.Axisymmetric : Symmetry.PlaneStrain;
        var material = NearlyRigidPlastic(100);
        var cfg = Ideal(symmetry, r, h, t, w, m, material);
        var (load, fill, _) = ForgeToClosure(cfg);
        double sc = material.At(1000).Stress(1, cfg.RamSpeed / h), sf = material.At(1000).Stress(1, cfg.RamSpeed / t);
        SlabLoad Slab(CavityFlow flow) => symmetry == Symmetry.Axisymmetric
            ? SlabLoadEstimate.Axisymmetric(r, h, t, w, m, sc, sf, flow)
            : SlabLoadEstimate.PlaneStrain(r, h, t, w, m, sc, sf, cfg.BilletLength, flow);
        var sliding = Slab(CavityFlow.Sliding);
        var disk = Slab(CavityFlow.DiskShearing);
        Bench($"Fig. 14.15 die, {kind}, r {r} H {h} t {t} w {w} m {m}: FEM at closure (fill {fill:F1} %) vs Eq 14.9/14.11 sliding (cavity {sliding.Cavity / 1000:F0} + flash {sliding.Flash / 1000:F0} kN)",
            load / 1000, sliding.Total / 1000, "kN");
        Bench($"  same vs Table 15.1 disk shearing (cavity {disk.Cavity / 1000:F0} + flash {disk.Flash / 1000:F0} kN)", load / 1000, disk.Total / 1000, "kN");
        Assert.True(fill > 99, $"fill {fill:F1} %");
        // The FEM must fall between the two idealised flow modes, give or take 15 %.
        Assert.InRange(load, 0.85 * Math.Min(sliding.Total, disk.Total), 1.15 * Math.Max(sliding.Total, disk.Total));
    }

    /// <summary>Mesh convergence of the closure load on the Fig. 14.15 die (axisymmetric, t = 2 mm, w = 8 mm, m = 0.3): h = t/2 … t/6.</summary>
    [Theory]
    [Trait("Category", "Slow")]
    [InlineData(1.0)]
    [InlineData(0.67)]
    [InlineData(0.5)]
    [InlineData(0.33)]
    public void IdealDieMeshConvergence(double elementSize)
    {
        if (Environment.GetEnvironmentVariable("IFEA_STUDY") is null) return; // up to 40 min per case: a study, not a regression test
        var cfg = Ideal(Symmetry.Axisymmetric, 30, 20, 2, 8, 0.3, NearlyRigidPlastic(100)) with { ElementSize = elementSize, StrokeIncrement = Math.Min(0.1, elementSize / 5) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (load, fill, _) = ForgeToClosure(cfg);
        output.WriteLine($"BENCH | mesh convergence, h {elementSize:F2} mm ({2 / elementSize:F1} elements across the 2 mm flash): load at closure {load / 1000:F0} kN, fill {fill:F1} %, {sw.Elapsed.TotalMinutes:F1} min");
    }

    // ================================================================== 3. the connecting rod of App. A / Table 15.4

    /// <summary>
    /// The connecting-rod blocker forging (App. A; ForgePAL inputs Table 15.2): simplified as an axisymmetric part of
    /// radius L = 0.5482 in plus a plane-strain part 2L wide and Ls = 5.113 in long, cavity height 0.56 in, flash
    /// 0.1 × 0.3 in, m = 0.3, billet 1.12 in high. Measured load 320.67 tons (Table A.4; 320 in Table 15.4), slab
    /// estimate 311.7 tons (App. A), ForgePAL 340 tons. Our model: AISI 1020 (fitted to the AISI 1016 rows) at
    /// 1124 °C (Table 15.3 cavity temperature), constant ram speed 13.67 in/s (App. A.2), isothermal.
    /// </summary>
    /// <param name="material">"AISI 1020": our preset (fitted to the AISI 1016 rows) at 1124 °C; "book σ̄": nearly
    /// rate-insensitive, 105.5 MPa at the cavity rate (Table 15.3 cavity flow stress, 15,298 psi).</param>
    /// <param name="speedFactor">Ram speed as a fraction of the App. A.2 average, 13.67 in/s. A crank press slows
    /// towards bottom dead centre, where the peak load occurs; 0.25 brackets that.</param>
    [Theory]
    [Trait("Category", "Slow")]
    [InlineData("AISI 1020", 1.0)]
    [InlineData("AISI 1020", 0.25)]
    [InlineData("book σ̄", 1.0)]
    public void ConnectingRodBlockerLoad(string material, double speedFactor)
    {
        double l = 0.5482 * Inch, ls = 5.113 * Inch, h = 0.56 * Inch, t = 0.1 * Inch, w = 0.3 * Inch, h0 = 1.12 * Inch, v = speedFactor * 13.67 * Inch;
        var mat = material == "AISI 1020"
            ? MaterialPresets.All.First(x => x.Name.StartsWith("AISI 1020", StringComparison.Ordinal))
            : NearlyRigidPlastic(15298 * Psi / Math.Pow(v / h, 0.02));
        double temp = material == "AISI 1020" ? 1124 : 1000;
        var axiCfg = Ideal(Symmetry.Axisymmetric, l, h, t, w, 0.3, mat, v, temp, h0 / h);
        var psCfg = Ideal(Symmetry.PlaneStrain, l, h, t, w, 0.3, mat, v, temp, h0 / h) with { BilletLength = ls };
        var axi = ForgeToClosure(axiCfg);
        var ps = ForgeToClosure(psCfg);
        double total = (axi.Load + ps.Load) / ShortTon;
        string tag = $"{material}, v {v:F0} mm/s";
        output.WriteLine($"BENCH | connecting rod FEM ({tag}): axisymmetric {axi.Load / ShortTon:F1} ton (fill {axi.Fill:F1} %), plane strain {ps.Load / ShortTon:F1} ton (fill {ps.Fill:F1} %)");
        Bench($"Connecting-rod blocker ({tag}), FEM total vs measured (Table A.4)", total, 320.67, "ton");
        Bench($"Connecting-rod blocker ({tag}), FEM total vs App. A slab estimate (Table A.4)", total, 311.7, "ton");
        Bench($"Connecting-rod blocker ({tag}), FEM total vs ForgePAL (Table 15.4)", total, 340, "ton");

        // The slab estimate for the same simplified geometry and flow stresses as the FEM run.
        double sc = mat.At(temp).Stress(1.0, v / h), sf = mat.At(temp).Stress(1.0, v / t);
        var sa = SlabLoadEstimate.Axisymmetric(l, h, t, w, 0.3, sc, sf, CavityFlow.DiskShearing);
        var sp = SlabLoadEstimate.PlaneStrain(l, h, t, w, 0.3, sc, sf, ls, CavityFlow.DiskShearing);
        var sa2 = SlabLoadEstimate.Axisymmetric(l, h, t, w, 0.3, sc, sf);
        var sp2 = SlabLoadEstimate.PlaneStrain(l, h, t, w, 0.3, sc, sf, ls);
        output.WriteLine($"BENCH | slab, same σ̄ (cavity {sc:F0} MPa at {v / h:F0}/s, flash {sf:F0} MPa at {v / t:F0}/s): disk shearing {(sa.Total + sp.Total) / ShortTon:F1} ton, sliding {(sa2.Total + sp2.Total) / ShortTon:F1} ton");
        Assert.True(axi.Fill > 99 && ps.Fill > 99);
    }

    // ================================================================== 4. our presets vs the slab estimate

    /// <summary>
    /// Gear blank (2D): rim radius 55.3 mm, the die's flash land (3 × 9 mm flat), average cavity height from volume / plan area;
    /// AISI 4140 at 1150 °C, 50 mm/s, m = 0.3. σ̄ at ε̄ = 1 and the cavity (v/H) and flash (v/t) strain rates.
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public void GearBlankFemVsSlab()
    {
        var die = DiePresets.All.Single(d => d.Name == "Gear blank");
        var cfg = new SimulationConfig
        {
            Die = die,
            Material = MaterialPresets.Steel4140,
            Temperature = 1150,
            BilletRadius = die.RecommendedBilletRadius,
            BilletHeight = die.RecommendedHeight(die.RecommendedBilletRadius, die.RecommendedLength),
            BilletLength = die.RecommendedLength,
            FrictionFactor = 0.3,
            ElementSize = die.RecommendedElementSize,
            StrokeIncrement = die.RecommendedStrokeIncrement,
            RamSpeed = 50,
        };
        var (load, fill, peak) = ForgeToClosure(cfg);
        double r = die.CavityOuterRadius, t = die.Flash!.Thickness, w = die.Flash.Width;
        double hAvg = (die.CavityVolume(1) - FlashVolume(r, t, w)) / (Math.PI * r * r);
        double sc = MaterialPresets.Steel4140.At(1150).Stress(1, 50 / hAvg), sf = MaterialPresets.Steel4140.At(1150).Stress(1, 50 / t);
        var slab = SlabLoadEstimate.Axisymmetric(r, hAvg, t, w, 0.3, sc, sf);
        Bench($"Gear blank FEM at closure (fill {fill:F1} %) vs slab (H̄ {hAvg:F1} mm, σc {sc:F0}, σf {sf:F0} MPa)", load / ShortTon, slab.Total / ShortTon, "ton");
        var disk = SlabLoadEstimate.Axisymmetric(r, hAvg, t, w, 0.3, sc, sf, CavityFlow.DiskShearing);
        Bench("  gear blank FEM vs slab, disk shearing", load / ShortTon, disk.Total / ShortTon, "ton");
        output.WriteLine($"BENCH | gear blank FEM peak over the stroke {peak / ShortTon:F1} ton");
    }

    private static double FlashVolume(double r, double t, double w) => Math.PI * ((r + w) * (r + w) - r * r) * t;

    /// <summary>
    /// 1"-8 hex bolt head (3D, quarter model): hexagon 38.1 mm across flats as an equal-area disc, head height 15.5 mm,
    /// the die's flash land (2 × 5.8 mm flat); AISI 4140 at 1150 °C, 50 mm/s, m = 0.3.
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public void HexBoltHeadFemVsSlab()
    {
        var die = DiePresets3D.All.Single(d => d.Name.StartsWith("Hex bolt head", StringComparison.Ordinal));
        var (r0, dim) = die.RecommendedBillet();
        var cfg0 = new SimulationConfig3D { Die = die, Material = MaterialPresets.Steel4140, Temperature = 1150, BilletRadius = r0, BilletDimension = dim, FrictionFactor = 0.3, RamSpeed = 50 };
        double hAuto = cfg0.CalibratedAutoElementSize();
        var cfg = cfg0 with { ElementSize = hAuto, StrokeIncrement = SimulationConfig3D.AutoStrokeIncrement(hAuto) };
        using var solver = new Forging3DSolver(cfg, _ => { });
        ForgingSnapshot s = solver.Initial();
        while (!solver.IsFinished) s = solver.Step(CancellationToken.None);
        double af = 38.1, rEq = Math.Sqrt(Math.Sqrt(3) / 2 * af * af / Math.PI), hHead = 15.5, t = die.Flash!.Thickness, w = die.Flash.Width;
        double sc = MaterialPresets.Steel4140.At(1150).Stress(1, 50 / hHead), sf = MaterialPresets.Steel4140.At(1150).Stress(1, 50 / t);
        var slab = SlabLoadEstimate.Axisymmetric(rEq, hHead, t, w, 0.3, sc, sf);
        Bench($"Hex bolt head FEM at closure (fill {s.FillPercent:F1} %, h {hAuto:F2} mm) vs slab (r_eq {rEq:F1} mm, σc {sc:F0}, σf {sf:F0} MPa)", s.LoadNewton / ShortTon, slab.Total / ShortTon, "ton");
        Bench("  hex bolt head FEM vs slab, disk shearing", s.LoadNewton / ShortTon, SlabLoadEstimate.Axisymmetric(rEq, hHead, t, w, 0.3, sc, sf, CavityFlow.DiskShearing).Total / ShortTon, "ton");
    }
}
