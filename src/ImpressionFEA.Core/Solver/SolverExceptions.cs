namespace ImpressionFEA.Core.Solver;

/// <summary>Base class for all simulation failures surfaced to the operator.</summary>
public class SimulationException : Exception
{
    public SimulationException() { }

    public SimulationException(string message) : base(message) { }

    public SimulationException(string message, Exception inner) : base(message, inner) { }
}

public sealed class InputValidationException : SimulationException
{
    public InputValidationException(IReadOnlyList<string> errors)
        : base("Invalid simulation input: " + string.Join("; ", errors)) => Errors = errors;

    public IReadOnlyList<string> Errors { get; }
}

public sealed class SolverDivergenceException : SimulationException
{
    public SolverDivergenceException(string message) : base(message) { }
}

public sealed class MeshDistortionException : SimulationException
{
    public MeshDistortionException(string message) : base(message) { }
}

public sealed class MatrixNotPositiveDefiniteException : SimulationException
{
    public MatrixNotPositiveDefiniteException(int row, double pivot)
        : base(FormattableString.Invariant($"Stiffness matrix not positive definite at equation {row} (pivot {pivot:E3}). The billet is probably unrestrained.")) =>
        Row = row;

    public int Row { get; }
}

public sealed class EngineUnavailableException : SimulationException
{
    public EngineUnavailableException(string message) : base(message) { }
}
