namespace ImpressionFEA.Core.Solver.Linear;

/// <summary>Creates the requested (or fastest available) sparse SPD solver, falling back GPU → MKL → managed.</summary>
public static class SpdSolverFactory
{
    private static readonly Lazy<(bool Ok, string Detail)> GpuProbe = new(() =>
    {
        using var s = CudssSolver.TryCreate(out string? reason);
        return s is null ? (false, reason ?? "unavailable") : (true, s.Name);
    });

    private static readonly Lazy<(bool Ok, string Detail)> MklProbe = new(() =>
    {
        string? reason = MklPardisoSolver.Probe();
        return reason is null ? (true, new MklPardisoSolver().Name) : (false, reason);
    });

    /// <summary>One line per backend with availability, for logs and the UI.</summary>
    public static IReadOnlyList<string> Describe() =>
    [
        $"GPU  · {(GpuProbe.Value.Ok ? GpuProbe.Value.Detail : "unavailable (" + GpuProbe.Value.Detail + ")")}",
        $"CPU  · {(MklProbe.Value.Ok ? MklProbe.Value.Detail : "MKL unavailable (" + MklProbe.Value.Detail + ")")}",
        "Base · CSparse.NET (managed, 1 thread)",
    ];

    public static bool IsAvailable(LinearBackend backend) => backend switch
    {
        LinearBackend.CudaGpu => GpuProbe.Value.Ok,
        LinearBackend.MklCpu => MklProbe.Value.Ok,
        _ => true,
    };

    /// <summary>Best available backend for a request: Auto picks GPU, then MKL, then managed.</summary>
    public static LinearBackend Resolve(LinearBackend requested) => requested switch
    {
        LinearBackend.Auto => GpuProbe.Value.Ok ? LinearBackend.CudaGpu : MklProbe.Value.Ok ? LinearBackend.MklCpu : LinearBackend.Managed,
        LinearBackend.CudaGpu when !GpuProbe.Value.Ok => MklProbe.Value.Ok ? LinearBackend.MklCpu : LinearBackend.Managed,
        LinearBackend.MklCpu when !MklProbe.Value.Ok => LinearBackend.Managed,
        _ => requested,
    };

    public static ISpdSolver Create(LinearBackend requested, Action<string>? log = null)
    {
        if (requested == LinearBackend.Auto && GpuProbe.Value.Ok && MklProbe.Value.Ok)
        {
            var g = CudssSolver.TryCreate(out _);
            if (g is not null) return new AutoTuningSolver(g, new MklPardisoSolver(), log);
        }

        var kind = Resolve(requested);
        if (kind != requested && requested != LinearBackend.Auto)
            log?.Invoke($"WARNING      : {requested} backend unavailable — using {kind}.");
        switch (kind)
        {
            case LinearBackend.CudaGpu:
                var gpu = CudssSolver.TryCreate(out string? reason);
                if (gpu is not null) return gpu;
                log?.Invoke($"WARNING      : GPU solver failed to start ({reason}) — using CPU.");
                return MklProbe.Value.Ok ? new MklPardisoSolver() : new ManagedSpdSolver();
            case LinearBackend.MklCpu:
                return new MklPardisoSolver();
            default:
                return new ManagedSpdSolver();
        }
    }
}
