using ImpressionFEA.Core.ThreeD;
using static System.FormattableString;

namespace ImpressionFEA.Core.Geometry;

/// <summary>
/// Flash land of a die set: the closed die gap over the land (flash thickness t) and the flat land width w, both mm.
/// w is the flat part of the land, where the closed gap stays within 5 % of t (die-edge radii and the gutter excluded),
/// as in the slab model of the flash (Altan, Ngaile &amp; Shen, Fig. 14.15).
/// </summary>
public sealed record FlashLand(double Thickness, double Width)
{
    /// <summary>Land ratio w/t.</summary>
    public double Ratio => Width / Thickness;

    public override string ToString() => Invariant($"t {Thickness:0.0#} mm × w {Width:0.0#} mm (w/t {Ratio:0.0})");
}

/// <summary>How a flash land compares with the handbook guidance for a forging of a given weight.</summary>
public sealed record FlashAssessment(
    FlashLand Land,
    double WeightKg,
    double SurveyThickness,
    double SurveyRatio,
    IReadOnlyList<string> Issues)
{
    /// <summary>Thickness relative to the survey curve (1 = the survey's typical value for this weight).</summary>
    public double ThicknessFactor => Land.Thickness / SurveyThickness;

    /// <summary>Within the survey's spread of thickness and the w/t range of the empirical load data.</summary>
    public bool WithinGuideline => Issues.Count == 0;

    /// <summary>On the thick side, as the built-in dies are (see <see cref="FlashDesign"/>).</summary>
    public bool ThickSide => ThicknessFactor >= FlashDesign.ThickSideMin && ThicknessFactor <= FlashDesign.SurveyMax
        && Land.Ratio >= FlashDesign.ThickSideRatioMin && Land.Ratio <= FlashDesign.ThickSideRatioMax;

    public string Summary => Invariant(
        $"{Land}; for a {WeightKg:0.00} kg steel forging the ASM survey gives t ≈ {SurveyThickness:0.0} mm (this die {ThicknessFactor:0.0}×) and w/t ≈ {SurveyRatio:0.0}")
        + (Issues.Count == 0 ? "" : ". " + string.Join(" ", Issues));
}

/// <summary>
/// Flash design guidance for impression-die forgings of carbon and alloy steel, from Altan, Ngaile &amp; Shen,
/// <i>Cold and Hot Forging: Fundamentals and Applications</i> (ASM International, 2005), §14.4–14.5:
/// <list type="bullet">
/// <item>A survey of about 1,500 steel forgings from eight forging companies (Fig. 14.13, shape group 224): flash
/// thickness t and land ratio w/t against forging weight Q. The two trend curves are digitised here (reading accuracy
/// about ±0.1 mm and ±0.2). The flash grows with weight while w/t falls towards about 2. Most of the survey's points lie
/// between half and twice the curve thickness.</item>
/// <item>Vieregge's rules for round forgings (Eq 14.6, 14.7): flash dimensions with little flash allowance, for
/// accurately sized billets. They give thinner flash than the survey.</item>
/// <item>Neuberger and Pannasch's forging-pressure data (Fig. 14.14) cover land ratios w/t = 2–4.</item>
/// </list>
/// <para>
/// <b>Built-in die policy (thick side).</b> ImpressionFEA's solver is isothermal. In a real press a thin flash chills
/// against the dies and deforms fast, so its flow stress rises (the book's slab method uses a separate, higher flow
/// stress for the flash). An isothermal model cannot capture that, and a thin flash also needs a much finer mesh. The
/// built-in dies therefore use flash on the thick side of practice: t between <see cref="ThickSideMin"/> and
/// <see cref="SurveyMax"/> times the survey curve, and w/t between <see cref="ThickSideRatioMin"/> and
/// <see cref="ThickSideRatioMax"/> (about 3, the middle of the 2–4 band). Thicker flash gives steadier, mesh-insensitive
/// loads. When heat transfer is modelled, flash design and loads will change substantially: revisit this policy then.
/// </para>
/// </summary>
public static class FlashDesign
{
    /// <summary>Density of steel, kg/mm³ (the survey covers steel forgings; other materials are compared by steel weight).</summary>
    public const double SteelDensity = 7.85e-6;

    /// <summary>Land ratio range of the empirical forging-pressure data (Fig. 14.14).</summary>
    public const double MinRatio = 2, MaxRatio = 4;

    /// <summary>Thickness range of the survey's points, as factors of the survey curve (our reading of Fig. 14.13).</summary>
    public const double SurveyMin = 0.5, SurveyMax = 2.0;

    /// <summary>Built-in die policy: lower bound of t as a factor of the survey curve (the thick side).</summary>
    public const double ThickSideMin = 1.25;

    /// <summary>Built-in die policy: land ratio band around 3.</summary>
    public const double ThickSideRatioMin = 2.5, ThickSideRatioMax = 3.5;

    // Fig. 14.13 trend curves, digitised: forging weight (kg) → flash thickness (mm) and → w/t.
    private static readonly (double Q, double V)[] SurveyT =
        [(0.1, 1.25), (0.2, 1.33), (0.3, 1.42), (0.5, 1.55), (1, 1.85), (2, 2.3), (5, 3.4), (10, 4.6), (20, 6.0), (50, 8.1), (100, 9.6)];

    private static readonly (double Q, double V)[] SurveyR =
        [(0.13, 4.5), (0.2, 4.0), (0.5, 3.6), (1, 3.25), (2, 2.95), (5, 2.65), (10, 2.45), (50, 2.25), (100, 2.15)];

