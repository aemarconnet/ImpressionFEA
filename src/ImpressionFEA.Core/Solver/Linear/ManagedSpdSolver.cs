using CSparse;
using CSparse.Double;
using CSparse.Double.Factorization;

namespace ImpressionFEA.Core.Solver.Linear;

/// <summary>CSparse.NET up-looking Cholesky with AMD ordering (single-threaded, managed; always available).</summary>
public sealed class ManagedSpdSolver : ISpdSolver
{
    private SparseMatrix? _a;
    private double[] _values = [];
    private SparseCholesky? _chol;

    public string Name => "CSparse.NET (managed, 1 thread)";

    public LinearBackend Kind => LinearBackend.Managed;

    public long FactorNonZeros => _chol?.NonZerosCount ?? 0;

    public void Analyze(int n, int[] colPtr, int[] rowIdx)
    {
        _values = new double[rowIdx.Length];
        _a = new SparseMatrix(n, n, _values, rowIdx, colPtr);
        _chol = null;
    }

    public void Factorize(double[] values)
    {
        if (_a is null) throw new InvalidOperationException("Analyze must be called first.");
        Array.Copy(values, _values, _values.Length);
        if (_chol is null) _chol = SparseCholesky.Create(_a, ColumnOrdering.MinimumDegreeAtPlusA);
        else _chol.Refactorize(_a);
    }

    public void Solve(double[] b, double[] x)
    {
        if (_chol is null) throw new InvalidOperationException("Factorize must be called first.");
        _chol.Solve(b, x);
    }

    public void Dispose()
    {
        _chol = null;
        _a = null;
    }
}
