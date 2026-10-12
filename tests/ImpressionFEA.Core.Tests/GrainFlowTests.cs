using ImpressionFEA.Core.Geometry;
using ImpressionFEA.Core.Materials;
using ImpressionFEA.Core.Mesh;
using ImpressionFEA.Core.Simulation;
using ImpressionFEA.Core.Solver;

namespace ImpressionFEA.Core.Tests;

public class GrainFlowTests
{
    private static List<ForgingSnapshot> Run(SimulationConfig cfg)
    {
        var solver = new ForgingSolver(cfg);
        var snaps = new List<ForgingSnapshot> { solver.InitialSnapshot() };
        while (!solver.IsFinished) snaps.Add(solver.Advance(CancellationToken.None));
        return snaps;
    }

    [Fact]
    public void FrictionlessUpsetKeepsFibresStraightAndSpreadsThemByVolumeConstancy()
    {
        // Homogeneous upsetting: a vertical material line at radius r0 stays vertical at r0·√(h0/h).
        var cfg = new SimulationConfig
        {
            Die = new DieSet
            {
                Name = "flat", Description = "flat", Symmetry = Symmetry.Axisymmetric,
                Bottom = new DieSurface(DieSide.Bottom, [new Vec2(0, 0), new Vec2(200, 0)]),
                TopClosed = new DieSurface(DieSide.Top, [new Vec2(0, 30), new Vec2(200, 30)]),
                CavityOuterRadius = 60, ViewExtent = 60, RecommendedBilletRadius = 20, RecommendedElementSize = 2, RecommendedStrokeIncrement = 0.5,
            },
            Material = new MaterialPreset("Test", "test", CRef: 100, N: 0, M: 0.1, Eps0: 1, TRef: 1000, Beta: 0, TMin: 0, TMax: 2000, DefaultTemperature: 1000),
            Temperature = 1000, BilletRadius = 20, BilletHeight = 40, BilletLength = 50, FrictionFactor = 0,
            ElementSize = 2, StrokeIncrement = 0.5, RamSpeed = 40,
        };
        var snaps = Run(cfg);
        var tracer = new GrainFlowTracer(FiberDirection.Vertical, 8);
        var start = tracer.LinesAt(snaps, 0)!;
        var end = tracer.LinesAt(snaps, snaps.Count - 1)!;
        Assert.Equal(8, start.Count);
        Assert.Equal(8, end.Count);
        double h0 = 40, h = 30;
        for (int l = 0; l < 8; l++)
        {
            double expected = start[l][0].X * Math.Sqrt(h0 / h);
            foreach (var p in end[l]) Assert.InRange(p.X, expected - 0.05, expected + 0.05);
            Assert.InRange(end[l].Min(p => p.Y), -0.01, 0.01);
            Assert.InRange(end[l].Max(p => p.Y), h - 0.01, h + 0.01);
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void FlowLinesStayInsideThePartThroughRemeshing()
    {
        var die = DiePresets.All.Single(d => d.Name == "Flanged shaft");
        var cfg = new SimulationConfig
        {
            Die = die, Material = MaterialPresets.Steel4140, Temperature = 1150,
            BilletRadius = die.RecommendedBilletRadius,
            BilletHeight = die.RecommendedHeight(die.RecommendedBilletRadius, die.RecommendedLength),
            BilletLength = die.RecommendedLength, FrictionFactor = 0.3,
            ElementSize = die.RecommendedElementSize, StrokeIncrement = die.RecommendedStrokeIncrement, RamSpeed = 50,
        };
        var snaps = Run(cfg);
        int remeshed = 0;
        for (int k = 1; k < snaps.Count; k++)
        {
            var s = (StepSnapshot)snaps[k];
            if (ReferenceEquals(s.Mesh, ((StepSnapshot)snaps[k - 1]).Mesh)) continue;
            remeshed++;
            // The saved velocity survives a remesh (it moved the material during the increment).
            Assert.Contains(s.VelocityY, v => Math.Abs(v) > 1);
        }

        Assert.True(remeshed > 5, $"only {remeshed} remeshes");

        var tracer = new GrainFlowTracer(FiberDirection.Vertical, 16);
        for (int k = 0; k < snaps.Count; k += 10) tracer.LinesAt(snaps, k); // incremental use, as in a live run
        int last = snaps.Count - 1;
        var lines = tracer.LinesAt(snaps, last)!;
        var fin = (StepSnapshot)snaps[last];
        var outline = fin.Mesh.BoundaryLoop.Select(n => new Vec2(fin.X[n], fin.Y[n])).ToArray();
        Assert.Equal(16, lines.Count);
        foreach (var p in lines.SelectMany(l => l))
        {
            Assert.False(double.IsNaN(p.X) || double.IsNaN(p.Y));
            Assert.True(TriangleMesher.Inside(outline, p) || TriangleMesher.DistanceToPolygon(outline, p) < 1e-3, $"({p.X:F3}, {p.Y:F3}) outside the part");
        }

        // Material flowed into the flange: some line reaches well beyond the shank radius.
        Assert.True(lines.SelectMany(l => l).Max(p => p.X) > 2 * die.RecommendedBilletRadius);
    }
}
