using System.Diagnostics;
using System.Globalization;
using ImpressionFEA.Core.Materials;
using ImpressionFEA.Core.Simulation;
using ImpressionFEA.Core.Solver;
using ImpressionFEA.Core.ThreeD;
using Xunit.Abstractions;

namespace ImpressionFEA.Core.Tests;

/// <summary>
/// Mesh study for thin flash (3D): thin-flash refinement, refinement levels, cutbacks and node budget on the 1"-8 hex
/// bolt head with a 0.8 mm and a 1.6 mm flash land. Hours of solver time, so it only runs with IFEA_STUDY set; each run
/// appends a row to IFEA_STUDY_CSV (if set) and prints a STUDY line.
/// </summary>
public class FlashRefinementStudy(ITestOutputHelper output)
{
    public static TheoryData<string, double, bool, int, int, int, int> Cases()
    {
        var data = new TheoryData<string, double, bool, int, int, int, int>();
        foreach (double flash in new[] { 0.8, 1.6 })
        {
            data.Add("A defaults (8k nodes, no thin-flash refinement)", flash, false, 1, 5, 12000, 8000);
            data.Add("S 2k nodes (quarter model: same h as the 8k full model)", flash, false, 1, 5, 12000, 2000);
            data.Add("T 4k nodes", flash, false, 1, 5, 12000, 4000);
            data.Add("U 8k nodes, thin flash 1 level, budget 16k", flash, true, 1, 5, 16000, 8000);
            data.Add("G as A, 3 cutbacks", flash, false, 1, 3, 12000, 8000);
            data.Add("F fast: 5k nodes", flash, false, 1, 5, 12000, 5000);
            data.Add("B thin flash 1 level, budget 16k", flash, true, 1, 5, 16000, 8000);
            data.Add("H fast + thin flash 1 level (5k nodes, budget 10k)", flash, true, 1, 5, 10000, 5000);
            data.Add("R reference: 24k nodes uniform, budget 36k", flash, false, 1, 5, 36000, 24000);
        }

        return data;
    }

    /// <summary>Where volume is lost: ΔV after every increment, with the remeshes in between (hex bolt head, given target nodes).</summary>
    [Theory]
    [Trait("Category", "Study")]
    [InlineData(1.6, 4000)]
    [InlineData(1.6, 8000)]
    [InlineData(0.8, 8000)]
    public void VolumeTrace(double flash, int targetNodes)
    {
        if (Environment.GetEnvironmentVariable("IFEA_STUDY") is null) return;
        if (Environment.GetEnvironmentVariable("IFEA_MAXCOMP") is { } mc) Forging3DSolver.MaxCompensation = double.Parse(mc, CultureInfo.InvariantCulture);
        var sw = Stopwatch.StartNew();
        var die = DiePresets3D.HexBoltHead(flashThickness: flash);
        var (r0, dim) = die.RecommendedBillet();
        var cfg0 = new SimulationConfig3D { Die = die, Material = MaterialPresets.Steel4140, Temperature = 1150, BilletRadius = r0, BilletDimension = dim, FrictionFactor = 0.3, RamSpeed = 50 };
        double h = cfg0.CalibratedAutoElementSize(targetNodes);
        var cfg = cfg0 with { ElementSize = h, StrokeIncrement = SimulationConfig3D.AutoStrokeIncrement(h) };
        var pending = new List<string>();
        using var solver = new Forging3DSolver(cfg, l =>
        {
            if (l.Contains("REMESH #", StringComparison.Ordinal) || l.Contains("rejected", StringComparison.Ordinal)) pending.Add(l.Trim());
        });
        ForgingSnapshot s = solver.Initial();
        double prev = 0;
        while (!solver.IsFinished)
        {
            s = solver.Step(CancellationToken.None);
            string rem = pending.Count > 0 ? " | " + string.Join(" ; ", pending.Select(p => p.Length > 60 ? p[..60] : p)) : "";
            output.WriteLine(FormattableString.Invariant($"VOL | step {s.Step,3} gap {s.DieGap,6:F2} mm | ΔV {s.VolumeChangePercent,7:F3} % | jump {s.VolumeChangePercent - prev,7:F3} %{rem}"));
            prev = s.VolumeChangePercent;
            pending.Clear();
        }
        output.WriteLine(FormattableString.Invariant($"VOLSUM | max compensation {Forging3DSolver.MaxCompensation} h | final ΔV {s.VolumeChangePercent:F2} % | load {s.LoadNewton / 9806.65:F1} t | fill {s.FillPercent:F1} % | {sw.Elapsed.TotalMinutes:F2} min | flash {flash} mm, {targetNodes} nodes | {solver.AccuracyWarning ?? "no accuracy warning"}"));
    }

