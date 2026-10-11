using System.Runtime.InteropServices;
using System.Text;

namespace ImpressionFEA.Core.Solver.Linear;

/// <summary>
/// NVIDIA cuDSS sparse direct solver on the GPU (SPD Cholesky, upper-triangle CSR view). Requires an NVIDIA GPU, a
/// driver supporting CUDA 12, and the redistributable cudss64_0 / cublas64_12 / cublasLt64_12 / cudart64_12 DLLs
/// next to the application. Analysis (reordering + symbolic factorisation) runs once per sparsity pattern.
/// </summary>
public sealed partial class CudssSolver : ISpdSolver
{
    private const string Cudss = "cudss64_0";
    private const string Cudart = "cudart64_12";
    private const string Driver = "nvcuda";

    private const int PhaseAnalysis = 3, PhaseFactorization = 4, PhaseSolve = 1008;
    private const int MTypeSpd = 3, MViewUpper = 2, BaseZero = 0, LayoutColMajor = 0;
    private const int CudaR64F = 1, CudaR32I = 10;
    private const int HostToDevice = 1, DeviceToHost = 2;

    private IntPtr _handle, _config, _data, _matA, _matB, _matX;
    private IntPtr _dRow, _dCol, _dVal, _dB, _dX;
    private UpperCsr? _csr;
    private bool _analyzed;

    private CudssSolver(string device)
    {
        Device = device;
        Check(CudssCreate(out _handle), "cudssCreate");
        Check(CudssConfigCreate(out _config), "cudssConfigCreate");
    }

    public string Device { get; }

    public string Name => $"NVIDIA cuDSS (GPU · {Device})";

    public LinearBackend Kind => LinearBackend.CudaGpu;

    public long FactorNonZeros => 0;

    [LibraryImport(Driver, EntryPoint = "cuInit")] private static partial int CuInit(uint flags);
    [LibraryImport(Driver, EntryPoint = "cuDeviceGetCount")] private static partial int CuDeviceGetCount(out int count);
    [LibraryImport(Driver, EntryPoint = "cuDeviceGet")] private static partial int CuDeviceGet(out int device, int ordinal);
    [LibraryImport(Driver, EntryPoint = "cuDeviceGetName")] private static partial int CuDeviceGetName(byte[] name, int len, int device);
    [LibraryImport(Driver, EntryPoint = "cuDriverGetVersion")] private static partial int CuDriverGetVersion(out int version);

    [LibraryImport(Cudart, EntryPoint = "cudaMalloc")] private static partial int CudaMalloc(out IntPtr ptr, nuint size);
    [LibraryImport(Cudart, EntryPoint = "cudaFree")] private static partial int CudaFree(IntPtr ptr);
    [LibraryImport(Cudart, EntryPoint = "cudaMemcpy")] private static partial int CudaMemcpyToDevice(IntPtr dst, int[] src, nuint count, int kind);
    [LibraryImport(Cudart, EntryPoint = "cudaMemcpy")] private static partial int CudaMemcpyToDevice(IntPtr dst, double[] src, nuint count, int kind);
    [LibraryImport(Cudart, EntryPoint = "cudaMemcpy")] private static partial int CudaMemcpyToHost(double[] dst, IntPtr src, nuint count, int kind);

    [LibraryImport(Cudss, EntryPoint = "cudssCreate")] private static partial int CudssCreate(out IntPtr handle);
    [LibraryImport(Cudss, EntryPoint = "cudssDestroy")] private static partial int CudssDestroy(IntPtr handle);
    [LibraryImport(Cudss, EntryPoint = "cudssConfigCreate")] private static partial int CudssConfigCreate(out IntPtr config);
    [LibraryImport(Cudss, EntryPoint = "cudssConfigDestroy")] private static partial int CudssConfigDestroy(IntPtr config);
    [LibraryImport(Cudss, EntryPoint = "cudssDataCreate")] private static partial int CudssDataCreate(IntPtr handle, out IntPtr data);
    [LibraryImport(Cudss, EntryPoint = "cudssDataDestroy")] private static partial int CudssDataDestroy(IntPtr handle, IntPtr data);
    [LibraryImport(Cudss, EntryPoint = "cudssMatrixCreateCsr")]
    private static partial int CudssMatrixCreateCsr(out IntPtr matrix, long nrows, long ncols, long nnz, IntPtr rowStart, IntPtr rowEnd,
        IntPtr colIndices, IntPtr values, int offsetType, int indexType, int valueType, int mtype, int mview, int indexBase);
    [LibraryImport(Cudss, EntryPoint = "cudssMatrixCreateDn")]
    private static partial int CudssMatrixCreateDn(out IntPtr matrix, long nrows, long ncols, long ld, IntPtr values, int valueType, int layout);
    [LibraryImport(Cudss, EntryPoint = "cudssMatrixDestroy")] private static partial int CudssMatrixDestroy(IntPtr matrix);
    [LibraryImport(Cudss, EntryPoint = "cudssExecute")]
    private static partial int CudssExecute(IntPtr handle, int phase, IntPtr config, IntPtr data, IntPtr a, IntPtr x, IntPtr b);

