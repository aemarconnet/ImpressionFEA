using System.Diagnostics;
using System.Globalization;
using ImpressionFEA.Core.Materials;
using ImpressionFEA.Core.Simulation;
using ImpressionFEA.Core.ThreeD;
using Xunit.Abstractions;

namespace ImpressionFEA.Core.Tests;

public class MeshConvergenceTests(ITestOutputHelper output)
{
    private static ConvergenceLevel L(double h, double load) => new(h, load, load, 100, 0, 1000, TimeSpan.Zero, "completed");

    [Fact]
    public void RichardsonRecoversAFirstOrderLimit()
    {
        // L(h) = 100 − 20 h, sizes 2, 1.6, 1.28: limit 100, order 1, the coarsest 40 % low.
        var r = MeshConvergence.Evaluate([L(2, 60), L(1.6, 68), L(1.28, 74.4)]);
        Assert.Equal(ConvergenceKind.NotConverged, r.Kind);
        Assert.Equal(100, r.Estimate!.Value, 6);
        Assert.Equal(1, r.Order!.Value, 6);
        Assert.Equal(-40, r.ErrorOfRunPercent!.Value, 6);
        Assert.Equal(25.6, r.UncertaintyPercent, 6); // the extrapolation distance
        Assert.StartsWith("Not converged", r.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void ErraticLevelsGiveTheFinestValueWithAnUncertainty()
    {
        var r = MeshConvergence.Evaluate([L(2, 100), L(1.6, 110), L(1.28, 104)]);
        Assert.Equal(ConvergenceKind.Uncertain, r.Kind);
        Assert.Equal(104, r.Estimate!.Value, 6);
        Assert.Null(r.ErrorOfRunPercent);
        Assert.Contains("104.0 t", r.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void FinerLevelsAgreeingWithinTheNoiseSettleTheLoad()
    {
        // Hex bolt head, 1.6 mm flash (Balanced): the two finer meshes agree within 0.7 %, so the load has settled at
        // about the finest value and the run's mesh is about 6 % low (no meaningful order from noise-level differences).
        var r = MeshConvergence.Evaluate([L(1.6, 104.9), L(1.28, 110.5), L(1.02, 111.3)]);
        Assert.Equal(ConvergenceKind.NotConverged, r.Kind);
        Assert.Equal(111.3, r.Estimate!.Value, 6);
        Assert.Null(r.Order);
        Assert.Equal(-5.75, r.ErrorOfRunPercent!.Value, 2);
        Assert.Equal(MeshConvergence.NoisePercent, r.UncertaintyPercent, 6);
    }

    [Fact]
    public void ALoadThatKeepsRisingFasterIsStillChanging()
    {
        // Hex bolt head, 0.8 mm flash (Balanced): each refinement raises the closing load more than the one before.
        var r = MeshConvergence.Evaluate([L(1.6, 75.8), L(1.28, 97.1), L(1.02, 131.1)]);
        Assert.Equal(ConvergenceKind.StillChanging, r.Kind);
        Assert.True(r.NeedsFinerMesh);
        Assert.True(r.Rising);
        Assert.Equal(131.1, r.Estimate!.Value, 6);
        Assert.Equal(-42.2, r.ErrorOfRunPercent!.Value, 1);
        Assert.Contains("42 % or more low", r.Verdict, StringComparison.Ordinal);
        Assert.Contains("likely above 131.1 t", r.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void EqualStepsAreAModerateBoundNotAnAlarm()
    {
        // Hex bolt head, 1.6 mm flash, from the app (ram speed 2 in/s): about 4 % per refinement; the steps do not shrink
        // within the noise, so the finest value is a bound, and the advice is the Accurate mesh, not "much finer".
        var r = MeshConvergence.Evaluate([L(1.6, 104.7), L(1.28, 108.5), L(1.02, 112.7)]);
        Assert.Equal(ConvergenceKind.StillChanging, r.Kind);
        Assert.Equal(-7.1, r.ErrorOfRunPercent!.Value, 1);
        Assert.Contains("7 % or more low", r.Verdict, StringComparison.Ordinal);
        Assert.Contains("Accurate", r.Verdict, StringComparison.Ordinal);
        Assert.DoesNotContain("much finer", r.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunWithinThreePercentIsConverged()
    {
        var r = MeshConvergence.Evaluate([L(2, 99), L(1.6, 100.6), L(1.28, 100.2)]);
        Assert.Equal(ConvergenceKind.Converged, r.Kind);
        Assert.StartsWith("Converged", r.Verdict, StringComparison.Ordinal);
        Assert.Contains("1.28 mm: 100.2 t", r.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void ALevelThatDidNotCloseGivesNoEstimate()
    {
        var r = MeshConvergence.Evaluate([L(2, 99), L(1.6, 100.6), new ConvergenceLevel(1.28, null, 90, 98, 0, 1000, TimeSpan.Zero, "failed: x")]);
        Assert.Equal(ConvergenceKind.NoEstimate, r.Kind);
        Assert.Null(r.Estimate);
    }

    /// <summary>Study: Balanced hex bolt head to a 2 mm gap, then the check (IFEA_STUDY; progress in IFEA_STUDY_DIR).</summary>
    [Theory]
    [Trait("Category", "Study")]
    [InlineData(1.6)]
    [InlineData(0.8)]
    public void HexBoltHeadConvergenceCheck(double flash)
    {
        if (Environment.GetEnvironmentVariable("IFEA_STUDY") is null || Environment.GetEnvironmentVariable("IFEA_STUDY_DIR") is not { } dir) return;
        if (Environment.GetEnvironmentVariable("IFEA_STUDY_ONLY") is { } only && only != flash.ToString(CultureInfo.InvariantCulture)) return;
        string progress = Path.Combine(dir, $"progress-convergence-{flash.ToString(CultureInfo.InvariantCulture)}.txt");
        void Log(string s) => File.AppendAllText(progress, $"{DateTime.Now:HH:mm:ss} {s}{Environment.NewLine}");
        var die = DiePresets3D.HexBoltHead(flashThickness: flash);
        var (r0, dim) = die.RecommendedBillet();
        var cfg0 = new SimulationConfig3D { Die = die, Material = MaterialPresets.Steel4140, Temperature = 1150, BilletRadius = r0, BilletDimension = dim, FrictionFactor = 0.3, RamSpeed = 50 };
        double h = cfg0.CalibratedAutoElementSize();
        var cfg = cfg0 with { ElementSize = h, StrokeIncrement = SimulationConfig3D.AutoStrokeIncrement(h) };
        var sw = Stopwatch.StartNew();
        Forging3DCheckpoint? cp = null;
        var history = new List<(double, double)>();
        using (var solver = new Forging3DSolver(cfg, _ => { }))
        {
            solver.Initial();
            while (!solver.IsFinished)
            {
                var s = solver.Step(CancellationToken.None);
                history.Add((s.DieGap, s.LoadTonnes));
                if (cp is null && s.DieGap <= MeshConvergence.CheckpointGap) cp = solver.Checkpoint();
                if (s.Step % 10 == 0) Log($"run: step {s.Step}, gap {s.DieGap:F2} mm, load {s.LoadTonnes:F1} t, {sw.Elapsed.TotalMinutes:F1} min");
            }
        }

        double runMinutes = sw.Elapsed.TotalMinutes;
        Log($"run done: closing load {LoadMetrics.ClosingLoad(history):F1} t in {runMinutes:F1} min");
        sw.Restart();
        var result = MeshConvergence.Run(cfg, cp!, Log);
        foreach (var l in result.Levels) output.WriteLine($"CONV | flash {flash} | h {l.H:F2} | closing {l.ClosingLoad:F1} t | peak {l.PeakLoad:F1} t | fill {l.FillPercent:F1} % | ΔV {l.VolumeChangePercent:F2} % | {l.Nodes} nodes | {l.Elapsed.TotalMinutes:F1} min | {l.Status}");
        output.WriteLine($"CONV | flash {flash} | run {runMinutes:F1} min, check {sw.Elapsed.TotalMinutes:F1} min | {result.Kind}, estimate {result.Estimate:F1} t ±{result.UncertaintyPercent:F0} %, order {result.Order:F2} | {result.Verdict}");
        Log("check done: " + result.Verdict);
    }
}
