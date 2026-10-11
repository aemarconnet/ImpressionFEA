namespace ImpressionFEA.Core.Solver;

/// <summary>
/// Symmetric positive-definite band matrix, lower band stored row-wise, factorised in place by Cholesky (L·Lᵀ).
/// Cost O(N·b²) — ideal for the narrow bands produced by structured meshes.
/// </summary>
public sealed class SymmetricBandMatrix
{
    private readonly double[] _a;
    private readonly int _w;

    public SymmetricBandMatrix(int n, int halfBandwidth)
    {
        if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n));
        N = n;
        Bandwidth = Math.Min(halfBandwidth, n - 1);
        _w = Bandwidth + 1;
        _a = new double[(long)n * _w];
    }

    public int N { get; }

    public int Bandwidth { get; }

    public bool IsFactorized { get; private set; }

    private int Index(int i, int j) => i * _w + (j - i + Bandwidth);

    public void Clear()
    {
        Array.Clear(_a);
        IsFactorized = false;
    }

    /// <summary>A(i,j) += v (and implicitly A(j,i)); entries outside the band are rejected.</summary>
    public void Add(int i, int j, double v)
    {
        if (j > i) (i, j) = (j, i);
        if (i - j > Bandwidth) throw new InvalidOperationException($"Entry ({i},{j}) lies outside the bandwidth {Bandwidth}.");
        _a[Index(i, j)] += v;
    }

    public double Get(int i, int j)
    {
        if (j > i) (i, j) = (j, i);
        return i - j > Bandwidth ? 0.0 : _a[Index(i, j)];
    }

    /// <summary>Imposes a homogeneous Dirichlet condition on DOF i (zero row/column, unit diagonal).</summary>
    public void ConstrainDof(int i)
    {
        int j0 = Math.Max(0, i - Bandwidth);
        for (int j = j0; j < i; j++) _a[Index(i, j)] = 0;
        int k1 = Math.Min(N - 1, i + Bandwidth);
        for (int k = i + 1; k <= k1; k++) _a[Index(k, i)] = 0;
        _a[Index(i, i)] = 1.0;
    }

    /// <summary>In-place band Cholesky. Throws <see cref="MatrixNotPositiveDefiniteException"/> on a non-positive pivot.</summary>
    public void Factorize()
    {
        int b = Bandwidth;
        var a = _a;
        for (int i = 0; i < N; i++)
        {
            int j0 = Math.Max(0, i - b);
            int rowI = i * _w - i + b;
            for (int j = j0; j <= i; j++)
            {
                int rowJ = j * _w - j + b;
                int k0 = Math.Max(j0, j - b);
                double s = a[rowI + j];
                for (int k = k0; k < j; k++) s -= a[rowI + k] * a[rowJ + k];
                if (j == i)
                {
                    if (!(s > 0) || double.IsNaN(s))
                        throw new MatrixNotPositiveDefiniteException(i, s);
                    a[rowI + i] = Math.Sqrt(s);
                }
                else
                {
                    a[rowI + j] = s / a[rowJ + j];
                }
            }
        }

        IsFactorized = true;
    }

    /// <summary>Solves A·x = b in place using the Cholesky factor.</summary>
    public void Solve(double[] rhs)
    {
        ArgumentNullException.ThrowIfNull(rhs);
        if (!IsFactorized) throw new InvalidOperationException("Matrix must be factorised before solving.");
        int b = Bandwidth;
        for (int i = 0; i < N; i++)
        {
            int row = i * _w - i + b;
            double s = rhs[i];
            for (int k = Math.Max(0, i - b); k < i; k++) s -= _a[row + k] * rhs[k];
            rhs[i] = s / _a[row + i];
        }

        for (int i = N - 1; i >= 0; i--)
        {
            int row = i * _w - i + b;
            double xi = rhs[i] / _a[row + i];
            rhs[i] = xi;
            for (int k = Math.Max(0, i - b); k < i; k++) rhs[k] -= _a[row + k] * xi;
        }
    }
}