    /// <summary>Where the time goes: Balanced hex bolt head (1.6 mm flash) per linear-solver backend.</summary>
    [Theory]
    [Trait("Category", "Study")]
    [InlineData("Auto")]
    [InlineData("CudaGpu")]
    [InlineData("MklCpu")]
    public void ProfileHexBalanced(string backend)
    {
        if (Environment.GetEnvironmentVariable("IFEA_STUDY") is null) return;
        if (Environment.GetEnvironmentVariable("IFEA_STUDY_ONLY") is { } only && only != backend) return;
        var die = DiePresets3D.HexBoltHead();
        var (r0, dim) = die.RecommendedBillet();
        var cfg0 = new SimulationConfig3D
        {
            Die = die, Material = MaterialPresets.Steel4140, Temperature = 1150, BilletRadius = r0, BilletDimension = dim, FrictionFactor = 0.3, RamSpeed = 50,
            LinearBackend = Enum.Parse<Solver.Linear.LinearBackend>(backend),
        };
        double h = cfg0.CalibratedAutoElementSize();
        var cfg = cfg0 with { ElementSize = h, StrokeIncrement = SimulationConfig3D.AutoStrokeIncrement(h) };
        var log = new List<string>();
        var sw = Stopwatch.StartNew();
        using var solver = new Forging3DSolver(cfg, log.Add);
        ForgingSnapshot s = solver.Initial();
        while (!solver.IsFinished) s = solver.Step(CancellationToken.None);
        output.WriteLine($"PROFILE | {backend} | {sw.Elapsed.TotalMinutes:F2} min | {solver.TimingSummary} | {log.FirstOrDefault(l => l.Contains("Linear solver", StringComparison.Ordinal))}");
        output.WriteLine($"PROFILE | {backend} | iterations: NR/PI lines {log.Count(l => l.StartsWith("    it", StringComparison.Ordinal))}, PCG solves {log.Count(l => l.Contains(" pcg ", StringComparison.Ordinal))}, factorisations {log.Count(l => l.Contains(" fact", StringComparison.Ordinal))}");
    }

