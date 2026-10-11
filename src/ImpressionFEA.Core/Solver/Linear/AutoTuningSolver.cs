using System.Diagnostics;

namespace ImpressionFEA.Core.Solver.Linear;

/// <summary>
/// Auto-selection by measurement: on the first factorisation of a run both the GPU (cuDSS) and the CPU (MKL PARDISO)
/// backends factorise the actual system, the faster one is kept and the other released. FP64 throughput varies
/// enormously between GPUs (workstation/consumer cards run double precision at 1/32–1/64 rate) and CPUs range from 4
/// to 64+ cores, so measuring is the only reliable rule.
/// </summary>
public sealed class AutoTuningSolver : ISpdSolver
{
    private readonly Action<string>? _log;
    private ISpdSolver? _gpu;
    private ISpdSolver? _cpu;
    private ISpdSolver? _chosen;
    private int _n;
    private int[] _colPtr = [];
    private int[] _rowIdx = [];

    public AutoTuningSolver(ISpdSolver gpu, ISpdSolver cpu, Action<string>? log)
    {
        _gpu = gpu;
        _cpu = cpu;
        _log = log;
    }

    public string Name => _chosen is null ? $"Auto ({_gpu?.Name} | {_cpu?.Name})" : $"{_chosen.Name} [auto-selected]";

    public LinearBackend Kind => _chosen?.Kind ?? LinearBackend.Auto;

    public long FactorNonZeros => _chosen?.FactorNonZeros ?? _cpu?.FactorNonZeros ?? 0;

    public void Analyze(int n, int[] colPtr, int[] rowIdx)
    {
        (_n, _colPtr, _rowIdx) = (n, colPtr, rowIdx);
        if (_chosen is not null) _chosen.Analyze(n, colPtr, rowIdx);
    }

    public void Factorize(double[] values)
    {
        if (_chosen is not null)
        {
            _chosen.Factorize(values);
            return;
        }

        double TimeIt(ISpdSolver s)
        {
            s.Analyze(_n, _colPtr, _rowIdx);
            s.Factorize(values); // warm-up (allocations, kernel loading)
            var sw = Stopwatch.StartNew();
            s.Factorize(values);
            return sw.Elapsed.TotalMilliseconds;
        }

        double tg = double.PositiveInfinity, tc = double.PositiveInfinity;
        try { tg = TimeIt(_gpu!); }
        catch (SimulationException ex) { _log?.Invoke("  auto-tune: GPU factorisation failed (" + ex.Message + ")"); }
        try { tc = TimeIt(_cpu!); }
        catch (SimulationException ex) { _log?.Invoke("  auto-tune: CPU factorisation failed (" + ex.Message + ")"); }
        if (double.IsPositiveInfinity(tg) && double.IsPositiveInfinity(tc))
            throw new SolverDivergenceException("Neither the GPU nor the CPU sparse solver could factorise the system.");

        bool gpuWins = tg < tc;
        _chosen = gpuWins ? _gpu : _cpu;
        (gpuWins ? _cpu : _gpu)!.Dispose();
        _log?.Invoke(FormattableString.Invariant($"  auto-tune: GPU {tg:F0} ms vs CPU {tc:F0} ms per factorisation → {_chosen!.Name}"));
        _gpu = _cpu = null;
    }

    public void Solve(double[] b, double[] x) =>
        (_chosen ?? throw new InvalidOperationException("Factorize must be called first.")).Solve(b, x);

    public void Dispose()
    {
        _chosen?.Dispose();
        _gpu?.Dispose();
        _cpu?.Dispose();
    }
}
