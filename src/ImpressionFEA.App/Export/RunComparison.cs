using System.Globalization;
using System.IO;
using ImpressionFEA.App.Infrastructure;
using ImpressionFEA.App.ViewModels;
using ImpressionFEA.Core.Cad;
using ImpressionFEA.Core.Simulation;

namespace ImpressionFEA.App.Export;

/// <summary>An input of a run as shown in reports; <see cref="Value"/> is set for inputs a sweep can be plotted against.</summary>
public sealed record RunParameter(string Name, string Unit, Func<RunSettings, string> Show, Func<RunSettings, double?>? Value = null)
{
    public string AxisTitle => Unit.Length > 0 ? Name + " (" + Unit + ")" : Name;
}

/// <summary>Which inputs the compared runs share and which differ (in the current display units).</summary>
public static class RunComparison
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static UnitSet U => Units.Current;

    private static string Fine(double mm) => U.L(mm).ToString(U.FineLengthFormat, Inv) + " " + U.Length;

    /// <summary>Every input reports compare, in display order.</summary>
    public static IReadOnlyList<RunParameter> Parameters =>
    [
        new("Analysis", "", s => s.Analysis),
        new("Geometry", "", s => s.StepFile is { } f ? Path.GetFileName(f) + (s.TopDieBody is { } b ? string.Format(Inv, " (top die body {0})", b) : "") : s.Preset ?? "—"),
        new("Workpiece", "", Workpiece),
        new("Flash thickness", U.Length, s => s.FlashThickness is { } f ? Fine(f) : "—", s => s.FlashThickness is { } f ? U.L(f) : null),
        new("Material", "", s => s.Material),
        new("Temperature", U.Temperature, s => U.Tf(s.Temperature), s => U.T(s.Temperature)),
        new("Friction m", "", s => s.Friction.ToString("F2", Inv), s => s.Friction),
        new("Ram speed", U.Speed, s => U.Speedf(s.RamSpeed), s => U.L(s.RamSpeed)),
        new("Element size h", U.Length, s => Fine(s.ElementSize), s => U.L(s.ElementSize)),
        new("Stroke increment Δs", U.Length, s => Fine(s.StrokeIncrement), s => U.L(s.StrokeIncrement)),
        new("Auto time-stepping", "", s => s.AutoStep ? "on" : "off"),
        new("Press capacity", U.Load, s => U.Loadf(s.PressCapacity)),
        new("Linear solver", "", s => s.LinearBackend),
        new("Max cutbacks", "", s => s.MaxCutbacks.ToString(Inv), s => s.MaxCutbacks),
        new("Refinement levels", "", s => s.RefinementLevels.ToString(Inv) + (s.RefineThinFlash ? " + thin flash" : ""), s => s.RefinementLevels),
        new("Remesh quality", "", s => s.RemeshQuality.ToString("F2", Inv), s => s.RemeshQuality),
        new("Max nodes", "", s => s.MaxNodeCount.ToString("N0", Inv), s => s.MaxNodeCount),
        new("Max iterations", "", s => s.MaxIterations.ToString(Inv), s => s.MaxIterations),
        new("Tolerance", "", s => s.Tolerance.ToString("0.0E+0", Inv), s => s.Tolerance),
    ];

    private static string Workpiece(RunSettings s)
    {
        if (s.Workpiece is { } w)
        {
            string shape = w.Shape switch
            {
                UserBilletShape.Block => "block " + U.L(w.Width).ToString(U.LengthFormat, Inv) + " × " + U.L(w.Depth).ToString(U.LengthFormat, Inv) + " × " + Fine(w.Height),
                UserBilletShape.HorizontalBar => "horizontal bar Ø " + U.L(w.Diameter).ToString(U.LengthFormat, Inv) + " × " + Fine(w.Length),
                _ => "vertical bar Ø " + U.L(w.Diameter).ToString(U.LengthFormat, Inv) + " × " + Fine(w.Length),
            };
            if (w.AngleDeg != 0) shape += string.Format(Inv, ", {0:F1}°", w.AngleDeg);
            if (w.OffsetX != 0 || w.OffsetY != 0) shape += ", offset " + U.Lf(w.OffsetX) + " / " + U.Lf(w.OffsetY);
            return shape;
        }

        if (s.BilletRadius is { } r) return "R " + U.Lf(r) + ", H " + U.Lf(s.BilletHeight ?? 0) + (s.BilletLength is { } l ? ", L " + U.Lf(l) : "");
        return s.StepFile is not null ? "from the STEP file" : "—";
    }

    /// <summary>Inputs equal in every run, and those that differ.</summary>
    public static (List<RunParameter> Common, List<RunParameter> Varied) Split(IEnumerable<RunSettings> settings)
    {
        var all = settings.ToList();
        var common = new List<RunParameter>();
        var varied = new List<RunParameter>();
        foreach (var p in Parameters)
        {
            if (all.Select(p.Show).Distinct().Count() > 1) varied.Add(p);
            else common.Add(p);
        }

        return (common, varied);
    }

    /// <summary>The input to plot results against: the only one that differs, when it is numeric.</summary>
    public static RunParameter? SweepParameter(IReadOnlyList<RunParameter> varied) =>
        varied is [{ Value: not null } p] ? p : null;

    /// <summary>"Run 3 · 950 °C" — the run number and, when only one or two inputs differ, their values.</summary>
    public static string LegendName(RunRecord run, IReadOnlyList<RunParameter> varied) =>
        "Run " + run.Number.ToString(Inv) + (varied.Count is > 0 and <= 2 ? " · " + string.Join(" · ", varied.Select(p => p.Show(run.Settings))) : "");

    /// <summary>What sets a run apart, for column headers: the values of the inputs that differ, else its label.</summary>
    public static string Distinction(RunRecord run, IReadOnlyList<RunParameter> varied) =>
        varied.Count is > 0 and <= 3
            ? string.Join(" · ", varied.Select(p => (p.Name.StartsWith("Friction", StringComparison.Ordinal) ? "m " : "") + p.Show(run.Settings)))
            : System.Text.RegularExpressions.Regex.Replace(run.Label, @"^#\d+\s*", "");
}
