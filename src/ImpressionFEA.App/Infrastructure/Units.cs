using System.Globalization;

namespace ImpressionFEA.App.Infrastructure;

public enum UnitSystem
{
    /// <summary>US customary: in, ksi, US tons-force (tonf), °F, in³.</summary>
    US,

    /// <summary>Metric: mm, MPa, metric tonnes-force (t), °C, cm³.</summary>
    Metric,
}

/// <summary>
/// Display units. The solver works in mm, MPa, N and °C; everything shown to the user (inputs, read-outs, colour bars,
/// chart, report) is converted with the current set. US: inches and ksi to 3 decimals; metric: mm and MPa to 2.
/// </summary>
public sealed record UnitSet(
    UnitSystem System,
    string Length, double MmPerLength, int LengthDecimals,
    string Stress, double MpaPerStress, int StressDecimals,
    string Load, double NewtonPerLoad, int LoadDecimals,
    string Temperature,
    string Volume, double Mm3PerVolume, int VolumeDecimals)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly UnitSet Us = new(UnitSystem.US, "in", 25.4, 3, "ksi", 6.894757293168361, 3, "tonf", 8896.443230521, 2, "°F", "in³", 16387.064, 3);

    public static readonly UnitSet Metric = new(UnitSystem.Metric, "mm", 1, 2, "MPa", 1, 2, "t", 9806.65, 1, "°C", "cm³", 1000, 2);

    public static UnitSet For(UnitSystem s) => s == UnitSystem.US ? Us : Metric;

    public string Speed => Length + "/s";

    public string LengthFormat => "F" + LengthDecimals;

    public string StressFormat => "F" + StressDecimals;

    /// <summary>Format for small lengths entered as inputs (element size, stroke increment): one more decimal.</summary>
    public string FineLengthFormat => "F" + (LengthDecimals + 1);

    public string LoadFormat => "F" + LoadDecimals;

    public double L(double mm) => mm / MmPerLength;

    public double LToMm(double v) => v * MmPerLength;

    public double S(double mpa) => mpa / MpaPerStress;

    public double LoadFromNewton(double n) => n / NewtonPerLoad;

    public double LoadFromTonnes(double t) => t * 9806.65 / NewtonPerLoad;

    public double LoadToTonnes(double v) => v * NewtonPerLoad / 9806.65;

    public double T(double celsius) => System == UnitSystem.US ? celsius * 9 / 5 + 32 : celsius;

    public double TToCelsius(double v) => System == UnitSystem.US ? (v - 32) * 5 / 9 : v;

    public double V(double mm3) => mm3 / Mm3PerVolume;

    public string Lf(double mm) => L(mm).ToString(LengthFormat, Inv) + " " + Length;

    public string Sf(double mpa) => S(mpa).ToString(StressFormat, Inv) + " " + Stress;

    public string LoadfN(double newton) => LoadFromNewton(newton).ToString(LoadFormat, Inv) + " " + Load;

    public string Loadf(double tonnes) => LoadFromTonnes(tonnes).ToString(LoadFormat, Inv) + " " + Load;

    public string Tf(double celsius) => T(celsius).ToString("F0", Inv) + " " + Temperature;

    public string Vf(double mm3) => V(mm3).ToString("F" + VolumeDecimals, Inv) + " " + Volume;

    public string Speedf(double mmPerS) => L(mmPerS).ToString("F" + LengthDecimals, Inv) + " " + Speed;
}

/// <summary>The app-wide unit set, remembered between sessions (AppSettings key "units"; US by default).</summary>
public static class Units
{
    public static UnitSet Current { get; private set; } =
        Enum.TryParse<UnitSystem>(AppSettings.Get("units"), out var s) ? UnitSet.For(s) : UnitSet.Us;

    public static event EventHandler? Changed;

    public static void Set(UnitSystem system)
    {
        if (Current.System == system) return;
        Current = UnitSet.For(system);
        AppSettings.Set("units", system.ToString());
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
