using ImpressionFEA.Core.Cad;
using ImpressionFEA.Core.Materials;
using ImpressionFEA.Core.Simulation;
using ImpressionFEA.Core.ThreeD;
using Xunit.Abstractions;

namespace ImpressionFEA.Core.Tests;

/// <summary>Automatic element size (target node count) and the run ETA.</summary>
public class AutoMeshTests(ITestOutputHelper output)
{
    private static readonly MaterialPreset Viscoplastic =
        new("Test", "test", CRef: 100, N: 0, M: 0.1, Eps0: 1, TRef: 1000, Beta: 0, TMin: 0, TMax: 2000, DefaultTemperature: 1000);

    private static bool IsMeshError(string e) => e.Contains("Element size", StringComparison.Ordinal) || e.Contains("mesh too fine", StringComparison.Ordinal);

    private static string Sample(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples"))) dir = dir.Parent;
        return dir is null ? "" : Path.Combine(dir.FullName, "samples", name);
    }

    private SimulationConfig3D Auto(SimulationConfig3D cfg, int target = SimulationConfig3D.DefaultTargetNodes)
    {
        double h = cfg.CalibratedAutoElementSize(target);
        var c = cfg with { ElementSize = h, StrokeIncrement = SimulationConfig3D.AutoStrokeIncrement(h) };
        output.WriteLine($"{cfg.Die.Name}: h {h:F3} mm (range {cfg.FinestElementSize:F3}–{cfg.CoarsestElementSize:F3}), ≈{c.EstimatedNodes:N0} nodes, initial mesh {c.InitialNodeCount(h):N0} nodes");
        return c;
    }

    [Fact]
    public void PresetsAutoSizeHitsTheTargetNodeCount()
    {
        foreach (var die in DiePresets3D.All)
        {
            var (r, dim) = die.RecommendedBillet();
            var cfg = Auto(new SimulationConfig3D { Die = die, Material = Viscoplastic, Temperature = 1000, BilletRadius = r, BilletDimension = dim, ElementSize = 1, StrokeIncrement = 0.1 });
            Assert.DoesNotContain(cfg.Validate(), IsMeshError);
            bool capped = cfg.ElementSize >= cfg.CoarsestElementSize - 0.06;
            if (!capped) Assert.InRange(cfg.InitialNodeCount(cfg.ElementSize)!.Value, 0.85 * SimulationConfig3D.DefaultTargetNodes, 1.15 * SimulationConfig3D.DefaultTargetNodes);
            Assert.True(cfg.StrokeIncrement <= 0.4 * cfg.ElementSize);
        }
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(8000)]
    [InlineData(20000)]
    public void AutoSizeFollowsTheTarget(int target)
    {
        var die = DiePresets3D.All[0];
        var (r, dim) = die.RecommendedBillet();
        var cfg = Auto(new SimulationConfig3D { Die = die, Material = Viscoplastic, Temperature = 1000, BilletRadius = r, BilletDimension = dim, MaxNodeCount = 60000 }, target);
        if (cfg.ElementSize < cfg.CoarsestElementSize - 0.06) Assert.InRange(cfg.InitialNodeCount(cfg.ElementSize)!.Value, 0.85 * target, 1.15 * target);
    }

    [Fact]
    public void AutoSizeNeverExceedsTheNodeBudget()
    {
        var die = DiePresets3D.All[0];
        var (r, dim) = die.RecommendedBillet();
        var cfg = Auto(new SimulationConfig3D { Die = die, Material = Viscoplastic, Temperature = 1000, BilletRadius = r, BilletDimension = dim, MaxNodeCount = 3000 }, 20000);
        Assert.True(cfg.EstimatedNodes <= 3000);
    }

    /// <summary>A user workpiece from a few times smaller to a few times larger than the cavity stays meshable (and visible).</summary>
    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public void DieOnlyWorkpieceOfAnySizeGetsAValidMesh(double scale)
    {
        if (!StepImporter.IsAvailable(out string why))
        {
            output.WriteLine(why + " — skipped.");
            return;
        }

        var asm = ForgingAssembly.Classify("gear dies", StepImporter.Read(Sample("sample_gear_dies_only.step")));
        var die = asm.BuildDieSet(asm.SuggestedTop, output.WriteLine, new UserBillet { Diameter = 56, Length = 81 });
        die.SetUserBillet(new UserBillet { Diameter = 56 * Math.Cbrt(scale), Length = 81 }.WithVolume(scale * 1.1 * die.CavityVolume()));
        var cfg = Auto(new SimulationConfig3D { Die = die, Material = Viscoplastic, Temperature = 1000, RamSpeed = 40 });
        Assert.DoesNotContain(cfg.Validate(), IsMeshError);
        if (cfg.Validate().Count == 0)
        {
            using var solver = new Forging3DSolver(cfg, output.WriteLine);
            var s = solver.Initial();
            output.WriteLine($"initial mesh: {s.NodeCount:N0} nodes");
            Assert.InRange(s.NodeCount, 0.85 * SimulationConfig3D.DefaultTargetNodes, 1.15 * SimulationConfig3D.DefaultTargetNodes);
        }
    }

    [Fact]
    public void EtaIsExactForAConstantRate()
    {
        var eta = new EtaEstimator();
        Assert.Null(eta.RemainingSeconds);
        for (int i = 1; i <= 20; i++) eta.Add(i * 2.0, i / 100.0); // 2 s per 1 %
        Assert.Equal(160, eta.RemainingSeconds!.Value, 6);
        Assert.Equal(150, eta.RemainingAt(50)!.Value, 6);
        Assert.Equal(0, eta.RemainingAt(1000)!.Value);
    }

    [Fact]
    public void EtaFollowsASlowdown()
    {
        var eta = new EtaEstimator();
        for (int i = 1; i <= 40; i++) eta.Add(i, i / 100.0); // 1 s per 1 %
        double before = eta.RemainingSeconds!.Value;
        double t = 40;
        for (int i = 41; i <= 50; i++) eta.Add(t += 3, i / 100.0); // now 3 s per 1 %
        double after = eta.RemainingSeconds!.Value;
        output.WriteLine($"remaining before slowdown {before:F0} s, after {after:F0} s");
        Assert.InRange(after, 0.7 * 3 * 50, 3 * 50); // mostly the recent rate
    }

    [Fact]
    public void EtaWaitsForEnoughData()
    {
        var eta = new EtaEstimator();
        eta.Add(1, 0.01);
        eta.Add(2, 0.02);
        eta.Add(3, 0.025);
        Assert.Null(eta.RemainingSeconds); // under 3 %
        eta.Add(4, 0.04);
        Assert.NotNull(eta.RemainingSeconds);
    }
}
