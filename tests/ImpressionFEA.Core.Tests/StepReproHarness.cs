using ImpressionFEA.Core.Cad;
using ImpressionFEA.Core.Materials;
using ImpressionFEA.Core.Simulation;
using ImpressionFEA.Core.Solver;
using ImpressionFEA.Core.ThreeD;
using Xunit.Abstractions;

namespace ImpressionFEA.Core.Tests;

public class StepReproHarness(ITestOutputHelper output)
{
    private static double Env(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : fallback;

    [Fact]
    [Trait("Category", "Scratch")]
    public void RunStepFileToClosure()
    {
        // Developer harness: set REPRO_STEP to a 3-body STEP file to run it to closure with the full solver log.
        var path = Environment.GetEnvironmentVariable("REPRO_STEP");
        if (string.IsNullOrEmpty(path)) return;
        var asm = ForgingAssembly.Classify(Path.GetFileName(path), StepImporter.Read(path));
        foreach (var b in asm.Bodies) output.WriteLine($"body {b.Index + 1}: V {b.Volume:F0} COG {b.CenterOfGravity.X:F2},{b.CenterOfGravity.Y:F2},{b.CenterOfGravity.Z:F2} box {b.Min.X:F1},{b.Min.Y:F1},{b.Min.Z:F1} .. {b.Max.X:F1},{b.Max.Y:F1},{b.Max.Z:F1}");
        var die = asm.BuildDieSet(asm.SuggestedTop, output.WriteLine);
        var cfg = new SimulationConfig3D
        {
            Die = die, Material = MaterialPresets.All[0], Temperature = MaterialPresets.All[0].DefaultTemperature,
            ElementSize = Env("REPRO_H", die.RecommendedElementSize), StrokeIncrement = Env("REPRO_DS", die.RecommendedStrokeIncrement), RamSpeed = 50,
            RefineThinFlash = Environment.GetEnvironmentVariable("REPRO_FLASH") == "1",
        };
        output.WriteLine(string.Join("\n", cfg.Validate()));
        using var solver = new Forging3DSolver(cfg, l => { if (!l.StartsWith("    it")) output.WriteLine(l); });
        ForgingSnapshot s = solver.Initial();
        try { while (!solver.IsFinished) s = solver.Step(CancellationToken.None); }
        catch (SimulationException ex) { output.WriteLine("FAILED: " + ex.Message); }
        output.WriteLine($"final stroke {s.Stroke:F2} / {solver.TotalStroke:F2}");
    }
}
