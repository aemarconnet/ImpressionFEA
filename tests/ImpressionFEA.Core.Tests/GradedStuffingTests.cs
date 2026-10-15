using ImpressionFEA.Core.ThreeD;
using Xunit.Abstractions;

namespace ImpressionFEA.Core.Tests;

/// <summary>Graded isosurface stuffing: conforming, the right volume, good elements, and graded where asked.</summary>
public class GradedStuffingTests(ITestOutputHelper output)
{
    private const double R = 12.7, H = 60;

    private static bool Cylinder(Vec3 p) => p.X * p.X + p.Y * p.Y < R * R && p.Z > 0 && p.Z < H;

    /// <summary>Fine (h) in the top 15 mm, coarse (4h) below.</summary>
    private static Func<Vec3, double> TopFine(double h) => p => p.Z > H - 15 ? h : 4 * h;

    private (Vec3[] X, int[] T) Mesh(double hMin, int levels, Func<Vec3, double> size, bool sym)
    {
        var r = IsosurfaceStuffing.StuffGraded(Cylinder, new Vec3(-R, -R, 0), new Vec3(R, R, H), hMin, levels, size, sym, sym);
        output.WriteLine($"{r.Points.Length} nodes, {r.Tets.Length / 4} tets");
        return (r.Points, r.Tets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GradedMeshIsConformingWithTheRightVolume(bool sym)
    {
        var (x, t) = Mesh(1.0, 2, TopFine(1.0), sym);
        double vol = 0, qMin = 1;
        var faces = new Dictionary<(int, int, int), int>();
        for (int e = 0; e < t.Length / 4; e++)
        {
            var (a, b, c, d) = (t[4 * e], t[4 * e + 1], t[4 * e + 2], t[4 * e + 3]);
            double v = TetMesh.Volume(x[a], x[b], x[c], x[d]);
            Assert.True(v > 0, $"tet {e} inverted ({v})");
            vol += v;
            qMin = Math.Min(qMin, TetMesh.Quality(x[a], x[b], x[c], x[d]));
            foreach (var f in new[] { (a, b, c), (a, b, d), (a, c, d), (b, c, d) })
            {
                int[] s = [f.Item1, f.Item2, f.Item3];
                Array.Sort(s);
                var key = (s[0], s[1], s[2]);
                faces[key] = faces.GetValueOrDefault(key) + 1;
            }
        }

        Assert.DoesNotContain(faces.Values, n => n > 2);
        // Faces used once lie on the part surface (or a symmetry plane), never inside it: no cracks at size changes.
        foreach (var (f, n) in faces)
        {
            if (n != 1) continue;
            var c = (1.0 / 3) * (x[f.Item1] + x[f.Item2] + x[f.Item3]);
            double rr = Math.Sqrt(c.X * c.X + c.Y * c.Y);
            bool onSurface = Math.Abs(rr - R) < 1.0 || c.Z < 1.0 || c.Z > H - 1.0 || (sym && (c.X < 0.5 || c.Y < 0.5));
            Assert.True(onSurface, $"open face inside the part at ({c.X:F2}, {c.Y:F2}, {c.Z:F2})");
        }

        var worst = Enumerable.Range(0, t.Length / 4).Select(e => (Q: TetMesh.Quality(x[t[4 * e]], x[t[4 * e + 1]], x[t[4 * e + 2]], x[t[4 * e + 3]]), C: 0.25 * (x[t[4 * e]] + x[t[4 * e + 1]] + x[t[4 * e + 2]] + x[t[4 * e + 3]]))).OrderBy(w => w.Q).Take(6);
        foreach (var w in worst) output.WriteLine($"  worst q {w.Q:F3} at r {Math.Sqrt(w.C.X * w.C.X + w.C.Y * w.C.Y):F2} z {w.C.Z:F2}");
        var uni = IsosurfaceStuffing.Stuff(Cylinder, new Vec3(-R, -R, 0), new Vec3(R, R, H), 4.0, sym, sym);
        double uq = 1;
        for (int e = 0; e < uni.Tets.Length / 4; e++) uq = Math.Min(uq, TetMesh.Quality(uni.Points[uni.Tets[4 * e]], uni.Points[uni.Tets[4 * e + 1]], uni.Points[uni.Tets[4 * e + 2]], uni.Points[uni.Tets[4 * e + 3]]));
        output.WriteLine($"  uniform h 4 mesh of the same part: worst q {uq:F3}");
        double exact = Math.PI * R * R * H / (sym ? 4 : 1);
        output.WriteLine($"volume {vol:F0} vs {exact:F0} mm³ ({100 * (vol / exact - 1):+0.00;-0.00} %), worst quality {qMin:F3}");
        Assert.InRange(vol / exact, 0.97, 1.02); // coarse cells facet the curved surface
        Assert.True(qMin > 0.05, $"worst element quality {qMin:F3}"); // clipped transition tets; the solver smooths them after remeshing
    }

    [Fact]
    public void GradingPutsTheNodesWhereAsked()
    {
        var (x, _) = Mesh(1.0, 2, TopFine(1.0), true);
        int top = x.Count(p => p.Z > H - 15), bottom = x.Count(p => p.Z < H - 20);
        output.WriteLine($"nodes in the fine 15 mm: {top}, in the coarse bottom 40 mm: {bottom}");
        Assert.True(top > 5 * bottom, "the fine zone should hold far more nodes per volume");
    }

    /// <summary>Background lattice quality before clipping: regular BCC tets and the transition tets at size changes.</summary>
    [Fact]
    public void LatticeTetsAreWellShaped()
    {
        var (pos, _, tets) = GradedLattice.Build(new Vec3(-R, -R, 0), new Vec3(R, R, H), 1.0, 2, TopFine(1.0), new Vec3(0.31, 0.37, 0.17));
        var q = new List<double>();
        for (int e = 0; e < tets.Count / 4; e++) q.Add(Math.Abs(TetMesh.Quality(pos[tets[4 * e]], pos[tets[4 * e + 1]], pos[tets[4 * e + 2]], pos[tets[4 * e + 3]])));
        q.Sort();
        output.WriteLine($"lattice: {q.Count} tets, worst {q[0]:F3}, 1 % {q[q.Count / 100]:F3}, median {q[q.Count / 2]:F3}; distinct qualities: {string.Join(", ", q.Select(v => Math.Round(v, 3)).Distinct().Take(12))}");
        Assert.True(q[0] > 0.3, $"worst lattice tet {q[0]:F3}");
    }

    [Fact]
    public void UniformSizeMatchesTheUniformLattice()
    {
        var graded = IsosurfaceStuffing.StuffGraded(Cylinder, new Vec3(-R, -R, 0), new Vec3(R, R, H), 1.0, 2, _ => 1.0, true, true);
        var uniform = IsosurfaceStuffing.Stuff(Cylinder, new Vec3(-R, -R, 0), new Vec3(R, R, H), 1.0, true, true);
        output.WriteLine($"graded {graded.Points.Length} / uniform {uniform.Points.Length} nodes");
        Assert.InRange((double)graded.Points.Length / uniform.Points.Length, 0.97, 1.03);
    }
}
