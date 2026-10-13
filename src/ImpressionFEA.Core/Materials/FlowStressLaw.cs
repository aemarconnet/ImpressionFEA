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
/// Material preset (Fields–Backofen power law with Swift offset and exponential thermal softening):
/// σ̄ = C(T)·(ε₀+ε̄)ⁿ·ε̄̇ᵐ, C(T) = C_ref · exp(−β (T − T_ref)). <see cref="Source"/> says where the constants come from.
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

    /// <summary>Material family, for grouping in lists ("Wrought aluminium", "Stainless steel", …).</summary>
    public string Family { get; init; } = "Other";

    /// <summary>Where the constants come from (table and grade used, or "representative").</summary>
    public string Source { get; init; } = "";

    /// <summary>True for materials read from the user's materials file.</summary>
    public bool IsUser { get; init; }

    /// <summary>Condition and data source, for tooltips.</summary>
    public string Description => Source.Length > 0 ? Condition + ". " + Source : Condition;

    public override string ToString() => Name;
}

/// <summary>
/// Built-in materials, grouped by family. Where a grade (or the closest listed grade) appears in Altan, Ngaile and Shen,
/// "Cold and Hot Forging: Fundamentals and Applications" (ASM International, 2005), Tables 4.7–4.9, the constants are
/// fitted to those tables: β from the log-slope of C against temperature over the forging window, m at the nominal
/// temperature, n from the change of C with strain at that temperature, and C_ref so the law reproduces the table at
/// ε ≈ 0.5 (C converted from ksi, × 6.895). Other grades carry representative values, marked as such in
/// <see cref="MaterialPreset.Source"/>. T_ref is each material's nominal forging temperature (the default).
/// </summary>
public static class MaterialPresets
{
    private const string Carbon = "Carbon & low-alloy steel", Stainless = "Stainless steel", Tool = "Tool steel",
        Aluminium = "Wrought aluminium", Copper = "Copper & copper alloys", Modelling = "Physical modelling";

    private const string Altan = "Fitted to Altan, Ngaile & Shen, Cold and Hot Forging (ASM, 2005), ";

    private static MaterialPreset Hot(string family, string name, double c, double n, double m, double tRef, double beta, double tMin, double tMax, double t, string source) =>
        new(name, "Hot forging", CRef: c, N: n, M: m, Eps0: 0.05, TRef: tRef, Beta: beta, TMin: tMin, TMax: tMax, DefaultTemperature: t) { Family = family, Source = source };

    public static MaterialPreset Steel4140 { get; } = Hot(Carbon, "AISI 4140 alloy steel", 89.7, 0.128, 0.130, 1150, 0.00313, 900, 1250, 1150,
        Altan + "Table 4.7, 'alloy steel' 0.35C-1.49Mn-0.28Mo (closest listed to 4140), ε 0.1–0.7, 900–1200 °C.");

    public static MaterialPreset Al6061 { get; } = Hot(Aluminium, "6061-T6 aluminium", 48, 0.02, 0.12, 450, 0.0065, 350, 520, 450,
        "Representative value: 6061 is not in Altan Table 4.8.");

    public static MaterialPreset CopperC11000 { get; } = Hot(Copper, "C11000 ETP copper", 51.5, 0.026, 0.189, 800, 0.00290, 600, 900, 800,
        Altan + "Table 4.9, 99.9+ % copper (4–40 /s), ε 0.1–0.7, 600–900 °C (the CDA 110 rows stop at 600 °C).");

    public static MaterialPreset Plasticine { get; } =
        new("Modeling plasticine", "Physical modelling, room temp.", CRef: 0.20, N: 0.10, M: 0.18, Eps0: 0.05, TRef: 20, Beta: 0.035, TMin: 10, TMax: 35, DefaultTemperature: 20)
        {
            Family = Modelling,
            Source = "Representative value (physical-modelling literature); varies by brand and temperature, so calibrate with a compression test.",
        };

