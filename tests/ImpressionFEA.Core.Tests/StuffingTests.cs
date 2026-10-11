using ImpressionFEA.Core.ThreeD;
using Xunit.Abstractions;

namespace ImpressionFEA.Core.Tests;

public class StuffingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void CylinderMeshIsManifoldAccurateAndWellShaped(bool symX, bool symY)
    {
        const double r = 20, hgt = 30, h = 3;
        bool Inside(Vec3 p) => p.X * p.X + p.Y * p.Y < r * r && p.Z > 0 && p.Z < hgt;
        var res = IsosurfaceStuffing.Stuff(Inside, new Vec3(-r, -r, 0), new Vec3(r, r, hgt), h, symX, symY);
        var mesh = new TetMesh(res.Points.Length, res.Tets,
            res.Points.Select(p => symX && p.X == 0).ToArray(), res.Points.Select(p => symY && p.Y == 0).ToArray());

        double vol = 0, qmin = 1;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var t = mesh.Tets;
            var (a, b, c, d) = (res.Points[t[4 * e]], res.Points[t[4 * e + 1]], res.Points[t[4 * e + 2]], res.Points[t[4 * e + 3]]);
            vol += TetMesh.Volume(a, b, c, d);
            qmin = Math.Min(qmin, TetMesh.Quality(a, b, c, d));
        }

        double exact = Math.PI * r * r * hgt / ((symX ? 2 : 1) * (symY ? 2 : 1));
        output.WriteLine($"nodes {mesh.NodeCount}, tets {mesh.ElementCount}, volume {vol:F0} / {exact:F0}, q_min {qmin:F3}");
        Assert.InRange(vol / exact, 0.97, 1.01);
        Assert.True(qmin > 0.05, $"q_min {qmin}");
        // Symmetry faces exactly planar, physical surface nodes on the cylinder surface.
        for (int f = 0; f < mesh.FaceIsSymmetry.Length; f++)
        {
            if (mesh.FaceIsSymmetry[f]) continue;
            foreach (int n in mesh.BoundaryFaces.AsSpan(3 * f, 3))
            {
                var p = res.Points[n];
                double dr = Math.Abs(Math.Sqrt(p.X * p.X + p.Y * p.Y) - r);
                double dz = Math.Min(Math.Abs(p.Z), Math.Abs(p.Z - hgt));
                bool onPlane = (symX && p.X == 0) || (symY && p.Y == 0);
                Assert.True(onPlane || Math.Min(dr, dz) < 1e-3, $"surface node off the surface: {p}");
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void LocalRefinementIsConformingAndKeepsVolume(bool symX, bool symY)
    {
        const double r = 20, hgt = 30, h = 4;
        static bool Inside(Vec3 p) => p.X * p.X + p.Y * p.Y < r * r && p.Z > 0 && p.Z < hgt;
        var res = IsosurfaceStuffing.Stuff(Inside, new Vec3(-r, -r, 0), new Vec3(r, r, hgt), h, symX, symY);
        double V(Vec3[] x, int[] t)
        {
            double v = 0;
            for (int e = 0; e < t.Length / 4; e++)
            {
                double ve = TetMesh.Volume(x[t[4 * e]], x[t[4 * e + 1]], x[t[4 * e + 2]], x[t[4 * e + 3]]);
                Assert.True(ve > 0, "inverted tet after refinement");
                v += ve;
            }

            return v;
        }

        // Refine the bottom layer (z < 8) to h/4.
        var (pts, tets, splits) = TetRefiner.Refine(res.Points, res.Tets, p => p.Z < 8 ? h / 4 : h, 400000);
        Assert.True(splits > 0);
        Assert.Equal(V(res.Points, res.Tets), V(pts, tets), 6);
        // Conforming and manifold (TetMesh throws on a face shared by more than two tets) with symmetry planes intact.
        var mesh = new TetMesh(pts.Length, tets, pts.Select(p => symX && p.X == 0).ToArray(), pts.Select(p => symY && p.Y == 0).ToArray());
        double minQ = double.MaxValue, maxEdgeLow = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var a = pts[tets[4 * e]];
            var b = pts[tets[4 * e + 1]];
            var c = pts[tets[4 * e + 2]];
            var d = pts[tets[4 * e + 3]];
            minQ = Math.Min(minQ, TetMesh.Quality(a, b, c, d));
            if ((a + b + c + d).Z / 4 < 6) maxEdgeLow = Math.Max(maxEdgeLow, new[] { (a - b).Length, (a - c).Length, (a - d).Length, (b - c).Length, (b - d).Length, (c - d).Length }.Max());
        }

        output.WriteLine($"{res.Tets.Length / 4} → {tets.Length / 4} tets, {splits} splits, q_min {minQ:F3}, longest edge in refined zone {maxEdgeLow:F2} mm");
        Assert.True(minQ > 0.05);
        Assert.True(maxEdgeLow <= 1.05 * h / 4 + 1e-9);
    }
}