    [Theory]
    [Trait("Category", "Study")]
    [MemberData(nameof(Cases))]
    public void HexBoltHeadFlash(string name, double flash, bool thinFlash, int levels, int cutbacks, int budget, int targetNodes)
    {
        if (Environment.GetEnvironmentVariable("IFEA_STUDY") is null)
        {
            output.WriteLine("Set IFEA_STUDY=1 to run the flash refinement study.");
            return;
        }

        // IFEA_STUDY_ONLY="A|0.8" runs one case (config letter | flash), so the cases can run as parallel processes.
        string id = $"{name[0]}|{flash.ToString(CultureInfo.InvariantCulture)}";
        if (Environment.GetEnvironmentVariable("IFEA_STUDY_ONLY") is { } only && only != id) return;
        string? progress = Environment.GetEnvironmentVariable("IFEA_STUDY_CSV") is { } csvPath
            ? Path.Combine(Path.GetDirectoryName(csvPath)!, $"progress-{name[0]}-{flash.ToString(CultureInfo.InvariantCulture)}.txt")
            : null;

        var die = DiePresets3D.HexBoltHead(flashThickness: flash);
        var (r0, dim) = die.RecommendedBillet();
        var cfg0 = new SimulationConfig3D
        {
            Die = die, Material = MaterialPresets.Steel4140, Temperature = 1150, BilletRadius = r0, BilletDimension = dim,
            FrictionFactor = 0.3, RamSpeed = 50, MaxNodeCount = budget, RefineThinFlash = thinFlash, RefinementLevels = levels, MaxCutbacks = cutbacks,
        };
        double h = cfg0.CalibratedAutoElementSize(targetNodes);
        var cfg = cfg0 with { ElementSize = h, StrokeIncrement = SimulationConfig3D.AutoStrokeIncrement(h) };

        int cutbackLines = 0, remeshes = 0, refinements = 0, maxNodes = 0, steps = 0;
        double peak = 0, minVolume = 0, loadBefore = 0;
        string status = "completed";
        ForgingSnapshot? last = null;
        var sw = Stopwatch.StartNew();
        try
        {
            using var solver = new Forging3DSolver(cfg, line =>
            {
                if (line.Contains("  cutback ", StringComparison.Ordinal)) cutbackLines++;
                if (line.Contains("REMESH #", StringComparison.Ordinal)) remeshes++;
                if (line.Contains("local refinement to", StringComparison.Ordinal)) refinements++;
            });
            last = solver.Initial();
            maxNodes = last.NodeCount;
            while (!solver.IsFinished)
            {
                last = solver.Step(CancellationToken.None);
                steps++;
                peak = Math.Max(peak, last.LoadNewton);
                maxNodes = Math.Max(maxNodes, last.NodeCount);
                minVolume = Math.Min(minVolume, last.VolumeChangePercent);
                if (last.DieGap >= 0.25) loadBefore = last.LoadNewton;
                if (progress is not null)
                    File.AppendAllText(progress, FormattableString.Invariant($"{DateTime.Now:HH:mm:ss} step {steps} stroke {last.Stroke:F2} mm gap {last.DieGap:F2} mm nodes {last.NodeCount} load {last.LoadNewton / 9806.65:F1} t fill {last.FillPercent:F1} % ({sw.Elapsed.TotalMinutes:F1} min){Environment.NewLine}"));
            }
        }
        catch (SimulationException ex)
        {
            status = "FAILED: " + ex.Message.Split('.')[0];
        }

        sw.Stop();
        var pen = last is Snapshot3D s3 ? ContactPenetrationStudy.Measure(s3, die, h) : default;
        const double tonnes = 9806.65;
        string row = string.Join(";", new object[]
        {
            name, flash, h.ToString("F2", CultureInfo.InvariantCulture), (last?.LoadNewton ?? 0) / tonnes, loadBefore / tonnes, peak / tonnes, last?.FillPercent ?? 0,
            last?.VolumeChangePercent ?? 0, minVolume, pen.Node, pen.Inside, maxNodes, steps, cutbackLines, remeshes, refinements, sw.Elapsed.TotalMinutes, status,
        }.Select(o => o is double d ? d.ToString("F2", CultureInfo.InvariantCulture) : Convert.ToString(o, CultureInfo.InvariantCulture)));
        output.WriteLine("STUDY | " + row);
        if (Environment.GetEnvironmentVariable("IFEA_STUDY_CSV") is { } csv)
        {
            // Parallel runs (one process per case) share the file: retry while another process holds it.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (!File.Exists(csv))
                        File.WriteAllText(csv, "config;flash mm;h mm;load at closure t;load 0.25 mm before closure t;peak load t;fill %;volume change %;worst volume change %;node penetration at closure mm;nodes inside > 0.05h;max nodes;increments;cutbacks;remeshes;hot-spot refinements;minutes;status" + Environment.NewLine);
                    File.AppendAllText(csv, row + Environment.NewLine);
                    break;
                }
                catch (IOException) when (attempt < 50)
                {
                    Thread.Sleep(200);
                }
            }
        }
    }
}
