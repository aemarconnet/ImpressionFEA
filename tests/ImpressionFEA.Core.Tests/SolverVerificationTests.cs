using ImpressionFEA.Core.Geometry;
using ImpressionFEA.Core.Materials;
using ImpressionFEA.Core.Simulation;
using ImpressionFEA.Core.Solver;
using Xunit.Abstractions;

namespace ImpressionFEA.Core.Tests;

public class SolverVerificationTests(ITestOutputHelper output)
{
    private static readonly MaterialPreset PerfectViscoplastic =
        new("Test", "test", CRef: 100, N: 0, M: 0.1, Eps0: 1, TRef: 1000, Beta: 0, TMin: 0, TMax: 2000, DefaultTemperature: 1000);

    private static DieSet FlatDies(Symmetry symmetry, double shut) => new()
    {
        Name = "flat",
        Description = "flat",
        Symmetry = symmetry,
        Bottom = new DieSurface(DieSide.Bottom, [new Vec2(0, 0), new Vec2(200, 0)]),
        TopClosed = new DieSurface(DieSide.Top, [new Vec2(0, shut), new Vec2(200, shut)]),
        CavityOuterRadius = 60,
        ViewExtent = 60,
        RecommendedBilletRadius = 20,
        RecommendedElementSize = 2,
        RecommendedStrokeIncrement = 0.5,
    };

    private SimulationConfig Upset(Symmetry symmetry, double m) => new()
    {
        Die = FlatDies(symmetry, 30),
        Material = PerfectViscoplastic,
        Temperature = 1000,
        BilletRadius = 20,
        BilletHeight = 40,
        BilletLength = 50,
        FrictionFactor = m,
        ElementSize = 2,
        StrokeIncrement = 0.5,
        RamSpeed = 40,
    };

    private ForgingSolver Make(SimulationConfig cfg) => new(cfg, s => output.WriteLine(s));

    [Fact]
    public void FrictionlessAxisymmetricUpsetMatchesHomogeneousSolution()
    {
        var cfg = Upset(Symmetry.Axisymmetric, 0);
        var solver = Make(cfg);
        var s1 = solver.Advance(CancellationToken.None);
        double rate = 40.0 / 40.0;
        double expected = 100 * Math.Pow(rate, 0.1) * Math.PI * 20 * 20;
        Assert.InRange(s1.LoadNewton / expected, 0.995, 1.005);
    }

    [Fact]
    public void FrictionlessPlaneStrainUpsetMatchesHomogeneousSolution()
    {
        var cfg = Upset(Symmetry.PlaneStrain, 0);
        var solver = Make(cfg);
        var s1 = solver.Advance(CancellationToken.None);
        double rateBar = 2 / Math.Sqrt(3) * 40.0 / 40.0;
        double expected = 2 / Math.Sqrt(3) * 100 * Math.Pow(rateBar, 0.1) * (2 * 20) * 50;
        Assert.InRange(s1.LoadNewton / expected, 0.995, 1.005);
    }

    [Fact]
    public void FrictionRaisesLoadWithinUpperBoundEstimate()
    {
        var solver = Make(Upset(Symmetry.Axisymmetric, 0.5));
        var s1 = solver.Advance(CancellationToken.None);
        double sig = 100 * Math.Pow(1.0, 0.1);
        double frictionless = sig * Math.PI * 400;
        // Avitzur upper bound for a cylinder: p/σ̄ ≈ 1 + m·D/(3√3·H)
        double avitzur = frictionless * (1 + 0.5 * 40 / (3 * Math.Sqrt(3) * 40));
        output.WriteLine($"load {s1.LoadNewton:F0} N, frictionless {frictionless:F0} N, Avitzur {avitzur:F0} N");
        Assert.True(s1.LoadNewton > 1.01 * frictionless);
        Assert.True(s1.LoadNewton < 1.02 * avitzur);
    }

