using System.Diagnostics;
using ImpressionFEA.Core.Solver.Linear;

namespace ImpressionFEA.Core.Tests;

/// <summary>Auto keeps the reproducible CPU solver unless the GPU is clearly faster.</summary>
public class AutoTuningSolverTests
{
    /// <summary>A backend whose factorisation takes a fixed time (busy wait: sleeps are too coarse on Windows).</summary>
    private sealed class Timed(string name, LinearBackend kind, double ms) : ISpdSolver
    {
        public string Name => name;

        public LinearBackend Kind => kind;

        public long FactorNonZeros => 0;

        public bool Disposed { get; private set; }

        public void Analyze(int n, int[] colPtr, int[] rowIdx) { }

        public void Factorize(double[] values)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalMilliseconds < ms) { }
        }

        public void Solve(double[] b, double[] x) => Array.Copy(b, x, b.Length);

        public void Dispose() => Disposed = true;
    }

    private static (LinearBackend Kind, string Log) Choose(double gpuMs, double cpuMs)
    {
        var log = new List<string>();
        using var auto = new AutoTuningSolver(new Timed("gpu", LinearBackend.CudaGpu, gpuMs), new Timed("cpu", LinearBackend.MklCpu, cpuMs), log.Add);
        auto.Analyze(1, [0, 1], [0]);
        auto.Factorize([1.0]);
        return (auto.Kind, string.Join(" ", log));
    }

    [Fact]
    public void CpuIsKeptWhenTheGpuIsOnlySlightlyFaster()
    {
        var (kind, log) = Choose(gpuMs: 30, cpuMs: 36); // GPU 1.2× faster: not enough
        Assert.Equal(LinearBackend.MklCpu, kind);
        Assert.Contains("reproducible", log, StringComparison.Ordinal);
    }

    [Fact]
    public void CpuIsKeptWhenItIsFaster() => Assert.Equal(LinearBackend.MklCpu, Choose(gpuMs: 40, cpuMs: 20).Kind);

    [Fact]
    public void GpuIsChosenWhenClearlyFaster() => Assert.Equal(LinearBackend.CudaGpu, Choose(gpuMs: 10, cpuMs: 40).Kind);
}
