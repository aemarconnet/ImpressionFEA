namespace ImpressionFEA.Core.Materials;

/// <summary>
/// Isothermal rigid-viscoplastic flow stress law  σ̄ = C · (ε₀ + ε̄)ⁿ · ε̄̇ᵐ   [MPa, ε̄̇ in 1/s].
/// </summary>
public readonly record struct FlowStressLaw(double C, double N, double M, double Eps0)
{
    public double Stress(double strain, double strainRate) =>
        C * Math.Pow(Eps0 + Math.Max(strain, 0), N) * Math.Pow(Math.Max(strainRate, 1e-12), M);

    public override string ToString() =>
        FormattableString.Invariant($"σ̄ = {C:F3}·({Eps0:F2}+ε̄)^{N:F3}·ε̄̇^{M:F3} MPa");
}

/// <summary>
/// Material preset: C(T) = C_ref · exp(−β (T − T_ref)). Coefficients are representative handbook-level
/// values (Altan et al., "Cold and Hot Forging", ASM) for the stated forging window, not certified data.
/// </summary>
public sealed record MaterialPreset(
    string Name,
    string Condition,
    double CRef,
    double N,
    double M,
    double Eps0,
    double TRef,
    double Beta,
    double TMin,
    double TMax,
    double DefaultTemperature)
{
    public double CAt(double temperature) => CRef * Math.Exp(-Beta * (temperature - TRef));

    public FlowStressLaw At(double temperature) => new(CAt(temperature), N, M, Eps0);

    public bool InRange(double temperature) => temperature >= TMin && temperature <= TMax;

    public override string ToString() => Name;
}

public static class MaterialPresets
{
    public static MaterialPreset Steel4140 { get; } =
        new("AISI 4140 alloy steel", "Hot forging", CRef: 105, N: 0.03, M: 0.14, Eps0: 0.05, TRef: 1100, Beta: 0.0032, TMin: 900, TMax: 1250, DefaultTemperature: 1150);

    public static MaterialPreset Al6061 { get; } =
        new("6061-T6 aluminium", "Hot forging", CRef: 48, N: 0.02, M: 0.12, Eps0: 0.05, TRef: 450, Beta: 0.0065, TMin: 350, TMax: 520, DefaultTemperature: 450);

    public static MaterialPreset CopperC11000 { get; } =
        new("C11000 ETP copper", "Hot forging", CRef: 52, N: 0.05, M: 0.15, Eps0: 0.05, TRef: 800, Beta: 0.0042, TMin: 600, TMax: 900, DefaultTemperature: 800);

    public static MaterialPreset Plasticine { get; } =
        new("Modeling plasticine", "Physical modelling, room temp.", CRef: 0.20, N: 0.10, M: 0.18, Eps0: 0.05, TRef: 20, Beta: 0.035, TMin: 10, TMax: 35, DefaultTemperature: 20);

    public static IReadOnlyList<MaterialPreset> All { get; } = [Steel4140, Al6061, CopperC11000, Plasticine];
}
