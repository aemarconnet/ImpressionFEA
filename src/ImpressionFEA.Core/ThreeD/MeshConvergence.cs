using System.Diagnostics;
using ImpressionFEA.Core.Simulation;

namespace ImpressionFEA.Core.ThreeD;

/// <summary>One element size of a convergence check.</summary>
public sealed record ConvergenceLevel(double H, double? ClosingLoad, double PeakLoad, double FillPercent, double VolumeChangePercent, int Nodes, TimeSpan Elapsed, string Status);

/// <summary>Outcome of a convergence check.</summary>
public enum ConvergenceKind
{
    /// <summary>The run's closing load is within <see cref="MeshConvergence.ConvergedPercent"/> of the estimate.</summary>
    Converged,

    /// <summary>The finer levels give an estimate, and the run's mesh is further from it.</summary>
    NotConverged,

    /// <summary>
    /// The load changes steadily but does not level off with refinement (too slow to extrapolate): the run's mesh is at
    /// least as far off as from the finest level, and the fine-mesh load lies beyond the finest value.
    /// </summary>
    StillChanging,

    /// <summary>The load does not change steadily with the mesh: the finest value, with the spread as uncertainty.</summary>
    Uncertain,

    /// <summary>A level did not reach die closure.</summary>
    NoEstimate,
}

/// <summary>
/// Result of a convergence check: the levels, the best estimate of the closing load at zero element size (t; the finest
/// level when there is no steady trend; null when a level did not close), its uncertainty, the observed order (null when
/// not determined) and how far the run's own mesh is from the estimate (null unless converged or not converged).
/// </summary>
public sealed record ConvergenceResult(
    IReadOnlyList<ConvergenceLevel> Levels,
    ConvergenceKind Kind,
    double? Estimate,
    double UncertaintyPercent,
    double? Order,
    double? ErrorOfRunPercent)
{
    /// <summary>The closing load grows with refinement (finest level above the run's).</summary>
    public bool Rising => Levels.Count > 0 && Levels[^1].ClosingLoad > Levels[0].ClosingLoad;

    /// <summary>The run's mesh needs refining (not converged, or still changing).</summary>
    public bool NeedsFinerMesh => Kind is ConvergenceKind.NotConverged or ConvergenceKind.StillChanging;

    /// <summary>The verdict in tonnes and millimetres.</summary>
    public string Verdict => Describe(t => FormattableString.Invariant($"{t:F1} t"), mm => FormattableString.Invariant($"{mm:F2} mm"));

    /// <summary>The verdict in words, with loads written by <paramref name="load"/> (from tonnes) and element sizes by <paramref name="length"/> (from mm).</summary>
    public string Describe(Func<double, string> load, Func<double, string> length)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(length);
        string I(FormattableString s) => FormattableString.Invariant(s);
        string levels = "(h " + string.Join(", ", Levels.Select(l => length(l.H) + ": " + (l.ClosingLoad is { } c ? load(c) : "–"))) + ")";
        return Kind switch
        {
            ConvergenceKind.Converged => I($"Converged: the run's closing load is within {Math.Abs(ErrorOfRunPercent ?? 0):F1} % of the fine-mesh estimate, ") + load(Estimate!.Value) + I($" ±{UncertaintyPercent:F0} % ") + levels + ".",
            ConvergenceKind.NotConverged => I($"Not converged: the run's closing load is {ErrorOfRunPercent:+0.0;-0.0;0.0} % off the fine-mesh estimate, ") + load(Estimate!.Value) + I($" ±{UncertaintyPercent:F0} % ") + levels + ". Use a finer mesh (Accurate mesh quality or a smaller h), or take the estimate.",
            ConvergenceKind.StillChanging => I($"Not converged: the closing load still {(Rising ? "rises" : "falls")} with every refinement ") + levels + I($", so the run's mesh is {Math.Abs(ErrorOfRunPercent ?? 0):F0} % or more {(Rising ? "low" : "high")}, and the fine-mesh load is likely {(Rising ? "above" : "below")} ") + load(Estimate!.Value) + (Math.Abs(ErrorOfRunPercent ?? 0) > 20 ? ". Use a much finer mesh." : ". Use a finer mesh (Accurate mesh quality or a smaller h), or take the finest value as a bound."),
            ConvergenceKind.Uncertain => "Inconclusive: the closing load does not change steadily with the mesh " + levels + ". Take the finest value, " + load(Estimate!.Value) + I($", with about ±{UncertaintyPercent:F0} % uncertainty."),
            _ => "Not every level reached die closure " + levels + "; no estimate.",
        };
    }
}

