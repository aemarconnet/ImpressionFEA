using System.Globalization;
using ImpressionFEA.Core.Materials;
using ImpressionFEA.Core.Simulation;
using ImpressionFEA.Core.Solver;
using ImpressionFEA.Core.ThreeD;
using Xunit.Abstractions;

namespace ImpressionFEA.Core.Tests;

/// <summary>
/// How far the workpiece mesh enters the dies (3D hex bolt head): nodes, tet edge midpoints and face centroids
/// checked against both dies at every increment, reported in mm and relative to the global h. Runs with IFEA_STUDY.
/// </summary>
public class ContactPenetrationStudy(ITestOutputHelper output)
{
    [Theory]
    [Trait("Category", "Study")]
    [InlineData("default (thin flash off, 1 level)", 1.6, false, 1)]
    [InlineData("thin flash, 2 levels", 1.6, true, 2)]
    public void HexBoltHeadPenetration(string name, double flash, bool thinFlash, int levels)
    {
        if (Environment.GetEnvironmentVariable("IFEA_STUDY") is null) return;
        var die = DiePresets3D.HexBoltHead(flashThickness: flash);
        var (r0, dim) = die.RecommendedBillet();
        var cfg0 = new SimulationConfig3D
        {
            Die = die, Material = MaterialPresets.Steel4140, Temperature = 1150, BilletRadius = r0, BilletDimension = dim,
            FrictionFactor = 0.3, RamSpeed = 50, RefineThinFlash = thinFlash, RefinementLevels = levels,
            MaxNodeCount = thinFlash ? 20000 : SimulationConfig3D.DefaultMaxNodes,
        };
        double h = cfg0.CalibratedAutoElementSize();
        var cfg = cfg0 with { ElementSize = h, StrokeIncrement = SimulationConfig3D.AutoStrokeIncrement(h) };
        var log = new List<string>();
        using var solver = new Forging3DSolver(cfg, log.Add);
        ForgingSnapshot s = solver.Initial();
        double worstNode = 0, worstPoint = 0;
        string worstAt = "";
        var csv = new List<string> { "step;stroke mm;nodes;node pen mm;edge/face pen mm;nodes inside > 0.05h;worst location" };
        try
        {
            while (!solver.IsFinished)
            {
                s = solver.Step(CancellationToken.None);
                var (node, point, inside, where) = Measure((Snapshot3D)s, die, h);
                csv.Add(string.Join(";", s.Step, F(s.Stroke), s.NodeCount, F(node), F(point), inside, where));
                if (point > worstPoint) { worstPoint = point; worstAt = $"step {s.Step}, {where}"; }
                worstNode = Math.Max(worstNode, node);
            }
        }
        catch (SimulationException ex)
        {
            output.WriteLine("FAILED: " + ex.Message);
            if (Environment.GetEnvironmentVariable("IFEA_STUDY_DIR") is { } logDir)
                File.WriteAllLines(Path.Combine(logDir, "failure-log.txt"), log.TakeLast(400));
        }

        output.WriteLine($"PEN | {name} | h {h:F2} mm | worst node penetration {worstNode:F3} mm ({worstNode / h:F2} h) | worst edge/face penetration {worstPoint:F3} mm ({worstPoint / h:F2} h) at {worstAt} | corner placements {solver.CornerPenetrations}, reverts {solver.ProjectionReverts}");
        if (Environment.GetEnvironmentVariable("IFEA_STUDY_DIR") is { } dir)
            File.WriteAllLines(Path.Combine(dir, $"penetration-{(thinFlash ? "thinflash" : "default")}-{flash.ToString(CultureInfo.InvariantCulture)}.csv"), csv);
    }

    private static string F(double v) => v.ToString("F3", CultureInfo.InvariantCulture);

    /// <summary>Deepest penetration of nodes, and of tet edge midpoints / face centroids (catches faces cutting die corners).</summary>
    internal static (double Node, double Point, int Inside, string Where) Measure(Snapshot3D s, DieSet3D die, double h)
    {
        double gap = s.DieGap;
        double Pen(Vec3 p) => Math.Max(0, -Math.Min(die.Bottom.Clearance(p, 0), die.TopClosed.Clearance(p, gap)));
        var x = new Vec3[s.NodeCount];
        for (int n = 0; n < x.Length; n++) x[n] = new Vec3(s.X[n], s.Y[n], s.Z[n]);
        double node = 0, point = 0;
        int inside = 0;
        Vec3 at = default;
        foreach (var p in x)
        {
            double d = Pen(p);
            if (d > 0.05 * h) inside++;
            if (d > node) node = d;
            if (d > point) { point = d; at = p; }
        }

        var t = s.Mesh.Tets;
        for (int e = 0; e < s.Mesh.ElementCount; e++)
        {
            for (int a = 0; a < 4; a++)
            {
                for (int b = a + 1; b < 4; b++)
                {
                    var m = 0.5 * (x[t[4 * e + a]] + x[t[4 * e + b]]);
                    double d = Pen(m);
                    if (d > point) { point = d; at = m; }
                }

                var c = (1.0 / 3) * (x[t[4 * e + (a + 1) % 4]] + x[t[4 * e + (a + 2) % 4]] + x[t[4 * e + (a + 3) % 4]]);
                double dc = Pen(c);
                if (dc > point) { point = dc; at = c; }
            }
        }

        string where = $"r {Math.Sqrt(at.X * at.X + at.Y * at.Y):F1} mm, z {at.Z:F2} mm";
        return (node, point, inside, where);
    }
}
