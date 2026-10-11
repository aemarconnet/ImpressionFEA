using ImpressionFEA.Core.Geometry;
using ImpressionFEA.Core.Materials;
using ImpressionFEA.Core.Simulation;
using ImpressionFEA.Core.Solver;
using ImpressionFEA.Core.Solver.Linear;
using ImpressionFEA.Core.ThreeD;
using Xunit.Abstractions;

namespace ImpressionFEA.Core.Tests;

public class Solver3DTests(ITestOutputHelper output)
{
    private static readonly MaterialPreset Viscoplastic =
        new("Test", "test", CRef: 100, N: 0, M: 0.1, Eps0: 1, TRef: 1000, Beta: 0, TMin: 0, TMax: 2000, DefaultTemperature: 1000);

    private static SimulationConfig3D Upset(double m, LinearBackend backend = LinearBackend.Auto) => new()
    {
        Die = DiePresets3D.SimpleUpsetBlock(),
        Material = Viscoplastic,
        Temperature = 1000,
        BilletRadius = 20,
        BilletDimension = 40,
        FrictionFactor = m,
        ElementSize = 3,
        StrokeIncrement = 0.5,
        RamSpeed = 40,
        LinearBackend = backend,
    };

    [Fact]
    public void FrictionlessUpsetMatchesHomogeneousSolution()
    {
        using var solver = new Forging3DSolver(Upset(0), output.WriteLine);
        var s0 = (Snapshot3D)solver.Initial();
        var s1 = solver.Step(CancellationToken.None);
        // Homogeneous compression: F = σ̄(ε̇)·A with A = (mesh volume)/H (stuffed cylinder, slightly faceted).
        double volume = 0;
        var t = s0.Mesh.Tets;
        for (int e = 0; e < s0.Mesh.ElementCount; e++)
        {
            Vec3 P(int n) => new(s0.X[n], s0.Y[n], s0.Z[n]);
            volume += TetMesh.Volume(P(t[4 * e]), P(t[4 * e + 1]), P(t[4 * e + 2]), P(t[4 * e + 3]));
        }

        double area = 4 * volume / 40;
        double expected = 100 * Math.Pow(40.0 / 40.0, 0.1) * area;
        output.WriteLine($"load {s1.LoadNewton:F0} N, expected {expected:F0} N");
        Assert.InRange(s1.LoadNewton / expected, 0.99, 1.01);
    }

    [Theory]
    [InlineData(LinearBackend.MklCpu)]
    [InlineData(LinearBackend.CudaGpu)]
    public void NativeBackendsReproduceManagedSolution(LinearBackend backend)
    {
        if (!SpdSolverFactory.IsAvailable(backend))
        {
            output.WriteLine($"{backend} not available on this machine — skipped.");
            return;
        }

        double Load(LinearBackend b)
        {
            using var solver = new Forging3DSolver(Upset(0.3, b), output.WriteLine);
            solver.Initial();
            solver.Step(CancellationToken.None);
            return solver.Step(CancellationToken.None).LoadNewton;
        }

        double managed = Load(LinearBackend.Managed), native = Load(backend);
        Assert.InRange(native / managed, 1 - 1e-6, 1 + 1e-6);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void FullUpsetAgreesWithAxisymmetricSolver()
    {
        var die2 = DiePresets.SimpleUpsetBlock();
        var cfg2 = new SimulationConfig
        {
            Die = die2, Material = MaterialPresets.Steel4140, Temperature = 1150, BilletRadius = 20, BilletHeight = 51.2,
            FrictionFactor = 0.3, ElementSize = 1.25, StrokeIncrement = 0.25, RamSpeed = 50,
        };
        var s2 = new ForgingSolver(cfg2);
        double peak2 = 0;
        while (!s2.IsFinished) peak2 = Math.Max(peak2, s2.Advance(CancellationToken.None).LoadNewton);

        var cfg3 = new SimulationConfig3D
        {
            Die = DiePresets3D.SimpleUpsetBlock(), Material = MaterialPresets.Steel4140, Temperature = 1150, BilletRadius = 20,
            BilletDimension = 51.2, FrictionFactor = 0.3, ElementSize = 3, StrokeIncrement = 0.5, RamSpeed = 50,
        };
        using var s3 = new Forging3DSolver(cfg3);
        double peak3 = 0;
        ForgingSnapshot last = s3.Initial();
        while (!s3.IsFinished)
        {
            last = s3.Step(CancellationToken.None);
            peak3 = Math.Max(peak3, last.LoadNewton);
        }

        output.WriteLine($"peak 2D {peak2 / 9806.65:F2} t, 3D {peak3 / 9806.65:F2} t, 3D volume change {last.VolumeChangePercent:F2} %");
        Assert.InRange(peak3 / peak2, 0.93, 1.07);
        Assert.InRange(Math.Abs(last.VolumeChangePercent), 0, 1.0);
    }
}
