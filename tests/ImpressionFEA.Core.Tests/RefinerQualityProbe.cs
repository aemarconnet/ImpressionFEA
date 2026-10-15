using ImpressionFEA.Core.ThreeD;
using Xunit.Abstractions;

namespace ImpressionFEA.Core.Tests;

public class RefinerQualityProbe(ITestOutputHelper output)
{
    [Fact]
    public void Probe()
    {
        if (Environment.GetEnvironmentVariable("IFEA_STUDY") is null) return;
        bool Inside(Vec3 p) => p.X * p.X + p.Y * p.Y < 12.7 * 12.7 && p.Z > 0 && p.Z < 80;
        var min = new Vec3(0, 0, 0);
        var max = new Vec3(12.7, 12.7, 80);
        void Report(string name, Vec3[] x, int[] t)
        {
            var q = new List<double>();
            for (int e = 0; e < t.Length / 4; e++) q.Add(TetMesh.Quality(x[t[4 * e]], x[t[4 * e + 1]], x[t[4 * e + 2]], x[t[4 * e + 3]]));
            q.Sort();
            output.WriteLine($"QUAL | {name,-34} | {x.Length,6} nodes {q.Count,7} tets | min {q[0]:F3} | 1% {q[q.Count / 100]:F3} | 10% {q[q.Count / 10]:F3} | median {q[q.Count / 2]:F3}");
        }

        var fine = IsosurfaceStuffing.Stuff(Inside, min, max, 1.05, true, true);
        Report("stuffed at h 1.05", fine.Points, fine.Tets);
        var coarse = IsosurfaceStuffing.Stuff(Inside, min, max, 2.1, true, true);
        Report("stuffed at h 2.10", coarse.Points, coarse.Tets);
        var (rx, rt, _) = TetRefiner.Refine(coarse.Points, coarse.Tets, _ => 1.05, 200000);
        Report("stuffed 2.10, bisected to 1.05", rx, rt);
    }
}
