using ImpressionFEA.Core.Mesh;
using ImpressionFEA.Core.ThreeD;

namespace ImpressionFEA.Core.Simulation;

public enum ResultField
{
    VonMisesStress,
    EffectiveStrain,
    EffectiveStrainRate,
    MeanStress,
}

public enum NodeContact : byte
{
    Free = 0,
    BottomDie = 1,
    TopDie = 2,
}

/// <summary>Telemetry and per-element fields after one ram increment (2D or 3D).</summary>
public abstract class ForgingSnapshot
{
    public required int Step { get; init; }

    public required double Time { get; init; }

    /// <summary>Ram travel from first contact, mm.</summary>
    public required double Stroke { get; init; }

    /// <summary>Remaining top-die offset above the closed position, mm.</summary>
    public required double DieGap { get; init; }

    /// <summary>Press load on the top die (full part), N.</summary>
    public required double LoadNewton { get; init; }

    public required double FillPercent { get; init; }

    public required double VolumeChangePercent { get; init; }

    public required int Iterations { get; init; }

    public required float[] VonMises { get; init; }

    public required float[] EffectiveStrain { get; init; }

    public required float[] EffectiveStrainRate { get; init; }

    public required float[] MeanStress { get; init; }

    public required NodeContact[] Contact { get; init; }

    /// <summary>Nodal material velocity of this increment, mm/s (x / radius r; y / axial z in 2D). Empty for the setup state.</summary>
    public float[] VelocityX { get; init; } = [];

    public float[] VelocityY { get; init; } = [];

    /// <summary>3D only: velocity along the press axis z, mm/s.</summary>
    public float[] VelocityZ { get; init; } = [];

    public bool HasVelocity => VelocityX.Length > 0;

    public double LoadTonnes => LoadNewton / 9806.65;

    public abstract int ElementCount { get; }

    public abstract int NodeCount { get; }

    public float[] Field(ResultField field) => field switch
    {
        ResultField.VonMisesStress => VonMises,
        ResultField.EffectiveStrain => EffectiveStrain,
        ResultField.EffectiveStrainRate => EffectiveStrainRate,
        ResultField.MeanStress => MeanStress,
        _ => VonMises,
    };
}

/// <summary>2D (axisymmetric / plane-strain) deformed configuration.</summary>
public sealed class StepSnapshot : ForgingSnapshot
{
    /// <summary>Mesh topology of this configuration (changes when the solver remeshes).</summary>
    public required QuadMesh Mesh { get; init; }

    public required float[] X { get; init; }

    public required float[] Y { get; init; }

    public override int ElementCount => Mesh.ElementCount;

    public override int NodeCount => Mesh.NodeCount;
}

/// <summary>3D deformed configuration of the modelled portion (half / quarter); the full part is its mirror image.</summary>
public sealed class Snapshot3D : ForgingSnapshot
{
    public required TetMesh Mesh { get; init; }

    public required float[] X { get; init; }

    public required float[] Y { get; init; }

    public required float[] Z { get; init; }

    public override int ElementCount => Mesh.ElementCount;

    public override int NodeCount => Mesh.NodeCount;
}

public enum SimulationStatus
{
    Completed,
    Aborted,
    Failed,
}

public sealed class SimulationResult
{
    public required IReadOnlyList<ForgingSnapshot> Snapshots { get; init; }

    public required SimulationStatus Status { get; init; }

    public string? Message { get; init; }

    public TimeSpan Elapsed { get; init; }
}
