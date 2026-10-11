using System.Globalization;
using System.Text;

namespace ImpressionFEA.Core.Simulation;

/// <summary>One increment of telemetry, without the mesh (cheap to keep for many runs).</summary>
public sealed record TelemetryRow(
    int Step, double Time, double Stroke, double DieGap, double LoadTonnes, double FillPercent, double VolumeChangePercent,
    double MaxVonMises, double MaxEffectiveStrain, int Iterations, int Elements, int Nodes)
{
    public static TelemetryRow From(ForgingSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.Step, s.Time, s.Stroke, s.DieGap, s.LoadTonnes, s.FillPercent, s.VolumeChangePercent,
            s.VonMises.Length > 0 ? s.VonMises.Max() : 0, s.EffectiveStrain.Length > 0 ? s.EffectiveStrain.Max() : 0,
            s.Iterations, s.ElementCount, s.NodeCount);
    }
}

/// <summary>Writes run telemetry (one row per increment) and multi-run summaries as CSV (invariant culture, mm / t / MPa).</summary>
public static class TelemetryCsv
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const string Header = "step,time_s,stroke_mm,die_gap_mm,load_t,load_kN,fill_pct,volume_change_pct,max_von_mises_MPa,max_effective_strain,iterations,elements,nodes";

    /// <summary>Telemetry of one run, preceded by '#' comment lines describing it.</summary>
    public static void Write(string path, IEnumerable<TelemetryRow> rows, IEnumerable<string>? comments = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder();
        foreach (var c in comments ?? []) sb.Append("# ").AppendLine(c.Replace('\n', ' '));
        sb.AppendLine(Header);
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(',',
                r.Step.ToString(Inv), r.Time.ToString("G6", Inv), r.Stroke.ToString("F4", Inv), r.DieGap.ToString("F4", Inv),
                r.LoadTonnes.ToString("F3", Inv), (r.LoadTonnes * 9.80665).ToString("F2", Inv), r.FillPercent.ToString("F3", Inv),
                r.VolumeChangePercent.ToString("F4", Inv), r.MaxVonMises.ToString("F2", Inv), r.MaxEffectiveStrain.ToString("F4", Inv),
                r.Iterations.ToString(Inv), r.Elements.ToString(Inv), r.Nodes.ToString(Inv)));
        }

        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>Quotes a CSV field when needed.</summary>
    public static string Field(string s) => s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
