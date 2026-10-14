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

    // ---------------------------------------------------------------- advanced (robustness) settings

    /// <summary>Keep a reduced stroke increment after a cutback and grow it back (×1.5 per clean increment) to Δs.</summary>
    public bool AutoStep { get; init; } = true;

    /// <summary>Halvings of the increment tried before remeshing (Δs / 2^n is the smallest increment).</summary>
    public int MaxCutbacks { get; init; } = 5;

    /// <summary>Local refinement depth: 0 = uniform mesh; n allows elements down to h / 2^n in thin flash and around trouble spots.</summary>
    public int RefinementLevels { get; init; } = 1;

    /// <summary>Also refine wherever the closed die gap is thin (flash lands) — more accurate flash, more nodes and time.</summary>
    public bool RefineThinFlash { get; init; }

    /// <summary>Remesh when the worst element quality (normalised, 1 = regular tet) drops below this.</summary>
    public double RemeshQuality { get; init; } = 0.12;

    /// <summary>Node budget of the modelled portion (mesh-size and refinement limit).</summary>
    public int MaxNodeCount { get; init; } = DefaultMaxNodes;

    public const int DefaultMaxNodes = 12000;

    /// <summary>Default node budget (kept for callers that size meshes before a config exists).</summary>
    public const int MaxNodes = DefaultMaxNodes;

    /// <summary>Default target node count of the automatic element size.</summary>
    public const int DefaultTargetNodes = 8000;

    /// <summary>Nodes per h³ of workpiece volume (BCC lattice of the isosurface stuffing).</summary>
    public const double NodesPerCubicElement = 2.2;

    /// <summary>Workpiece volume of the modelled portion (half / quarter for symmetric presets), mm³.</summary>
    public double ModelledVolume => Math.Abs(Die.BilletVolumeFor(BilletRadius, BilletDimension)) / Die.SymmetryFactor;

    /// <summary>Approximate node count of the modelled portion (BCC lattice: ≈ 2 nodes per h³).</summary>
    public int EstimatedNodes => ElementSize > 0
        ? (int)Math.Min(int.MaxValue, NodesPerCubicElement * ModelledVolume / Math.Pow(ElementSize, 3))
        : int.MaxValue;

    /// <summary>Element size giving about <paramref name="nodes"/> nodes in the modelled portion, mm.</summary>
    public double ElementSizeForNodes(int nodes) => nodes > 0 ? Math.Cbrt(NodesPerCubicElement * ModelledVolume / nodes) : double.PositiveInfinity;

    /// <summary>Largest valid h: 4 elements across the smallest extent of an imported workpiece, R / 2.5 for a preset billet.</summary>
    public double CoarsestElementSize
    {
        get
        {
            if (!Die.IsImported) return BilletRadius / 2.5;
            var b = Die.ImportedBillet!;
            return Math.Min(b.Max.X - b.Min.X, Math.Min(b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z)) / 4;
        }
    }

    /// <summary>Smallest valid h: the node budget.</summary>
    public double FinestElementSize => ElementSizeForNodes(MaxNodeCount);

    /// <summary>
    /// Automatic element size: about <paramref name="targetNodes"/> nodes, kept between <see cref="FinestElementSize"/>
    /// and <see cref="CoarsestElementSize"/> (the coarse limit wins when the two cross) and rounded to a tidy value.
    /// </summary>
    public double AutoElementSize(int targetNodes = DefaultTargetNodes)
    {
        double lo = FinestElementSize, hi = CoarsestElementSize;
        double h = ElementSizeForNodes(targetNodes);
        if (!double.IsFinite(h) || !(hi > 0)) return double.IsFinite(hi) && hi > 0 ? hi : 1;
        return Tidy(Math.Min(Math.Max(h, lo), hi), lo, hi);
    }

    /// <summary>
    /// Node count of the initial mesh at element size <paramref name="h"/> (runs the isosurface stuffing on the seated
    /// workpiece; no solver set-up), or null when the workpiece cannot be placed.
    /// </summary>
    public int? InitialNodeCount(double h)
    {
        if (!(h > 0)) return null;
        try
        {
            var (seat, _) = Die.Place(BilletRadius, BilletDimension);
            var (min, max) = Die.BilletBounds(BilletRadius, BilletDimension, seat);
            return IsosurfaceStuffing.Stuff(Die.BilletInside(BilletRadius, BilletDimension, seat), min, max, h, Die.SymmetricX, Die.SymmetricY).Points.Length;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// <see cref="AutoElementSize"/> corrected with the actual initial mesh: the volume estimate leaves out the extra
    /// nodes of the cut surface (15–40 %), so h is enlarged by up to two secant steps on the real node count. Stays
    /// within the same limits; falls back to the estimate when the workpiece cannot be meshed.
    /// </summary>
    public double CalibratedAutoElementSize(int targetNodes = DefaultTargetNodes)
    {
        double h = AutoElementSize(targetNodes);
        double lo = FinestElementSize, hi = CoarsestElementSize;
        if (!(h < hi) || targetNodes <= 0) return h;
        for (int pass = 0; pass < 2; pass++)
        {
            if (InitialNodeCount(h) is not { } n || n <= 0) return h;
            double ratio = (double)n / targetNodes;
            if (Math.Abs(ratio - 1) < 0.05) break;
            // N ≈ a/h³ + b/h²: an exponent of 2.8 between the two converges in one or two steps.
            double next = Tidy(Math.Min(Math.Max(h * Math.Pow(ratio, 1 / 2.8), lo), hi), lo, hi);
            if (next == h) break;
            h = next;
        }

        return h;
    }

    private static double Tidy(double h, double lo, double hi)
    {
        double step = h < 1 ? 0.01 : h < 10 ? 0.05 : 0.25;
        double r = Math.Round(h / step) * step;
        if (r > hi) r = Math.Floor(hi / step) * step;
        else if (r < lo) r = Math.Ceiling(lo / step) * step;
        return Math.Round(r > 0 ? r : h, 4);
    }

    /// <summary>Automatic stroke increment for element size <paramref name="h"/>: 0.2·h (half the 0.4·h limit).</summary>
    public static double AutoStrokeIncrement(double h) => Math.Max(0.01, Math.Round(0.2 * h, 2));

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

        if (ElementSize > CoarsestElementSize) e.Add("Element size h is too coarse for the billet radius (need R ≥ 2.5·h).");
        if (EstimatedNodes > MaxNodeCount)
            e.Add($"3D mesh too fine: ≈{EstimatedNodes:N0} nodes (limit {MaxNodeCount:N0}). Increase h or raise the node budget (Advanced).");
        if (FinestElementSize > CoarsestElementSize)
            e.Add($"No element size fits this workpiece: the node budget needs h ≥ {FinestElementSize:F2} mm, its shape needs h ≤ {CoarsestElementSize:F2} mm. Raise the node budget (Advanced) or use a smaller workpiece.");
        if (StrokeIncrement > 0.4 * ElementSize)
            e.Add("Stroke increment must not exceed 0.4·h (contact and mesh-distortion control).");
        AdvancedChecks(e);
        var (_, gap) = Die.Place(BilletRadius, BilletDimension);
        if (!(gap > StrokeIncrement))
            e.Add($"Billet too small for this die: initial die gap {gap:F2} mm leaves no forging stroke.");
        if (Die.Billet == BilletShape.HorizontalBar && BilletDimension > Die.XMax - Die.XMin)
            e.Add("Bar is longer than the die.");
        return e;
    }

    private void AdvancedChecks(List<string> e)
    {
        if (MaxCutbacks is < 0 or > 8) e.Add("Max cutbacks must be 0–8.");
        if (RefinementLevels is < 0 or > 3) e.Add("Refinement levels must be 0–3.");
        if (!(RemeshQuality is >= 0.02 and <= 0.4)) e.Add("Remesh quality threshold must be 0.02–0.4.");
        if (MaxNodeCount is < 1000 or > 60000) e.Add("Node budget must be 1,000–60,000.");
        if (MaxIterations is < 5 or > 500) e.Add("Max iterations per increment must be 5–500.");
        if (!(Tolerance is > 0 and <= 1e-2)) e.Add("Convergence tolerance must be in (0, 0.01].");
    }

    private List<string> ValidateImported(List<string> e)
    {
        double thin = 4 * CoarsestElementSize;
        if (!(FrictionFactor >= 0 && FrictionFactor <= 1)) e.Add("Shear friction factor m must lie in [0, 1].");
        if (!(RamSpeed > 0)) e.Add("Ram speed must be > 0 mm/s.");
        if (!(ElementSize > 0)) e.Add("Element size h must be > 0 mm.");
        if (!(StrokeIncrement > 0)) e.Add("Stroke increment must be > 0 mm.");
        if (!double.IsFinite(Temperature)) e.Add("Temperature must be a finite number.");
        if (e.Count > 0) return e;
        if (ElementSize > CoarsestElementSize) e.Add($"Element size h is too coarse for the workpiece (smallest extent {thin:F1} mm, need ≥ 4·h).");
        if (EstimatedNodes > MaxNodeCount)
            e.Add($"3D mesh too fine: ≈{EstimatedNodes:N0} nodes (limit {MaxNodeCount:N0}). Increase h or raise the node budget (Advanced).");
        if (FinestElementSize > CoarsestElementSize)
            e.Add($"No element size fits this workpiece: the node budget needs h ≥ {FinestElementSize:F2} mm, its shape needs h ≤ {CoarsestElementSize:F2} mm. Raise the node budget (Advanced) or use a smaller workpiece.");
        if (StrokeIncrement > 0.4 * ElementSize)
            e.Add("Stroke increment must not exceed 0.4·h (contact and mesh-distortion control).");
        AdvancedChecks(e);
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