    /// <summary>Survey trend of flash thickness (mm) for a steel forging of <paramref name="weightKg"/> (Fig. 14.13).</summary>
    public static double SurveyThickness(double weightKg) => Interpolate(SurveyT, weightKg);

    /// <summary>Survey trend of the land ratio w/t for a steel forging of <paramref name="weightKg"/> (Fig. 14.13).</summary>
    public static double SurveyRatio(double weightKg) => Interpolate(SurveyR, weightKg);

    /// <summary>Vieregge's flash thickness for a round forging of outside diameter D (mm), Eq 14.6: t = 0.017·D + 1/√(D + 5).</summary>
    public static double ViereggeThickness(double diameter) => 0.017 * diameter + 1 / Math.Sqrt(diameter + 5);

    /// <summary>
    /// Vieregge's land ratio for a round forging, Eq 14.7: w/t = 30 / ∛(D·[1 + 2D² / (H·(2·Rh + D))]), with D the outside
    /// diameter, H the height of the rib or shaft and Rh the radial distance of the rib centre from the axis (mm).
    /// </summary>
    public static double ViereggeRatio(double diameter, double height, double ribRadius) =>
        30 / Math.Cbrt(diameter * (1 + 2 * diameter * diameter / (height * (2 * ribRadius + diameter))));

    /// <summary>Steel weight (kg) of a forging of <paramref name="volume"/> mm³.</summary>
    public static double SteelWeight(double volume) => volume * SteelDensity;

    /// <summary>Compares a flash land with the survey and the empirical w/t range.</summary>
    public static FlashAssessment Assess(FlashLand land, double weightKg)
    {
        ArgumentNullException.ThrowIfNull(land);
        double ts = SurveyThickness(weightKg), rs = SurveyRatio(weightKg);
        var issues = new List<string>();
        if (land.Thickness < SurveyMin * ts)
            issues.Add(Invariant($"The flash is thinner than almost all surveyed forgings of this weight (below {SurveyMin * ts:0.0} mm): high load, and an isothermal model understates it."));
        else if (land.Thickness > SurveyMax * ts)
            issues.Add(Invariant($"The flash is thicker than almost all surveyed forgings of this weight (above {SurveyMax * ts:0.0} mm): the cavity may not fill."));
        if (land.Ratio < MinRatio)
            issues.Add(Invariant($"The land ratio w/t {land.Ratio:0.0} is below {MinRatio:0}: little restriction, the cavity may not fill."));
        else if (land.Ratio > MaxRatio)
            issues.Add(Invariant($"The land ratio w/t {land.Ratio:0.0} is above {MaxRatio:0}: outside the empirical load data, high load."));
        return new FlashAssessment(land, weightKg, ts, rs, issues);
    }

    /// <summary>Flash land of a 2D die set, measured on the closed dies beyond the impression (null: no flash land).</summary>
    public static FlashLand? Measure(DieSet die)
    {
        ArgumentNullException.ThrowIfNull(die);
        double r0 = die.CavityOuterRadius, length = Math.Max(die.ViewExtent, r0 + 10) * 1.5 - r0;
        return MeasureGap(s => die.TopClosed.HeightAt(r0 - 2 + s) - die.Bottom.HeightAt(r0 - 2 + s), length + 2, die.TopClosed.HeightAt(0) - die.Bottom.HeightAt(0));
    }

    /// <summary>
    /// Flash land of 3D dies measured along a plan-view ray from (x0, y0) in direction (dx, dy) over <paramref name="length"/>
    /// mm: start inside the impression, near its edge, and cross the land at right angles (null: no flash land).
    /// </summary>
    public static FlashLand? Measure(IRigidDie bottom, IRigidDie topClosed, double x0, double y0, double dx, double dy, double length)
    {
        ArgumentNullException.ThrowIfNull(bottom);
        ArgumentNullException.ThrowIfNull(topClosed);
        double n = Math.Sqrt(dx * dx + dy * dy);
        dx /= n;
        dy /= n;
        double Gap(double s) => topClosed.SurfaceZ(x0 + s * dx, y0 + s * dy) - bottom.SurfaceZ(x0 + s * dx, y0 + s * dy);
        return MeasureGap(Gap, length, Gap(0));
    }

    /// <summary>
    /// Thinnest closed gap along a line (the flash thickness) and the length over which it stays within 5 % of it (the
    /// flat land). No land when the thinnest gap is not much thinner than the impression (flat upsetting dies).
    /// </summary>
    private static FlashLand? MeasureGap(Func<double, double> gap, double length, double cavityGap)
    {
        const double step = 0.01;
        int n = (int)(length / step) + 1;
        var g = new double[n];
        int iMin = 0;
        for (int i = 0; i < n; i++)
        {
            g[i] = gap(i * step);
            if (g[i] < g[iMin]) iMin = i;
        }

        double t = g[iMin];
        if (t <= 0 || t > 0.5 * cavityGap) return null;
        int a = iMin, b = iMin;
        while (a > 0 && g[a - 1] <= 1.05 * t) a--;
        while (b < n - 1 && g[b + 1] <= 1.05 * t) b++;
        return new FlashLand(t, (b - a) * step);
    }

    private static double Interpolate((double Q, double V)[] table, double q)
    {
        if (q <= table[0].Q) return table[0].V;
        if (q >= table[^1].Q) return table[^1].V;
        int i = 1;
        while (table[i].Q < q) i++;
        double f = Math.Log(q / table[i - 1].Q) / Math.Log(table[i].Q / table[i - 1].Q);
        return table[i - 1].V + f * (table[i].V - table[i - 1].V);
    }
}
