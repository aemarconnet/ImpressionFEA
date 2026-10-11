namespace ImpressionFEA.Core.Solver.Linear;

/// <summary>Which sparse direct solver factorises the 3D system.</summary>
public enum LinearBackend
{
    /// <summary>Fastest available: NVIDIA GPU (cuDSS) → multithreaded CPU (MKL PARDISO) → managed CSparse.</summary>
    Auto,

    /// <summary>NVIDIA cuDSS on the GPU (requires an NVIDIA GPU and driver supporting CUDA 12).</summary>
    CudaGpu,

    /// <summary>Intel MKL PARDISO, multithreaded supernodal factorisation on all CPU cores.</summary>
    MklCpu,

    /// <summary>CSparse.NET — single-threaded managed code, always available.</summary>
    Managed,
}

/// <summary>
/// Sparse symmetric positive-definite direct solver. The matrix is supplied as a full (both triangles) compressed
/// sparse column pattern; each backend extracts the triangle it needs. The pattern is fixed between
/// <see cref="Analyze"/> calls; <see cref="Factorize"/> may be called repeatedly with new values.
/// </summary>
public interface ISpdSolver : IDisposable
{
    /// <summary>Backend and device description for the log, e.g. "cuDSS · NVIDIA RTX 4000 Ada".</summary>
    string Name { get; }

    LinearBackend Kind { get; }

    /// <summary>Symbolic analysis (ordering, elimination tree) of a full symmetric CSC pattern.</summary>
    void Analyze(int n, int[] colPtr, int[] rowIdx);

    /// <summary>Numeric factorisation; values are in the order of the full CSC pattern.</summary>
    void Factorize(double[] values);

    void Solve(double[] b, double[] x);

    /// <summary>Non-zeros in the factor, when the backend reports it (0 otherwise).</summary>
    long FactorNonZeros { get; }
}

/// <summary>Maps a full symmetric CSC pattern to the upper-triangle CSR pattern used by PARDISO and cuDSS.</summary>
internal sealed class UpperCsr
{
    public UpperCsr(int n, int[] colPtr, int[] rowIdx)
    {
        // Row i of the upper triangle = entries (i, j), j ≥ i = entries (j, i) of CSC column i with row j ≥ i.
        var rowPtr = new int[n + 1];
        for (int i = 0; i < n; i++)
        {
            for (int p = colPtr[i]; p < colPtr[i + 1]; p++)
            {
                if (rowIdx[p] >= i) rowPtr[i + 1]++;
            }
        }

        for (int i = 0; i < n; i++) rowPtr[i + 1] += rowPtr[i];
        var col = new int[rowPtr[n]];
        var map = new int[rowPtr[n]];
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            for (int p = colPtr[i]; p < colPtr[i + 1]; p++)
            {
                if (rowIdx[p] < i) continue;
                col[k] = rowIdx[p];
                map[k] = p;
                k++;
            }
        }

        N = n;
        RowPtr = rowPtr;
        ColIdx = col;
        Map = map;
        Values = new double[col.Length];
    }

    public int N { get; }

    public int[] RowPtr { get; }

    public int[] ColIdx { get; }

    public int[] Map { get; }

    public double[] Values { get; }

    public void Gather(double[] full)
    {
        for (int k = 0; k < Map.Length; k++) Values[k] = full[Map[k]];
    }
}
