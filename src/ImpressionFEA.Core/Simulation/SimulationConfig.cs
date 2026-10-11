using ImpressionFEA.Core.Geometry;
using ImpressionFEA.Core.Materials;

namespace ImpressionFEA.Core.Simulation;

/// <summary>Complete, immutable input deck for one forging simulation. Units: mm, s, °C, MPa.</summary>
public sealed record SimulationConfig
{
    public required DieSet Die { get; init; }

    public required MaterialPreset Material { get; init; }

    /// <summary>Billet radius (axisymmetric) or half-width (plane strain), mm.</summary>
    public double BilletRadius { get; init; }

    public double BilletHeight { get; init; }

    /// <summary>Out-of-plane length for plane strain (mm). Ignored for axisymmetric dies.</summary>
    public double BilletLength { get; init; } = 100;

    /// <summary>Isothermal forging temperature, °C.</summary>
    public double Temperature { get; init; }

    /// <summary>Constant shear friction factor m (τ = m·k), 0 = frictionless, 1 = sticking.</summary>
    public double FrictionFactor { get; init; } = 0.3;

    /// <summary>Target element edge length h, mm.</summary>
    public double ElementSize { get; init; } = 1.5;

    /// <summary>Ram stroke per solution increment, mm.</summary>
    public double StrokeIncrement { get; init; } = 0.25;

    /// <summary>Ram (top die) speed, mm/s.</summary>
    public double RamSpeed { get; init; } = 50;

    public int MaxIterations { get; init; } = 60;

    /// <summary>Convergence tolerance on ‖Δv‖/‖v‖.</summary>
    public double Tolerance { get; init; } = 1e-5;

    public const int MaxElements = 20000;

    public int ElementsX => Math.Max(3, (int)Math.Round(BilletRadius / ElementSize));

    public int ElementsZ => Math.Max(3, (int)Math.Round(BilletHeight / ElementSize));

    public double TimeIncrement => StrokeIncrement / RamSpeed;

    public IReadOnlyList<string> Validate()
    {
        var e = new List<string>();
        if (!(BilletRadius > 0)) e.Add("Billet radius must be > 0 mm.");
        if (!(BilletHeight > 0)) e.Add("Billet height must be > 0 mm.");
        if (Die.IsPlaneStrain && !(BilletLength > 0)) e.Add("Billet length must be > 0 mm for a plane-strain die.");
        if (!(FrictionFactor >= 0 && FrictionFactor <= 1)) e.Add("Shear friction factor m must lie in [0, 1].");
        if (!(RamSpeed > 0)) e.Add("Ram speed must be > 0 mm/s.");
        if (!(ElementSize > 0)) e.Add("Element size h must be > 0 mm.");
        if (!(StrokeIncrement > 0)) e.Add("Stroke increment must be > 0 mm.");
        if (!double.IsFinite(Temperature)) e.Add("Temperature must be a finite number.");
        if (e.Count > 0) return e;

        if (ElementSize > BilletRadius / 3 || ElementSize > BilletHeight / 3)
            e.Add("Element size h is too coarse: need at least 3 elements across the billet.");
        long elements = (long)ElementsX * ElementsZ;
        if (elements > MaxElements)
            e.Add($"Mesh too fine: {elements:N0} elements (limit {MaxElements:N0}). Increase h.");
        if (StrokeIncrement > 0.5 * ElementSize)
            e.Add("Stroke increment must not exceed h/2 (contact and mesh-distortion control).");
        if (BilletRadius >= Die.CavityOuterRadius)
            e.Add($"Billet radius must be smaller than the impression radius ({Die.CavityOuterRadius:F1} mm).");
        double gap = Die.InitialGap(BilletRadius, BilletHeight);
        if (!(gap > StrokeIncrement))
            e.Add($"Billet too short for this die: initial die gap {gap:F2} mm leaves no forging stroke.");
        if (Material.CAt(Temperature) <= 0 || !double.IsFinite(Material.CAt(Temperature)))
            e.Add("Flow stress is not positive at this temperature.");
        return e;
    }
}
