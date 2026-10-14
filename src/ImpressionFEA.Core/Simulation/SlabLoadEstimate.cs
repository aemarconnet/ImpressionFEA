namespace ImpressionFEA.Core.Simulation;

/// <summary>Cavity and flash parts of a slab-method forging load, N (or N per mm of length for plane strain).</summary>
public readonly record struct SlabLoad(double Cavity, double Flash)
{
    public double Total => Cavity + Flash;
}

/// <summary>How the metal in the cavity flows at the end of the stroke (Altan et al. 2005, Fig. 15.6 / Table 15.1).</summary>
public enum CavityFlow
{
    /// <summary>The metal slides along the die faces of a cavity of height H with friction m (Eqs 14.9 / 14.11).</summary>
    Sliding,

    /// <summary>
    /// The metal shears along a fictitious disk as thick as the flash, with sticking friction (m = 1) on it: the
    /// cavity term of Eqs 14.9 / 14.11 with H → t and m → 1 (Table 15.1, Fig. 15.6a; wide, shallow cavities).
    /// </summary>
    DiskShearing,
}

/// <summary>
/// Simplified slab method for the end-of-stroke load of a closed-die forging with flash (Subramanian &amp; Altan, 1980;
/// as presented by Altan, Ngaile &amp; Shen, "Cold and Hot Forging", ASM 2005, §14.5.2). The cavity is idealised as a
/// rectangle (radius or half-width <c>r</c>, height <c>H</c>) feeding a flash land of thickness <c>t</c> and width
/// <c>w</c>; constant shear friction τ = m·σ̄/√3 acts on every die surface, and the flash may have its own flow stress
/// (it is thinner, colder and strains faster). Units: mm, MPa, N.
/// <para>
/// Flash: the pressure rises linearly from the yield stress at the land exit to the cavity entrance,
/// σ_e = σ_f (1 + 2 m w / (√3 t)) axisymmetric, σ_e = (2/√3) σ_f (1 + m w / t) plane strain.
/// Cavity: the pressure rises from σ_e at the entrance to the centre by 2 m σ_c / (√3 H) per mm.
/// </para>
/// </summary>
public static class SlabLoadEstimate
{
    private static readonly double Sqrt3 = Math.Sqrt(3);

    /// <summary>Axisymmetric section: cavity radius r, cavity height H, flash thickness t, flash-land width w.</summary>
    public static SlabLoad Axisymmetric(double r, double cavityHeight, double t, double w, double m, double cavityStress, double flashStress,
        CavityFlow flow = CavityFlow.Sliding)
    {
        Check(r, cavityHeight, t, w, m);
        double big = r + w;
        double entrance = flashStress * (1 + 2 * m * w / (Sqrt3 * t));
        // ∫ 2πρ p dρ over the land, p = σ_f [1 + 2m (R − ρ) / (√3 t)].
        double flash = 2 * Math.PI * flashStress * ((1 + 2 * m * big / (Sqrt3 * t)) * (big * big - r * r) / 2
            - 2 * m / (Sqrt3 * t) * (big * big * big - r * r * r) / 3);
        // ∫ 2πρ p dρ over the cavity, p = σ_e + 2 m σ_c (r − ρ) / (√3 H).
        var (mc, hc) = CavityShear(flow, m, cavityHeight, t);
        double cavity = Math.PI * r * r * entrance + 2 * Math.PI * mc * cavityStress * r * r * r / (3 * Sqrt3 * hc);
        return new SlabLoad(cavity, flash);
    }

    /// <summary>Plane-strain section of length <paramref name="length"/>: cavity half-width r (width 2r), height H, flash t × w on both sides.</summary>
    public static SlabLoad PlaneStrain(double r, double cavityHeight, double t, double w, double m, double cavityStress, double flashStress, double length,
        CavityFlow flow = CavityFlow.Sliding)
    {
        Check(r, cavityHeight, t, w, m);
        double width = 2 * r;
        double entrance = 2 / Sqrt3 * flashStress * (1 + m * w / t);
        // Two lands, mean pressure (2/√3) σ_f (1 + m w / (2t)).
        double flash = 2 * w * flashStress / Sqrt3 * (2 + m * w / t);
        // Mean cavity pressure σ_e + m σ_c (2r) / (2√3 H).
        var (mc, hc) = CavityShear(flow, m, cavityHeight, t);
        double cavity = width * (entrance + mc * width * cavityStress / (2 * Sqrt3 * hc));
        return new SlabLoad(cavity * length, flash * length);
    }

    /// <summary>Friction factor and slab height of the cavity term for a flow mode.</summary>
    private static (double M, double H) CavityShear(CavityFlow flow, double m, double cavityHeight, double t) =>
        flow == CavityFlow.DiskShearing ? (1.0, t) : (m, cavityHeight);

    private static void Check(double r, double h, double t, double w, double m)
    {
        if (!(r > 0 && h > 0 && t > 0 && w >= 0)) throw new ArgumentOutOfRangeException(nameof(r), "Cavity and flash dimensions must be positive.");
        if (!(m >= 0 && m <= 1)) throw new ArgumentOutOfRangeException(nameof(m), "Shear friction factor m must lie in [0, 1].");
    }
}
