using System.Runtime.InteropServices;

namespace ImpressionFEA.Core.Solver.Linear;

/// <summary>
/// Intel MKL PARDISO (real symmetric positive definite, mtype 2): parallel supernodal Cholesky with METIS nested
/// dissection, multithreaded over all physical cores. Native library: mkl_rt.3.dll (NuGet intelmkl.redist.win-x64).
/// </summary>
public sealed partial class MklPardisoSolver : ISpdSolver
{
    private const string Lib = "mkl_rt.3";
    private const int MType = 2; // real SPD

    private readonly IntPtr[] _pt = new IntPtr[64];
    private readonly int[] _iparm = new int[64];
    private UpperCsr? _csr;
    private bool _analyzed;
    private long _factorNnz;

    public MklPardisoSolver()
    {
        _iparm[0] = 1;   // user-supplied iparm
        _iparm[1] = 3;   // parallel METIS nested dissection
        _iparm[7] = 0;   // no iterative refinement
        _iparm[9] = 8;   // pivot perturbation 1e-8
        _iparm[17] = -1; // report factor non-zeros
        _iparm[23] = 1;  // two-level parallel factorisation
        _iparm[26] = 0;  // no matrix checker
        _iparm[34] = 1;  // zero-based indexing
        Threads = MklGetMaxThreads();
    }

    public int Threads { get; }

    public string Name => $"Intel MKL PARDISO (CPU, {Threads} threads)";

    public LinearBackend Kind => LinearBackend.MklCpu;

    public long FactorNonZeros => _factorNnz;

    [LibraryImport(Lib, EntryPoint = "pardiso")]
    private static partial void Pardiso(IntPtr[] pt, ref int maxfct, ref int mnum, ref int mtype, ref int phase, ref int n,
        double[] a, int[] ia, int[] ja, int[]? perm, ref int nrhs, int[] iparm, ref int msglvl, double[]? b, double[]? x, ref int error);

    [LibraryImport(Lib, EntryPoint = "MKL_Get_Max_Threads")]
    private static partial int MklGetMaxThreads();

    /// <summary>Loads MKL and runs a 2×2 SPD solve; returns null on success or the reason it is unusable.</summary>
    public static string? Probe()
    {
        try
        {
            using var s = new MklPardisoSolver();
            s.Analyze(2, [0, 2, 4], [0, 1, 0, 1]);
            s.Factorize([4, 1, 1, 3]);
            var x = new double[2];
            s.Solve([1, 2], x);
            return Math.Abs(4 * x[0] + x[1] - 1) < 1e-9 && Math.Abs(x[0] + 3 * x[1] - 2) < 1e-9 ? null : "PARDISO self-test gave a wrong answer";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SimulationException)
        {
            return ex.Message;
        }
    }

    public void Analyze(int n, int[] colPtr, int[] rowIdx)
    {
        Release();
        _csr = new UpperCsr(n, colPtr, rowIdx);
        Run(11, null, null);
        _analyzed = true;
    }

    public void Factorize(double[] values)
    {
        if (_csr is null || !_analyzed) throw new InvalidOperationException("Analyze must be called first.");
        _csr.Gather(values);
        Run(22, null, null);
        _factorNnz = _iparm[17];
    }

    public void Solve(double[] b, double[] x)
    {
        if (_csr is null) throw new InvalidOperationException("Factorize must be called first.");
        Run(33, b, x);
    }

    private void Run(int phase, double[]? b, double[]? x)
    {
        var c = _csr!;
        int maxfct = 1, mnum = 1, mtype = MType, n = c.N, nrhs = 1, msglvl = 0, error = 0;
        Pardiso(_pt, ref maxfct, ref mnum, ref mtype, ref phase, ref n, c.Values, c.RowPtr, c.ColIdx, null, ref nrhs, _iparm, ref msglvl, b, x, ref error);
        if (error != 0)
        {
            throw error == -4
                ? new MatrixNotPositiveDefiniteException(-1, double.NaN)
                : new SolverDivergenceException($"MKL PARDISO phase {phase} failed with error {error}.");
        }
    }

    private void Release()
    {
        if (!_analyzed || _csr is null) return;
        int phase = -1, maxfct = 1, mnum = 1, mtype = MType, n = _csr.N, nrhs = 1, msglvl = 0, error = 0;
        Pardiso(_pt, ref maxfct, ref mnum, ref mtype, ref phase, ref n, _csr.Values, _csr.RowPtr, _csr.ColIdx, null, ref nrhs, _iparm, ref msglvl, null, null, ref error);
        _analyzed = false;
        Array.Clear(_pt);
    }

    public void Dispose() => Release();
}