    [Fact]
    public void DamagePeaksAtTheBulgingFreeSurface()
    {
        // Upset with friction barrels: the free equator stretches in the hoop direction (tension), the core is
        // compressed. Cockcroft–Latham damage must be ≥ 0, grow with stroke, and peak at the outer surface.
        var solver = Make(Upset(Symmetry.Axisymmetric, 0.5));
        StepSnapshot s = solver.InitialSnapshot();
        while (!solver.IsFinished) s = solver.Advance(CancellationToken.None);
        var c = s.Mesh.Connectivity;
        double xMax = s.X.Max();
        int worst = 0;
        for (int e = 1; e < s.ElementCount; e++)
        {
            if (s.Damage[e] > s.Damage[worst]) worst = e;
        }

        double cx = 0.25 * (s.X[c[4 * worst]] + s.X[c[4 * worst + 1]] + s.X[c[4 * worst + 2]] + s.X[c[4 * worst + 3]]);
        output.WriteLine($"max damage {s.Damage[worst]:F3} at r = {cx:F2} mm (outer radius {xMax:F2} mm); min {s.Damage.Min():F4}");
        Assert.True(s.Damage.Min() >= 0);
        Assert.InRange(s.Damage[worst], 0.02, 2.0);
        Assert.True(cx > 0.75 * xMax, "damage should peak near the bulging free surface");
    }

    [Fact]
    public void FullUpsetConservesVolumeAndReachesShutHeight()
    {
        var solver = Make(Upset(Symmetry.Axisymmetric, 0.3));
        StepSnapshot last = solver.InitialSnapshot();
        while (!solver.IsFinished) last = solver.Advance(CancellationToken.None);
        Assert.Equal(10.0, last.Stroke, 6);
        Assert.InRange(Math.Abs(last.VolumeChangePercent), 0, 0.5);
        float maxY = last.Y.Max();
        Assert.Equal(30.0, maxY, 3);
    }

    [Theory]
    [Trait("Category", "Slow")]
    [MemberData(nameof(PresetNames))]
    public void EveryPresetForgesToDieClosure(string name)
    {
        var die = DiePresets.All.Single(d => d.Name == name);
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
        var log = new List<string>();
        var solver = new ForgingSolver(cfg, log.Add);
        StepSnapshot last = solver.InitialSnapshot();
        try
        {
            while (!solver.IsFinished) last = solver.Advance(CancellationToken.None);
        }
        finally
        {
            foreach (var l in log.Where(l => !l.StartsWith("    it", StringComparison.Ordinal))) output.WriteLine(l);
        }

        Assert.True(solver.IsFinished);
        Assert.InRange(Math.Abs(last.VolumeChangePercent), 0, 2.0);
        Assert.True(last.FillPercent > 90, $"fill {last.FillPercent:F1} %");
    }

    [Theory]
    [Trait("Category", "Slow")]
    [InlineData(10.0)]
    [InlineData(50.0)]
    [InlineData(1000.0)]
    public void Demo3FlangedShaftAluminiumForgesAtEveryRamSpeed(double ramSpeed)
    {
        // Demo 3 (ram speed sweep): 6061 at 450 °C in the flanged shaft, billet as wide as the shank pocket.
        var die = DiePresets.All.Single(d => d.Name == "Flanged shaft");
        var cfg = new SimulationConfig
        {
            Die = die,
            Material = MaterialPresets.Al6061,
            Temperature = 450,
            BilletRadius = die.RecommendedBilletRadius,
            BilletHeight = die.RecommendedHeight(die.RecommendedBilletRadius, die.RecommendedLength),
            BilletLength = die.RecommendedLength,
            FrictionFactor = 0.3,
            ElementSize = 1.25, // as in the demo setup
            StrokeIncrement = die.RecommendedStrokeIncrement,
            RamSpeed = ramSpeed,
        };
        var log = new List<string>();
        var solver = new ForgingSolver(cfg, log.Add);
        StepSnapshot last = solver.InitialSnapshot();
        try
        {
            while (!solver.IsFinished) last = solver.Advance(CancellationToken.None);
        }
        finally
        {
            foreach (var l in log.Where(l => !l.StartsWith("    it", StringComparison.Ordinal))) output.WriteLine(l);
        }

        Assert.True(last.FillPercent > 90, $"fill {last.FillPercent:F1} %");
    }

    public static TheoryData<string> PresetNames()
    {
        var data = new TheoryData<string>();
        foreach (var d in DiePresets.All) data.Add(d.Name);
        return data;
    }
}
