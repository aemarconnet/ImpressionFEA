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

    /// <summary>
    /// Graded mesh trial: base mesh for <paramref name="targetNodes"/>, thin-flash refinement aiming for
    /// <paramref name="flashElements"/> elements through the closed gap, down to h / 2^levels, within the node budget.
    /// IFEA_STUDY_ONLY = "flash|target|levels|budget|elements" selects one case.
    /// </summary>
    [Theory]
    [Trait("Category", "Study")]
    [InlineData(1.6, 8000, 1, 12000, 0.0)]
    [InlineData(0.8, 8000, 1, 12000, 0.0)]
    [InlineData(1.6, 4000, 2, 16000, 1.5)]
    [InlineData(0.8, 4000, 2, 16000, 1.5)]
    [InlineData(1.6, 6000, 2, 16000, 1.5)]
    [InlineData(0.8, 6000, 2, 16000, 1.5)]
    [InlineData(1.6, 4000, 2, 16000, 1.2)]
    [InlineData(0.8, 4000, 2, 16000, 1.2)]
    [InlineData(1.6, 24000, 1, 36000, 0.0)]
    [InlineData(0.8, 24000, 1, 36000, 0.0)]
    [InlineData(1.6, 40000, 1, 60000, 0.0)]
    [InlineData(1.6, 4000, 2, 16000, 1.5, true)]
    [InlineData(0.8, 4000, 2, 16000, 1.5, true)]
    [InlineData(1.6, 6000, 2, 16000, 1.5, true)]
    [InlineData(0.8, 6000, 2, 16000, 1.5, true)]
    [InlineData(1.6, 4000, 1, 16000, 0.0, true)]
    [InlineData(1.6, 4000, 1, 30000, 0.0, false, true)]
    [InlineData(0.8, 4000, 1, 30000, 0.0, false, true)]
    [InlineData(1.6, 2000, 1, 20000, 0.0, false, true)]
    [InlineData(0.8, 2000, 1, 20000, 0.0, false, true)]
    [InlineData(1.6, 4000, 1, 40000, 0.0, false, true, 0.0)]
    [InlineData(1.6, 24000, 1, 40000, 0.0, false, true)]
    [InlineData(0.8, 24000, 1, 40000, 0.0, false, true)]
    [InlineData(1.6, 8000, 1, 16000, 0.0, false, true)]
    [InlineData(0.8, 8000, 1, 16000, 0.0, false, true)]
    [InlineData(1.6, 8000, 1, 40000, 0.0, false, false, 0.2, 4.0)]
    [InlineData(0.8, 8000, 1, 40000, 0.0, false, false, 0.2, 4.0)]
    [InlineData(1.6, 8000, 1, 40000, 0.0, false, false, 0.2, 2.0)]
    [InlineData(0.8, 8000, 1, 40000, 0.0, false, false, 0.2, 2.0)]
    public void GradedTrial(double flash, int targetNodes, int levels, int budget, double flashElements, bool edges = false, bool adaptive = false, double rateFraction = 0.2, double fineGap = 0)
    {
        if (Environment.GetEnvironmentVariable("IFEA_STUDY") is null) return;
        string id = FormattableString.Invariant($"{flash}|{targetNodes}|{levels}|{budget}|{flashElements}") + (edges ? "|edges" : "") + (adaptive ? "|graded" : "") + (rateFraction != 0.2 ? FormattableString.Invariant($"|rate{rateFraction}") : "") + (fineGap > 0 ? FormattableString.Invariant($"|fine{fineGap}") : "");
        if (Environment.GetEnvironmentVariable("IFEA_STUDY_ONLY") is { } only && only != id) return;
        if (flashElements > 0) Forging3DSolver.ThinFlashElements = flashElements;
        var die = DiePresets3D.HexBoltHead(flashThickness: flash);
        var (r0, dim) = die.RecommendedBillet();
        var cfg0 = new SimulationConfig3D
        {
            Die = die, Material = MaterialPresets.Steel4140, Temperature = 1150, BilletRadius = r0, BilletDimension = dim, FrictionFactor = 0.3, RamSpeed = 50,
            RefineThinFlash = flashElements > 0, RefinementLevels = levels, MaxNodeCount = budget, RefineDieEdges = edges, GradedMesh = adaptive, AdaptiveRateFraction = rateFraction, FineFinishGap = fineGap,
        };
        double h = cfg0.CalibratedAutoElementSize(targetNodes);
        var cfg = cfg0 with { ElementSize = h, StrokeIncrement = SimulationConfig3D.AutoStrokeIncrement(h) };
        int cutbacks = 0, remeshes = 0, maxNodes = 0;
        long nodeSum = 0;
        var sw = Stopwatch.StartNew();
        string status = "completed";
        using var solver = new Forging3DSolver(cfg, l =>
        {
            if (l.Contains("  cutback ", StringComparison.Ordinal)) cutbacks++;
            if (l.Contains("REMESH #", StringComparison.Ordinal)) remeshes++;
        });
        ForgingSnapshot s = solver.Initial();
        double loadBefore = 0;
        try
        {
            while (!solver.IsFinished)
            {
                s = solver.Step(CancellationToken.None);
                maxNodes = Math.Max(maxNodes, s.NodeCount);
                nodeSum += s.NodeCount;
                if (Environment.GetEnvironmentVariable("IFEA_STUDY_DIR") is { } progressDir)
                    File.AppendAllText(Path.Combine(progressDir, "progress-" + id.Replace('|', '_') + ".txt"), FormattableString.Invariant($"{DateTime.Now:HH:mm:ss} {sw.Elapsed.TotalMinutes,6:F1} min | step {s.Step,3} | stroke {s.Stroke,6:F2} mm | gap {s.DieGap,6:F2} mm | nodes {s.NodeCount,6} | load {s.LoadNewton / 9806.65,6:F1} t | fill {s.FillPercent,5:F1} % | ΔV {s.VolumeChangePercent,6:F2} %{Environment.NewLine}"));
                if (s.DieGap >= 0.25) loadBefore = s.LoadNewton;
            }
        }
        catch (SimulationException ex)
        {
            status = "FAILED " + ex.Message.Split('.')[0];
        }

        output.WriteLine(FormattableString.Invariant(
            $"GRADED | {id} | h {h:F2} | load {s.LoadNewton / 9806.65:F1} t (0.25 mm before {loadBefore / 9806.65:F1}) | ΔV {s.VolumeChangePercent:F2} % | nodes ≤ {maxNodes}, mean {(s.Step > 0 ? nodeSum / s.Step : 0)}, final {s.NodeCount} | {s.Step} incr, {cutbacks} cutbacks, {remeshes} remeshes | {sw.Elapsed.TotalMinutes:F2} min | {status} | {solver.AccuracyWarning ?? "no accuracy warning"}"));
    }

    /// <summary>
    /// Newton statistics of the Balanced hex bolt head (1.6 mm flash): corrections per increment, how fast the
    /// imbalance falls, how often the step is halved, and how many corrections come after the solution is already good
    /// to 1e-3 (what a looser tolerance would save). Writes newton-steps.csv and per-increment progress to IFEA_STUDY_DIR.
    /// </summary>
    [Fact]
    [Trait("Category", "Study")]
    public void NewtonStatistics()
    {
        if (Environment.GetEnvironmentVariable("IFEA_STUDY") is null || Environment.GetEnvironmentVariable("IFEA_STUDY_DIR") is not { } dir) return;
        var die = DiePresets3D.HexBoltHead();
        var (r0, dim) = die.RecommendedBillet();
        var cfg0 = new SimulationConfig3D { Die = die, Material = MaterialPresets.Steel4140, Temperature = 1150, BilletRadius = r0, BilletDimension = dim, FrictionFactor = 0.3, RamSpeed = 50 };
        double h = cfg0.CalibratedAutoElementSize();
        var cfg = cfg0 with { ElementSize = h, StrokeIncrement = SimulationConfig3D.AutoStrokeIncrement(h) };
        var iters = new List<(double Rel, double Lambda, bool Newton, string How)>();
        var rows = new List<string> { "step;stroke mm;load t;iterations;halved steps;after rel<1e-3;rels" };
        var sw = Stopwatch.StartNew();
        using var solver = new Forging3DSolver(cfg, l =>
        {
            // "    it  3 NR  |R| 1.234E+004 N  |Δv|/|v|  1.22E-001  λ 1.00  fact 2.3M   12.3 ms"
            if (!l.StartsWith("    it", StringComparison.Ordinal)) return;
            var t = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int iRel = Array.IndexOf(t, "|Δv|/|v|"), iLam = Array.IndexOf(t, "λ");
            if (iRel < 0 || iLam < 0) return;
            iters.Add((double.Parse(t[iRel + 1], CultureInfo.InvariantCulture), double.Parse(t[iLam + 1], CultureInfo.InvariantCulture), t[2] == "NR", t[iLam + 2]));
        });
        ForgingSnapshot s = solver.Initial();
        int total = 0, halved = 0, after = 0, facts = 0;
        while (!solver.IsFinished)
        {
            iters.Clear();
            s = solver.Step(CancellationToken.None);
            int firstGood = iters.FindIndex(x => x.Rel < 1e-3);
            int extra = firstGood >= 0 ? iters.Count - 1 - firstGood : 0;
            total += iters.Count;
            halved += iters.Count(x => x.Lambda < 1);
            after += extra;
            facts += iters.Count(x => x.How.StartsWith("fact", StringComparison.Ordinal));
            rows.Add(FormattableString.Invariant($"{s.Step};{s.Stroke:F2};{s.LoadNewton / 9806.65:F2};{iters.Count};{iters.Count(x => x.Lambda < 1)};{extra};{string.Join(" ", iters.Select(x => x.Rel.ToString("E1", CultureInfo.InvariantCulture)))}"));
            File.AppendAllText(Path.Combine(dir, "progress-newton.txt"), FormattableString.Invariant($"{DateTime.Now:HH:mm:ss} {sw.Elapsed.TotalMinutes,5:F1} min | step {s.Step,3} | gap {s.DieGap,6:F2} mm | iterations {iters.Count,2} | load {s.LoadNewton / 9806.65,6:F1} t{Environment.NewLine}"));
        }

        File.WriteAllLines(Path.Combine(dir, "newton-steps.csv"), rows);
        output.WriteLine(FormattableString.Invariant(
            $"NEWTON | {s.Step} increments, {total} corrections ({(double)total / s.Step:F1} per increment), {facts} factorisations, {halved} halved steps ({100.0 * halved / total:F0} %), {after} corrections after rel < 1e-3 ({100.0 * after / total:F0} %) | {sw.Elapsed.TotalMinutes:F1} min | {solver.TimingSummary}"));
    }

    /// <summary>
    /// Newton tuning experiment on the Balanced hex bolt head: tangent blend, quadratic line search, trend start and
    /// convergence tolerance. IFEA_STUDY_ONLY = case name. Progress per increment in IFEA_STUDY_DIR.
    /// </summary>
    [Theory]
    [Trait("Category", "Study")]
    [InlineData("baseline", 1.6, 1.0, false, false, 1e-4)]
    [InlineData("blend0.6", 1.6, 0.6, false, false, 1e-4)]
    [InlineData("quadLS", 1.6, 1.0, true, false, 1e-4)]
    [InlineData("trend", 1.6, 1.0, false, true, 1e-4)]
    [InlineData("tol1e-3", 1.6, 1.0, false, false, 1e-3)]
    [InlineData("all", 1.6, 0.6, true, true, 1e-3)]
    [InlineData("baseline-0.8", 0.8, 1.0, false, false, 1e-4)]
    [InlineData("all-0.8", 0.8, 0.6, true, true, 1e-3)]
    [InlineData("adapt", 1.6, 1.0, false, false, 1e-4, true)]
    [InlineData("adapt+trend", 1.6, 1.0, false, true, 1e-4, true)]
    [InlineData("adapt+all", 1.6, 0.6, true, true, 1e-3, true)]
    [InlineData("adapt-0.8", 0.8, 1.0, false, false, 1e-4, true)]
    [InlineData("sticky", 1.6, 1.0, false, false, 1e-4, false, true)]
    [InlineData("sticky+trend", 1.6, 1.0, false, true, 1e-4, false, true)]
    [InlineData("sticky-0.8", 0.8, 1.0, false, false, 1e-4, false, true)]
    [InlineData("abq-3", 1.6, 0.6, true, false, 1e-3, true)]
    [InlineData("abq-3-0.8", 0.8, 0.6, true, false, 1e-3, true)]
    [InlineData("abq-4", 1.6, 0.6, true, false, 1e-4, true)]
    public void NewtonExperiment(string name, double flash, double blend, bool quad, bool trend, double tol, bool adaptive = false, bool sticky = false)
    {
        if (Environment.GetEnvironmentVariable("IFEA_STUDY") is null || Environment.GetEnvironmentVariable("IFEA_STUDY_DIR") is not { } dir) return;
        if (Environment.GetEnvironmentVariable("IFEA_STUDY_ONLY") is { } only && only != name) return;
        Forging3DSolver.NewtonBlend = blend;
        Forging3DSolver.QuadraticLineSearch = quad;
        Forging3DSolver.StickySecant = sticky;
        if (Environment.GetEnvironmentVariable("IFEA_PCG") is { } pcg) Forging3DSolver.PcgBudget = int.Parse(pcg, CultureInfo.InvariantCulture);
        Forging3DSolver.AdaptiveNewton = adaptive;
        Forging3DSolver.ExtrapolateStart = trend;
        var die = DiePresets3D.HexBoltHead(flashThickness: flash);
        var (r0, dim) = die.RecommendedBillet();
        var cfg0 = new SimulationConfig3D { Die = die, Material = MaterialPresets.Steel4140, Temperature = 1150, BilletRadius = r0, BilletDimension = dim, FrictionFactor = 0.3, RamSpeed = 50, Tolerance = tol };
        double h = cfg0.CalibratedAutoElementSize();
        var cfg = cfg0 with { ElementSize = h, StrokeIncrement = SimulationConfig3D.AutoStrokeIncrement(h) };
        int corrections = 0, facts = 0;
        var sw = Stopwatch.StartNew();
        string status = "completed";
        using var solver = new Forging3DSolver(cfg, l =>
        {
            if (!l.StartsWith("    it", StringComparison.Ordinal)) return;
            corrections++;
            if (l.Contains(" fact", StringComparison.Ordinal)) facts++;
        });
        ForgingSnapshot s = solver.Initial();
        double peak = 0, before = 0;
        string progress = Path.Combine(dir, $"progress-newton-{name}.txt");
        try
        {
            while (!solver.IsFinished)
            {
                s = solver.Step(CancellationToken.None);
                peak = Math.Max(peak, s.LoadNewton);
                if (s.DieGap >= 0.25) before = s.LoadNewton;
                File.AppendAllText(progress, FormattableString.Invariant($"{DateTime.Now:HH:mm:ss} {sw.Elapsed.TotalMinutes,5:F1} min | step {s.Step,3} | gap {s.DieGap,6:F2} mm | corrections {corrections,4} | load {s.LoadNewton / 9806.65,6:F1} t{Environment.NewLine}"));
            }
        }
        catch (SimulationException ex)
        {
            status = "FAILED " + ex.Message.Split('.')[0];
        }

        output.WriteLine(FormattableString.Invariant(
            $"NEWTONX | {name} | flash {flash} | {s.Step} incr | {corrections} corrections ({(double)corrections / Math.Max(1, s.Step):F1}/incr), {facts} factorisations | load {s.LoadNewton / 9806.65:F1} t, before {before / 9806.65:F1} t, peak {peak / 9806.65:F1} t | fill {s.FillPercent:F2} % | ΔV {s.VolumeChangePercent:F2} % | {sw.Elapsed.TotalMinutes:F2} min | {status}"));
    }

    /// <summary>Full solver log of the first <paramref name="steps"/> increments for one Newton setting (IFEA_STUDY_DIR/trace-name.txt).</summary>
    [Theory]
    [Trait("Category", "Study")]
    [InlineData("trace-baseline", 1.0, false, false, 1e-4, 40)]
    [InlineData("trace-tol1e-3", 1.0, false, false, 1e-3, 40)]
    [InlineData("trace-trend", 1.0, false, true, 1e-4, 40)]
    [InlineData("trace-all", 0.6, true, true, 1e-3, 40)]
    [InlineData("trace-adapt", 1.0, false, false, 1e-4, 40, true)]
    public void NewtonTrace(string name, double blend, bool quad, bool trend, double tol, int steps, bool adaptive = false)
    {
        if (Environment.GetEnvironmentVariable("IFEA_STUDY") is null || Environment.GetEnvironmentVariable("IFEA_STUDY_DIR") is not { } dir) return;
        if (Environment.GetEnvironmentVariable("IFEA_STUDY_ONLY") is { } only && only != name) return;
        Forging3DSolver.NewtonBlend = blend;
        Forging3DSolver.QuadraticLineSearch = quad;
        Forging3DSolver.AdaptiveNewton = adaptive;
        Forging3DSolver.ExtrapolateStart = trend;
        var die = DiePresets3D.HexBoltHead();
        var (r0, dim) = die.RecommendedBillet();
        var cfg0 = new SimulationConfig3D { Die = die, Material = MaterialPresets.Steel4140, Temperature = 1150, BilletRadius = r0, BilletDimension = dim, FrictionFactor = 0.3, RamSpeed = 50, Tolerance = tol };
        double h = cfg0.CalibratedAutoElementSize();
        var cfg = cfg0 with { ElementSize = h, StrokeIncrement = SimulationConfig3D.AutoStrokeIncrement(h) };
        string file = Path.Combine(dir, name + ".txt");
        using var log = new StreamWriter(file) { AutoFlush = true };
        using var solver = new Forging3DSolver(cfg, l => log.WriteLine(l));
        solver.Initial();
        for (int i = 0; i < steps && !solver.IsFinished; i++) solver.Step(CancellationToken.None);
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
