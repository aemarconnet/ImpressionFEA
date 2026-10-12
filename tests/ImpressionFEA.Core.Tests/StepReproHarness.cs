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

    [Fact]
    [Trait("Category", "Scratch")]
    public void RunSetupToClosure()
    {
        // Developer harness: set REPRO_SETUP to an *.ifea.json (3D, STEP-based, e.g. a SolidWorks job) to run it to
        // closure exactly as set up, with the full solver log.
        var path = Environment.GetEnvironmentVariable("REPRO_SETUP");
        if (string.IsNullOrEmpty(path)) return;
        var s0 = RunSettings.Load(path);
        string step = Path.IsPathRooted(s0.StepFile!) ? s0.StepFile! : Path.Combine(Path.GetDirectoryName(path)!, s0.StepFile!);
        var asm = ForgingAssembly.Classify(Path.GetFileName(step), StepImporter.Read(step));
        var top = s0.TopDieBody is int tb ? asm.Bodies.First(b => b.Index + 1 == tb) : asm.SuggestedTop;
        var die = asm.BuildDieSet(top, output.WriteLine, asm.IsDieOnly ? s0.Workpiece ?? new UserBillet() : null);
        die.CavityVolume();
        die.Place(0, 0);
        var mat = MaterialPresets.All.First(m => m.Name == s0.Material);
        var cfg = new SimulationConfig3D
        {
            Die = die, Material = mat, Temperature = s0.Temperature, FrictionFactor = s0.Friction,
            ElementSize = s0.ElementSize, StrokeIncrement = s0.StrokeIncrement, RamSpeed = s0.RamSpeed,
            AutoStep = s0.AutoStep, MaxCutbacks = s0.MaxCutbacks, RefinementLevels = s0.RefinementLevels,
            RefineThinFlash = s0.RefineThinFlash, RemeshQuality = s0.RemeshQuality, MaxNodeCount = s0.MaxNodeCount,
            MaxIterations = s0.MaxIterations, Tolerance = s0.Tolerance,
        };
        output.WriteLine(string.Join("\n", cfg.Validate()));
        using var solver = new Forging3DSolver(cfg, l => { if (!l.StartsWith("    it")) output.WriteLine(l); });
        ForgingSnapshot s = solver.Initial();
        try { while (!solver.IsFinished) s = solver.Step(CancellationToken.None); }
        catch (SimulationException ex) { output.WriteLine("FAILED: " + ex.Message); }
        output.WriteLine($"final stroke {s.Stroke:F2} / {solver.TotalStroke:F2}");
    }
}
