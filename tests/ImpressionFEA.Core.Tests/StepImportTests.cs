using ImpressionFEA.Core.Cad;
using ImpressionFEA.Core.Materials;
using ImpressionFEA.Core.Simulation;
using ImpressionFEA.Core.ThreeD;
using Xunit.Abstractions;

namespace ImpressionFEA.Core.Tests;

public class StepImportTests(ITestOutputHelper output)
{
    private static readonly MaterialPreset Viscoplastic =
        new("Test", "test", CRef: 100, N: 0, M: 0.1, Eps0: 1, TRef: 1000, Beta: 0, TMin: 0, TMax: 2000, DefaultTemperature: 1000);

    private static string Sample(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples"))) dir = dir.Parent;
        return dir is null ? "" : Path.Combine(dir.FullName, "samples", name);
    }

    private bool Available()
    {
        if (StepImporter.IsAvailable(out string why)) return true;
        output.WriteLine(why + " — skipped.");
        return false;
    }

    [Theory]
    [InlineData("sample_upset_3bodies.step", 31.2)]
    [InlineData("sample_gear_3bodies.step", -1)]
    [InlineData("sample_hexflange_3bodies.step", -1)]
    public void ClassifiesBodiesAndFindsClosure(string file, double expectedStroke)
    {
        if (!Available()) return;
        var bodies = StepImporter.Read(Sample(file));
        Assert.Equal(3, bodies.Count);
        var asm = ForgingAssembly.Classify(file, bodies);
        Assert.Equal(1, asm.Workpiece!.Index); // samples are written bottom, billet, top
        Assert.Equal(2, asm.SuggestedTop.Index);
        var die = asm.BuildDieSet(asm.SuggestedTop, output.WriteLine);
        foreach (var b in bodies) output.WriteLine($"body {b.Index + 1}: V {b.Volume:F0} mm³ (mesh {b.Mesh.Volume:F0}), COG {b.CenterOfGravity}");
        Assert.InRange(die.ImportedBillet!.Volume / asm.Workpiece.Volume, 0.995, 1.005);
        var (seat, gap) = die.Place(0, 0);
        output.WriteLine($"seat {seat:F3} mm, stroke {gap:F3} mm, cavity {die.CavityVolume():F0} mm³");
        Assert.InRange(Math.Abs(seat), 0, 0.05); // samples rest on the bottom die
        if (expectedStroke > 0) Assert.InRange(gap, expectedStroke - 0.05, expectedStroke + 0.05);
        Assert.True(gap > 1);
    }

    [Theory]
    [InlineData(UserBilletShape.VerticalBar, 0, 0, 0)]
    [InlineData(UserBilletShape.HorizontalBar, 30, 6, -4)]
    [InlineData(UserBilletShape.Block, 15, 0, 0)]
    public void DieOnlyFileTakesAUserWorkpiece(UserBilletShape shape, double angle, double dx, double dy)
    {
        if (!Available()) return;
        var asm = ForgingAssembly.Classify("gear dies", StepImporter.Read(Sample("sample_gear_dies_only.step")));
        Assert.True(asm.IsDieOnly);
        Assert.Throws<ArgumentException>(() => asm.BuildDieSet(asm.SuggestedTop));
        var billet = new UserBillet { Shape = shape, Diameter = 56, Length = 81, Width = 60, Depth = 60, Height = 60, AngleDeg = angle, OffsetX = dx, OffsetY = dy };
        var die = asm.BuildDieSet(asm.SuggestedTop, output.WriteLine, billet);
        Assert.NotNull(die.UserBillet);
        Assert.InRange(die.ImportedBillet!.Volume / billet.Volume, 0.99, 1.0001); // faceted cylinder ≤ exact
        var (seat, gap) = die.Place(0, 0);
        output.WriteLine($"{shape}: seat {seat:F3} mm, stroke {gap:F3} mm, cavity {die.CavityVolume() / 1000:F1} cm³, billet {billet.Volume / 1000:F1} cm³");
        Assert.True(gap > 1, "stroke");
        // The workpiece sits on the bottom die after seating: its lowest point is no longer above the start height.
        Assert.True(seat < 0);

        // Changing the workpiece re-seats it.
        die.SetUserBillet(billet.WithVolume(1.1 * die.CavityVolume()));
        Assert.InRange(die.ImportedBillet!.Volume / (1.1 * die.CavityVolume()), 0.99, 1.0001);
        Assert.True(die.Place(0, 0).Gap > 1);
    }

    [Fact]
    public void DieOnlyGearForgesWithUserBar()
    {
        if (!Available()) return;
        var asm = ForgingAssembly.Classify("gear dies", StepImporter.Read(Sample("sample_gear_dies_only.step")));
        var die = asm.BuildDieSet(asm.SuggestedTop, output.WriteLine, new UserBillet { Diameter = 56, Length = 81 });
        var cfg = new SimulationConfig3D { Die = die, Material = Viscoplastic, Temperature = 1000, ElementSize = 4, StrokeIncrement = 1.2, RamSpeed = 40 };
        Assert.Empty(cfg.Validate());
        using var solver = new Forging3DSolver(cfg, output.WriteLine);
        ForgingSnapshot s = solver.Initial();
        for (int i = 0; i < 3; i++) s = solver.Step(CancellationToken.None);
        Assert.True(s.LoadNewton > 0);
    }

    [Fact]
    public void ImportedUpsetForgesToClosure()
    {
        if (!Available()) return;
        var asm = ForgingAssembly.Classify("upset", StepImporter.Read(Sample("sample_upset_3bodies.step")));
        var die = asm.BuildDieSet(asm.SuggestedTop, output.WriteLine);
        var cfg = new SimulationConfig3D
        {
            Die = die,
            Material = Viscoplastic,
            Temperature = 1000,
            FrictionFactor = 0.3,
            ElementSize = 4,
            StrokeIncrement = 1.2,
            RamSpeed = 40,
        };
        Assert.Empty(cfg.Validate());
        using var solver = new Forging3DSolver(cfg, output.WriteLine);
        ForgingSnapshot s = solver.Initial();
        int steps = 0;
        while (!solver.IsFinished && steps++ < 4) s = solver.Step(CancellationToken.None);
        output.WriteLine($"stroke {s.Stroke:F2} mm, load {s.LoadNewton / 9806.65:F1} t, ΔV {s.VolumeChangePercent:F2} %");
        Assert.True(s.LoadNewton > 0);
        Assert.InRange(s.VolumeChangePercent, -3, 3);
    }
}