/// <summary>
/// Mesh convergence check from a checkpoint near die closure: the end of the stroke is re-run at the run's element size
/// and two finer ones (ratio <see cref="Ratio"/>) in parallel, each closing load is fitted (<see cref="LoadMetrics"/>),
/// and the load is extrapolated to h → 0 (Richardson: L(h) = L∞ + C·h^p). The part before the checkpoint — most of the
/// stroke, where loads are small and insensitive to the mesh — is not repeated.
/// </summary>
public static class MeshConvergence
{
    /// <summary>Element-size ratio between the levels.</summary>
    public const double Ratio = 0.8;

    /// <summary>Die gap (mm) at which the checkpoint for the check is taken: before the flash forms.</summary>
    public const double CheckpointGap = 2.0;

    /// <summary>
    /// Scatter of the fitted closing load between equivalent runs, % (about ±1 %, see <see cref="LoadMetrics"/>): load
    /// differences below it between two levels are noise, not a mesh trend.
    /// </summary>
    public const double NoisePercent = 1.5;

    /// <summary>The run's mesh counts as converged within this distance of the estimate, %.</summary>
    public const double ConvergedPercent = 3.0;

    /// <summary>Runs the three levels in parallel.</summary>
    /// <param name="log">Progress lines (called from worker threads, one at a time).</param>
    /// <param name="progress">Completed fraction 0–1 of the slowest level (called from worker threads, one at a time).</param>
    public static ConvergenceResult Run(SimulationConfig3D config, Forging3DCheckpoint checkpoint, Action<string>? log = null, Action<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(checkpoint);
        double h0 = checkpoint.H;
        double[] sizes = [h0, h0 * Ratio, h0 * Ratio * Ratio];
        var levels = new ConvergenceLevel[sizes.Length];
        var done = new double[sizes.Length]; // fraction of the remaining stroke per level
        double gap0 = Math.Max(checkpoint.Gap, 1e-6);
        var sync = new object();

        void Report(int level, double fraction)
        {
            lock (sync)
            {
                done[level] = Math.Clamp(fraction, done[level], 1);
                progress?.Invoke(done.Min()); // the slowest level sets the pace
            }
        }

        Parallel.For(0, sizes.Length, new ParallelOptions { CancellationToken = ct }, i =>
        {
            double h = sizes[i];
            var cfg = config with
            {
                ElementSize = h,
                StrokeIncrement = SimulationConfig3D.AutoStrokeIncrement(h),
                MaxNodeCount = Math.Max(config.MaxNodeCount, (int)(config.MaxNodeCount * Math.Pow(h0 / h, 3) * 1.2)),
            };
            var sw = Stopwatch.StartNew();
            var history = new List<(double Gap, double Load)>();
            double peak = 0;
            ForgingSnapshot? last = null;
            string status = "completed";
            try
            {
                using var solver = new Forging3DSolver(cfg, _ => { });
                solver.RestoreFrom(checkpoint);
                while (!solver.IsFinished)
                {
                    ct.ThrowIfCancellationRequested();
                    last = solver.Step(ct);
                    history.Add((last.DieGap, last.LoadTonnes));
                    peak = Math.Max(peak, last.LoadTonnes);
                    Report(i, 1 - last.DieGap / gap0);
                    if (history.Count % 5 == 0) lock (sync) log?.Invoke(FormattableString.Invariant($"Convergence check: h {h:F2} mm at gap {last.DieGap:F2} mm, load {last.LoadTonnes:F1} t, {last.NodeCount:N0} nodes, {sw.Elapsed.TotalMinutes:F1} min"));
                }
            }
            catch (Solver.SimulationException ex)
            {
                status = "failed: " + ex.Message.Split('.')[0];
            }

            Report(i, 1);
            levels[i] = new ConvergenceLevel(h, LoadMetrics.ClosingLoad(history), peak, last?.FillPercent ?? 0, last?.VolumeChangePercent ?? 0, last?.NodeCount ?? 0, sw.Elapsed, status);
            lock (sync) log?.Invoke(FormattableString.Invariant($"Convergence check: h {h:F2} mm → closing load {(levels[i].ClosingLoad is { } c ? FormattableString.Invariant($"{c:F1} t") : "–")}, {levels[i].Nodes:N0} nodes, {sw.Elapsed.TotalMinutes:F1} min ({status})"));
        });

        return Evaluate(levels);
    }

