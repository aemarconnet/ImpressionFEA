using ImpressionFEA.Core.Materials;

namespace ImpressionFEA.Core.ThreeD;

/// <summary>Input deck for a 3D forging simulation. Units: mm, s, °C, MPa.</summary>
public sealed record SimulationConfig3D
{
    public required DieSet3D Die { get; init; }

    public required MaterialPreset Material { get; init; }

    public double BilletRadius { get; init; }

    /// <summary>Billet height (vertical cylinder) or length (horizontal bar), mm.</summary>
    public double BilletDimension { get; init; }

    public double Temperature { get; init; }

    public double FrictionFactor { get; init; } = 0.3;

    /// <summary>Target tetrahedron edge length h, mm.</summary>
    public double ElementSize { get; init; } = 3;

    public double StrokeIncrement { get; init; } = 0.5;

    public double RamSpeed { get; init; } = 50;

    public int MaxIterations { get; init; } = 70;

    public double Tolerance { get; init; } = 1e-4;

    /// <summary>Sparse direct solver: Auto = NVIDIA GPU (cuDSS) → multithreaded CPU (MKL PARDISO) → managed.</summary>
    public Solver.Linear.LinearBackend LinearBackend { get; init; } = Solver.Linear.LinearBackend.Auto;

    public const int MaxNodes = 12000;

    /// <summary>Approximate node count of the modelled portion (BCC lattice: ≈ 2 nodes per h³).</summary>
    public int EstimatedNodes => ElementSize > 0
        ? (int)(2.2 * Die.BilletVolumeFor(BilletRadius, BilletDimension) / Die.SymmetryFactor / Math.Pow(ElementSize, 3))
        : int.MaxValue;

    public double TimeIncrement => StrokeIncrement / RamSpeed;

    public IReadOnlyList<string> Validate()
    {
        var e = new List<string>();
        if (Die.IsImported) return ValidateImported(e);
        if (!(BilletRadius > 0)) e.Add("Billet radius must be > 0 mm.");
        if (!(BilletDimension > 0)) e.Add(Die.Billet == BilletShape.VerticalCylinder ? "Billet height must be > 0 mm." : "Billet length must be > 0 mm.");
        if (!(FrictionFactor >= 0 && FrictionFactor <= 1)) e.Add("Shear friction factor m must lie in [0, 1].");
        if (!(RamSpeed > 0)) e.Add("Ram speed must be > 0 mm/s.");
        if (!(ElementSize > 0)) e.Add("Element size h must be > 0 mm.");
        if (!(StrokeIncrement > 0)) e.Add("Stroke increment must be > 0 mm.");
        if (!double.IsFinite(Temperature)) e.Add("Temperature must be a finite number.");
        if (e.Count > 0) return e;

        if (ElementSize > BilletRadius / 2.5) e.Add("Element size h is too coarse for the billet radius (need R ≥ 2.5·h).");
        if (EstimatedNodes > MaxNodes)
            e.Add($"3D mesh too fine: ≈{EstimatedNodes:N0} nodes (limit {MaxNodes:N0}). Increase h.");
        if (StrokeIncrement > 0.4 * ElementSize)
            e.Add("Stroke increment must not exceed 0.4·h (contact and mesh-distortion control).");
        var (_, gap) = Die.Place(BilletRadius, BilletDimension);
        if (!(gap > StrokeIncrement))
            e.Add($"Billet too small for this die: initial die gap {gap:F2} mm leaves no forging stroke.");
        if (Die.Billet == BilletShape.HorizontalBar && BilletDimension > Die.XMax - Die.XMin)
            e.Add("Bar is longer than the die.");
        return e;
    }

    private List<string> ValidateImported(List<string> e)
    {
        var b = Die.ImportedBillet!;
        double thin = Math.Min(b.Max.X - b.Min.X, Math.Min(b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z));
        if (!(FrictionFactor >= 0 && FrictionFactor <= 1)) e.Add("Shear friction factor m must lie in [0, 1].");
        if (!(RamSpeed > 0)) e.Add("Ram speed must be > 0 mm/s.");
        if (!(ElementSize > 0)) e.Add("Element size h must be > 0 mm.");
        if (!(StrokeIncrement > 0)) e.Add("Stroke increment must be > 0 mm.");
        if (!double.IsFinite(Temperature)) e.Add("Temperature must be a finite number.");
        if (e.Count > 0) return e;
        if (ElementSize > thin / 4) e.Add($"Element size h is too coarse for the workpiece (smallest extent {thin:F1} mm, need ≥ 4·h).");
        if (EstimatedNodes > MaxNodes)
            e.Add($"3D mesh too fine: ≈{EstimatedNodes:N0} nodes (limit {MaxNodes:N0}). Increase h.");
        if (StrokeIncrement > 0.4 * ElementSize)
            e.Add("Stroke increment must not exceed 0.4·h (contact and mesh-distortion control).");
        try
        {
            var (_, gap) = Die.Place(0, 0);
            if (!(gap > StrokeIncrement))
                e.Add($"The workpiece leaves no forging stroke: the top die touches the bottom die {gap:F2} mm after first contact.");
        }
        catch (InvalidOperationException ex)
        {
            e.Add(ex.Message);
        }

        return e;
    }
}
