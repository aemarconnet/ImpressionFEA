using System.Diagnostics;
using ImpressionFEA.Core.Solver;
using ImpressionFEA.Core.ThreeD;

namespace ImpressionFEA.Core.Simulation;

/// <summary>Receives solver output. Called on the solver's background thread — implementations must marshal to the UI.</summary>
public interface ISimulationObserver
{
    void OnLog(string line);

    void OnStarted(double totalStroke, int nominalSteps);

    void OnStep(ForgingSnapshot snapshot);
}

/// <summary>Incremental forging solver driven by <see cref="NativeFemEngine"/> (2D and 3D share this contract).</summary>
public interface IForgingStepper
{
    double TotalStroke { get; }

    int NominalSteps { get; }

    int StepIndex { get; }

    bool IsFinished { get; }

    ForgingSnapshot Initial();

    ForgingSnapshot Step(CancellationToken ct);
}

public sealed record EngineProbe(bool Available, string Detail);

/// <summary>
/// A forging solver backend. The native engine is built in; external engines (FreeFem++, CalculiX…) can be added by
/// implementing this interface around a <see cref="System.Diagnostics.Process"/> wrapper.
/// </summary>
public interface ISimulationEngine
{
    string Name { get; }

    /// <summary>Checks that the engine's dependencies are present before a run.</summary>
    EngineProbe Probe();

    /// <summary>Runs a 2D batch simulation off the calling thread. Never throws for solver failures.</summary>
    Task<SimulationResult> RunAsync(SimulationConfig config, ISimulationObserver observer, CancellationToken cancellationToken);

    /// <summary>Runs a 3D batch simulation off the calling thread. Never throws for solver failures.</summary>
    Task<SimulationResult> RunAsync(SimulationConfig3D config, ISimulationObserver observer, CancellationToken cancellationToken);
}

public sealed class NativeFemEngine : ISimulationEngine
{
    /// <summary>Hard cap on increments (cut-backs included) to guarantee termination.</summary>
    public const int MaxIncrements = 4000;

    public string Name => "Native C# rigid-viscoplastic FEM (2D/3D)";

    public EngineProbe Probe()
    {
        // The 3D solver depends on the CSparse.NET sparse Cholesky assembly.
        try
        {
            var asm = typeof(CSparse.Double.Factorization.SparseCholesky).Assembly.GetName();
            return new(true, $"{Name} · in-process · .NET {Environment.Version} · {asm.Name} {asm.Version}");
        }
        catch (Exception ex) when (ex is System.IO.FileNotFoundException or System.IO.FileLoadException or TypeLoadException)
        {
            return new(false, "Sparse solver assembly CSparse.dll is missing or cannot be loaded: " + ex.Message);
        }
    }

    public Task<SimulationResult> RunAsync(SimulationConfig config, ISimulationObserver observer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(observer);
        return Task.Run(() => Run(() => new ForgingSolver(config, observer.OnLog), observer, cancellationToken), CancellationToken.None);
    }

    public Task<SimulationResult> RunAsync(SimulationConfig3D config, ISimulationObserver observer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(observer);
        return Task.Run(() => Run(() => new Forging3DSolver(config, observer.OnLog), observer, cancellationToken), CancellationToken.None);
    }

    private SimulationResult Run(Func<IForgingStepper> create, ISimulationObserver observer, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var snapshots = new List<ForgingSnapshot>();
        IForgingStepper? solver = null;

        SimulationResult Finish(SimulationStatus status, string? message)
        {
            if (message is not null) observer.OnLog((status == SimulationStatus.Failed ? "ERROR: " : "") + message);
            return new SimulationResult { Snapshots = snapshots, Status = status, Message = message, Elapsed = sw.Elapsed };
        }

        try
        {
            var probe = Probe();
            if (!probe.Available) throw new EngineUnavailableException(probe.Detail);
            observer.OnLog(probe.Detail);

            solver = create();
            if (solver.NominalSteps > MaxIncrements / 2)
                throw new InputValidationException([$"{solver.NominalSteps} increments requested; limit is {MaxIncrements / 2}. Increase the stroke increment."]);

            observer.OnStarted(solver.TotalStroke, solver.NominalSteps);
            var first = solver.Initial();
            snapshots.Add(first);
            observer.OnStep(first);

            while (!solver.IsFinished)
            {
                ct.ThrowIfCancellationRequested();
                if (solver.StepIndex >= MaxIncrements)
                    throw new SolverDivergenceException($"Increment limit ({MaxIncrements}) reached before die closure.");
                var snap = solver.Step(ct);
                snapshots.Add(snap);
                observer.OnStep(snap);
            }

            return Finish(SimulationStatus.Completed,
                $"Dies closed: {snapshots.Count - 1} increments in {sw.Elapsed.TotalSeconds:F2} s. Peak load {snapshots.Max(s => s.LoadTonnes):F2} t, final fill {snapshots[^1].FillPercent:F2} %.");
        }
        catch (OperationCanceledException)
        {
            return Finish(SimulationStatus.Aborted, $"Aborted by operator after {Math.Max(0, snapshots.Count - 1)} increments.");
        }
        catch (SimulationException ex)
        {
            return Finish(SimulationStatus.Failed, ex.Message);
        }
        catch (Exception ex) when (ex is ArithmeticException or InvalidOperationException or ArgumentException or OutOfMemoryException or IndexOutOfRangeException)
        {
            return Finish(SimulationStatus.Failed, $"Internal solver error ({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            (solver as IDisposable)?.Dispose();
        }
    }
}