    /// <summary>
    /// Estimate from three levels with a constant size ratio (run, finer, finest):
    /// <list type="bullet">
    /// <item>the two finer levels agree within the closing-load noise (or the trend dies out faster than order 4): the
    /// load has settled, the estimate is the finest value;</item>
    /// <item>a steady trend of order 0.5–4: Richardson extrapolation L∞ = L₃ + (L₃ − L₂)/(r^p − 1);</item>
    /// <item>a steady trend that does not level off (order below 0.5): still changing — the run is at least as far off
    /// as the finest level;</item>
    /// <item>a zig-zag beyond the noise: inconclusive, the finest value with the spread of the levels as uncertainty.</item>
    /// </list>
    /// </summary>
    public static ConvergenceResult Evaluate(IReadOnlyList<ConvergenceLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if (levels.Count != 3 || levels.Any(l => l.ClosingLoad is not > 0))
            return new ConvergenceResult(levels, ConvergenceKind.NoEstimate, null, 0, null, null);
        double l1 = levels[0].ClosingLoad!.Value, l2 = levels[1].ClosingLoad!.Value, l3 = levels[2].ClosingLoad!.Value;
        double spread = 100 * (levels.Max(l => l.ClosingLoad!.Value) - levels.Min(l => l.ClosingLoad!.Value)) / l3;
        double d12 = l1 - l2, d23 = l2 - l3;
        bool steady = d12 * d23 > 0;
        double? p = steady ? Math.Log(d12 / d23) / Math.Log(1 / Ratio) : null;

        double estimate, uncertainty;
        if (100 * Math.Abs(d23) / l3 <= NoisePercent || p > 4)
        {
            estimate = l3;
            uncertainty = Math.Max(NoisePercent, 100 * Math.Abs(d23) / l3);
            p = null; // differences at the noise level: no meaningful order
        }
        else if (p is >= 0.5 and <= 4)
        {
            estimate = l3 - d23 / (Math.Pow(1 / Ratio, p.Value) - 1);
            uncertainty = Math.Max(NoisePercent, 100 * Math.Abs(estimate - l3) / estimate);
        }
        else if (steady)
        {
            // Order below 0.5 (or negative): the steps do not shrink, so there is nothing to extrapolate — but the run is
            // at least as far off as the finest level.
            return new ConvergenceResult(levels, ConvergenceKind.StillChanging, l3, Math.Max(spread, NoisePercent), p, 100 * (l1 / l3 - 1));
        }
        else
        {
            return new ConvergenceResult(levels, ConvergenceKind.Uncertain, l3, Math.Max(spread, NoisePercent), null, null);
        }

        double err = 100 * (l1 / estimate - 1);
        return new ConvergenceResult(levels, Math.Abs(err) <= ConvergedPercent ? ConvergenceKind.Converged : ConvergenceKind.NotConverged, estimate, uncertainty, p, err);
    }
}