    /// <summary>Creates the solver if a CUDA device, the driver and the cuDSS runtime are usable; otherwise returns the reason.</summary>
    public static CudssSolver? TryCreate(out string? reason)
    {
        try
        {
            if (CuInit(0) != 0) { reason = "CUDA driver could not be initialised"; return null; }
            if (CuDeviceGetCount(out int count) != 0 || count == 0) { reason = "no CUDA device present"; return null; }
            CuDriverGetVersion(out int ver);
            if (ver < 12000) { reason = $"NVIDIA driver supports CUDA {ver / 1000}.{ver % 1000 / 10}; CUDA 12 is required"; return null; }
            CuDeviceGet(out int dev, 0);
            var name = new byte[256];
            CuDeviceGetName(name, name.Length, dev);
            string device = Encoding.ASCII.GetString(name).TrimEnd('\0');
            var s = new CudssSolver(device);
            // Self-test on a 2×2 SPD system (also loads cuBLAS / kernels up front).
            s.Analyze(2, [0, 2, 4], [0, 1, 0, 1]);
            s.Factorize([4, 1, 1, 3]);
            var x = new double[2];
            s.Solve([1, 2], x);
            if (Math.Abs(4 * x[0] + x[1] - 1) > 1e-9 || Math.Abs(x[0] + 3 * x[1] - 2) > 1e-9)
            {
                s.Dispose();
                reason = "cuDSS self-test gave a wrong answer";
                return null;
            }

            reason = null;
            return s;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SimulationException)
        {
            reason = ex.Message;
            return null;
        }
    }

    public void Analyze(int n, int[] colPtr, int[] rowIdx)
    {
        ReleasePattern();
        _csr = new UpperCsr(n, colPtr, rowIdx);
        long nnz = _csr.ColIdx.Length;
        Check(CudaMalloc(out _dRow, (nuint)(4 * (n + 1))), "cudaMalloc");
        Check(CudaMalloc(out _dCol, (nuint)(4 * nnz)), "cudaMalloc");
        Check(CudaMalloc(out _dVal, (nuint)(8 * nnz)), "cudaMalloc");
        Check(CudaMalloc(out _dB, (nuint)(8 * n)), "cudaMalloc");
        Check(CudaMalloc(out _dX, (nuint)(8 * n)), "cudaMalloc");
        Check(CudaMemcpyToDevice(_dRow, _csr.RowPtr, (nuint)(4 * (n + 1)), HostToDevice), "cudaMemcpy");
        Check(CudaMemcpyToDevice(_dCol, _csr.ColIdx, (nuint)(4 * nnz), HostToDevice), "cudaMemcpy");
        Check(CudaMemcpyToDevice(_dVal, _csr.Values, (nuint)(8 * nnz), HostToDevice), "cudaMemcpy");
        Check(CudssDataCreate(_handle, out _data), "cudssDataCreate");
        Check(CudssMatrixCreateCsr(out _matA, n, n, nnz, _dRow, IntPtr.Zero, _dCol, _dVal, CudaR32I, CudaR32I, CudaR64F, MTypeSpd, MViewUpper, BaseZero), "cudssMatrixCreateCsr");
        Check(CudssMatrixCreateDn(out _matB, n, 1, n, _dB, CudaR64F, LayoutColMajor), "cudssMatrixCreateDn");
        Check(CudssMatrixCreateDn(out _matX, n, 1, n, _dX, CudaR64F, LayoutColMajor), "cudssMatrixCreateDn");
        Check(CudssExecute(_handle, PhaseAnalysis, _config, _data, _matA, _matX, _matB), "cudss analysis");
        _analyzed = true;
    }

    public void Factorize(double[] values)
    {
        if (_csr is null || !_analyzed) throw new InvalidOperationException("Analyze must be called first.");
        _csr.Gather(values);
        Check(CudaMemcpyToDevice(_dVal, _csr.Values, (nuint)(8 * _csr.Values.Length), HostToDevice), "cudaMemcpy");
        Check(CudssExecute(_handle, PhaseFactorization, _config, _data, _matA, _matX, _matB), "cudss factorization");
    }

    public void Solve(double[] b, double[] x)
    {
        if (_csr is null) throw new InvalidOperationException("Factorize must be called first.");
        int n = _csr.N;
        Check(CudaMemcpyToDevice(_dB, b, (nuint)(8 * n), HostToDevice), "cudaMemcpy");
        Check(CudssExecute(_handle, PhaseSolve, _config, _data, _matA, _matX, _matB), "cudss solve");
        Check(CudaMemcpyToHost(x, _dX, (nuint)(8 * n), DeviceToHost), "cudaMemcpy");
        foreach (double v in x)
        {
            if (!double.IsFinite(v)) throw new MatrixNotPositiveDefiniteException(-1, double.NaN);
        }
    }

    private static void Check(int status, string what)
    {
        if (status != 0) throw new SolverDivergenceException($"{what} failed (status {status}).");
    }

    private void ReleasePattern()
    {
        if (_matA != IntPtr.Zero) CudssMatrixDestroy(_matA);
        if (_matB != IntPtr.Zero) CudssMatrixDestroy(_matB);
        if (_matX != IntPtr.Zero) CudssMatrixDestroy(_matX);
        if (_data != IntPtr.Zero) CudssDataDestroy(_handle, _data);
        foreach (var p in new[] { _dRow, _dCol, _dVal, _dB, _dX })
        {
            if (p != IntPtr.Zero) CudaFree(p);
        }

        _matA = _matB = _matX = _data = _dRow = _dCol = _dVal = _dB = _dX = IntPtr.Zero;
        _analyzed = false;
    }

    public void Dispose()
    {
        ReleasePattern();
        if (_config != IntPtr.Zero) CudssConfigDestroy(_config);
        if (_handle != IntPtr.Zero) CudssDestroy(_handle);
        _config = _handle = IntPtr.Zero;
    }
}
