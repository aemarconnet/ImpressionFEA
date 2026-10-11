using ImpressionFEA.Core.Geometry;
using ImpressionFEA.Core.Materials;
using ImpressionFEA.Core.Simulation;
using ImpressionFEA.Core.Solver;
using ImpressionFEA.Core.ThreeD;
using Xunit.Abstractions;

namespace ImpressionFEA.Core.Tests;

/// <summary>The exported deformed part must be a closed, outward-oriented surface enclosing the workpiece volume.</summary>
public class SurfaceExportTests(ITestOutputHelper output)
{
    private static SimulationConfig Config2D(DieSet die) => new()
    {
        Die = die,
        Material = MaterialPresets.Steel4140,
        Temperature = 1150,
        BilletRadius = die.RecommendedBilletRadius,
        BilletHeight = Math.Round(die.RecommendedHeight(die.RecommendedBilletRadius, die.RecommendedLength), 2),
        BilletLength = die.RecommendedLength,
        ElementSize = die.RecommendedElementSize,
        StrokeIncrement = die.RecommendedStrokeIncrement,
        RamSpeed = 50,
    };

    private static double SectionArea(StepSnapshot s)
    {
        double a = 0;
        var c = s.Mesh.Connectivity;
        for (int e = 0; e < s.Mesh.ElementCount; e++)
        {
            int p = c[4 * e], q = c[4 * e + 1], r = c[4 * e + 2], t = c[4 * e + 3];
            a += 0.5 * Math.Abs((s.X[r] - s.X[p]) * (s.Y[t] - s.Y[q]) - (s.X[t] - s.X[q]) * (s.Y[r] - s.Y[p]));
        }

        return a;
    }

    [Fact]
    public void AxisymmetricSectionRevolvesToClosedSolid()
    {
        var die = DiePresets.All.First(d => !d.IsPlaneStrain);
        var s = new ForgingSolver(Config2D(die)).InitialSnapshot();
        var mesh = DeformedSurface.Build(s, ResultField.VonMisesStress, segments: 192);
        // Pappus: V = 2π · ∫ r dA over the meridian section.
        double v = 0;
        var c = s.Mesh.Connectivity;
        for (int e = 0; e < s.Mesh.ElementCount; e++)
        {
            int p = c[4 * e], q = c[4 * e + 1], r = c[4 * e + 2], t = c[4 * e + 3];
            double area = 0.5 * Math.Abs((s.X[r] - s.X[p]) * (s.Y[t] - s.Y[q]) - (s.X[t] - s.X[q]) * (s.Y[r] - s.Y[p]));
            v += area * 0.25 * (s.X[p] + s.X[q] + s.X[r] + s.X[t]);
        }

        v *= 2 * Math.PI;
        double got = DeformedSurface.Volume(mesh);
        output.WriteLine($"{die.Name}: {mesh.TriangleCount} triangles, volume {got:F0} vs Pappus {v:F0} mm³");
        Assert.InRange(got / v, 0.995, 1.005);
    }

    [Fact]
    public void PlaneStrainSectionExtrudesToClosedSolid()
    {
        var die = DiePresets.All.First(d => d.IsPlaneStrain);
        var s = new ForgingSolver(Config2D(die)).InitialSnapshot();
        var mesh = DeformedSurface.Build(s, ResultField.EffectiveStrain, planeStrain: true, partLength: 80);
        double expected = 2 * SectionArea(s) * 80; // half section mirrored
        double got = DeformedSurface.Volume(mesh);
        output.WriteLine($"{die.Name}: {mesh.TriangleCount} triangles, volume {got:F0} vs {expected:F0} mm³");
        Assert.InRange(got / expected, 0.999, 1.001);
    }

    [Theory]
    [InlineData(0)] // quarter model (upset block)
    [InlineData(1)] // gear blank, quarter
    public void Symmetric3DModelMirrorsToFullClosedSolid(int preset)
    {
        var die = new[] { DiePresets3D.SimpleUpsetBlock(), DiePresets3D.GearBlank() }[preset];
        var (r, dim) = die.RecommendedBillet();
        using var solver = new Forging3DSolver(new SimulationConfig3D
        {
            Die = die,
            Material = MaterialPresets.Steel4140,
            Temperature = 1150,
            BilletRadius = r,
            BilletDimension = dim,
            ElementSize = die.RecommendedElementSize,
            StrokeIncrement = die.RecommendedStrokeIncrement,
        });
        var s = (Snapshot3D)solver.Initial();
        double modelled = 0;
        var t = s.Mesh.Tets;
        for (int e = 0; e < s.Mesh.ElementCount; e++)
        {
            Vec3 P(int k) => new(s.X[t[4 * e + k]], s.Y[t[4 * e + k]], s.Z[t[4 * e + k]]);
            modelled += TetMesh.Volume(P(0), P(1), P(2), P(3));
        }

        var mesh = DeformedSurface.Build(s, ResultField.VonMisesStress, die.Symmetry);
        double got = DeformedSurface.Volume(mesh), expected = modelled * die.SymmetryFactor;
        output.WriteLine($"{die.Name}: {mesh.TriangleCount} triangles, volume {got:F0} vs {expected:F0} mm³");
        Assert.InRange(got / expected, 0.9999, 1.0001);
    }

    [Fact]
    public void StlAndPlyWritersProduceExpectedSizes()
    {
        var mesh = new SurfaceMesh
        {
            Vertices = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)],
            Triangles = [0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3],
            Values = [0, 1, 2, 3],
        };
        Assert.Equal(1.0 / 6, DeformedSurface.Volume(mesh), 12);
        string stl = Path.GetTempFileName(), ply = Path.GetTempFileName();
        try
        {
            DeformedSurface.WriteStl(stl, mesh);
            Assert.Equal(84 + 4 * 50, new FileInfo(stl).Length);
            DeformedSurface.WritePly(ply, mesh, v => ((byte)(v * 50), 0, 0));
            var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(ply));
            Assert.StartsWith("ply\nformat binary_little_endian 1.0", text);
            Assert.Contains("element vertex 4", text);
            Assert.Contains("element face 4", text);
        }
        finally
        {
            File.Delete(stl);
            File.Delete(ply);
        }
    }
}