    /// <summary>Every built-in material, in family order (AISI 4140 first: the default).</summary>
    public static IReadOnlyList<MaterialPreset> All { get; } =
    [
        Steel4140,
        Hot(Carbon, "AISI 1010 low-carbon steel", 65.4, 0.017, 0.182, 1180, 0.00344, 900, 1260, 1180,
            Altan + "Table 4.7, AISI 1016 (closest listed), ε 0.1–0.7, 900–1200 °C."),
        Hot(Carbon, "AISI 1018 low-carbon steel", 63.8, 0.017, 0.198, 1180, 0.00303, 900, 1260, 1180,
            Altan + "Table 4.7, AISI 1018 (one strain level), 980–1205 °C; n from AISI 1016."),
        Hot(Carbon, "AISI 1020 low-carbon steel", 65.4, 0.017, 0.182, 1180, 0.00344, 900, 1260, 1180,
            Altan + "Table 4.7, AISI 1016 (closest listed), ε 0.1–0.7, 900–1200 °C."),
        Hot(Carbon, "AISI 1035 medium-carbon steel", 84.5, 0.07, 0.180, 1150, 0.0037, 900, 1250, 1150,
            "Interpolated between AISI 1016 and AISI 1045 of Altan, Ngaile & Shen (ASM, 2005), Table 4.7."),
        Hot(Carbon, "AISI 1045 medium-carbon steel", 100.3, 0.133, 0.181, 1150, 0.00403, 900, 1250, 1150,
            Altan + "Table 4.7, AISI 1045, ε 0.05–0.4, 900–1200 °C."),
        Hot(Carbon, "AISI 1060 high-carbon steel", 79.1, 0.042, 0.162, 1120, 0.00330, 900, 1200, 1120,
            Altan + "Table 4.7, AISI 1060, ε 0.1–0.7, 900–1200 °C."),
        Hot(Carbon, "AISI 1095 high-carbon steel", 74.0, 0.004, 0.176, 1100, 0.00361, 900, 1150, 1100,
            Altan + "Table 4.7, AISI 1095, ε 0.1–0.7, 900–1200 °C."),
        Hot(Stainless, "AISI 304 austenitic stainless", 124.6, 0.130, 0.107, 1150, 0.00308, 925, 1200, 1150,
            Altan + "Table 4.7, 302 SS (1.5–100 /s; closest listed to 304), ε 0.05–0.7, 900–1200 °C."),
        Hot(Stainless, "AISI 316 austenitic stainless", 142, 0.05, 0.11, 1150, 0.0030, 925, 1200, 1150,
            "Representative value, about 1.2 × 304 (the 316 SS rows of Altan Table 4.7 are high-rate, hot-drawn and scattered)."),
        Hot(Stainless, "AISI 410 martensitic stainless", 78.9, 0.040, 0.147, 1100, 0.00359, 900, 1200, 1100,
            Altan + "Table 4.7, 403 SS (12.6 Cr; closest listed to 410), ε 0.25–0.7, 1000–1200 °C."),
        Hot(Stainless, "17-4 PH stainless", 137, 0.04, 0.12, 1150, 0.0031, 1000, 1180, 1150,
            "Representative value: 17-4 PH is not in Altan Table 4.7."),
        Hot(Tool, "AISI D2 tool steel", 153.9, 0.0, 0.140, 1050, 0.00385, 925, 1095, 1050,
            Altan + "Table 4.7, AISI D3 (closest listed; D2 is not tabled), ε 0.1–0.7, 900–1100 °C."),
        Hot(Aluminium, "1100 commercially pure aluminium", 40.5, 0.064, 0.100, 400, 0.00506, 300, 500, 400,
            Altan + "Table 4.8, 1100 extruded and annealed, ε 0.1–0.7, 350–450 °C."),
        Hot(Aluminium, "2014 aluminium", 43.7, 0.033, 0.088, 440, 0.00515, 420, 470, 440,
            Altan + "Table 4.8, 2017 solution treated (closest listed Al-Cu alloy), ε 0.115 and 2.66, 400–500 °C."),
        Hot(Aluminium, "2024 aluminium", 43.7, 0.033, 0.088, 440, 0.00515, 400, 460, 440,
            Altan + "Table 4.8, 2017 solution treated (closest listed Al-Cu alloy), ε 0.115 and 2.66, 400–500 °C."),
        Al6061,
        Hot(Aluminium, "6063 aluminium", 40, 0.02, 0.12, 480, 0.0065, 420, 520, 480,
            "Representative value: 6063 is not in Altan Table 4.8."),
        Hot(Aluminium, "6082 aluminium", 45, 0.02, 0.12, 480, 0.0065, 430, 520, 480,
            "Representative value: 6082 is not in Altan Table 4.8."),
        Hot(Aluminium, "7075 aluminium", 55.9, 0.0, 0.117, 420, 0.00895, 370, 440, 420,
            Altan + "Table 4.8, 7075 solution treated and aged, ε 0.115 and 2.66, 400–450 °C."),
        CopperC11000,
        Hot(Copper, "C28000 Muntz metal (60/40 brass)", 25.1, 0.0, 0.256, 720, 0.00328, 650, 800, 720,
            Altan + "Table 4.9, CDA 280, ε 0.25–0.7, 600–800 °C."),
        Hot(Copper, "C37700 forging brass", 25.3, 0.0, 0.173, 720, 0.00699, 650, 780, 720,
            Altan + "Table 4.9, CDA 365 (60Cu-0.9Pb leaded brass; closest listed), ε 0.25–0.7, 600–800 °C."),
        Hot(Copper, "C63000 aluminium bronze", 95, 0.04, 0.14, 850, 0.0050, 800, 925, 870,
            "Representative value: aluminium bronze is not in Altan Table 4.9."),
        Plasticine,
    ];
}
