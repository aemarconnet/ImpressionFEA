using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ImpressionFEA.Core.Simulation;

namespace ImpressionFEA.App.Export;

/// <summary>Self-contained HTML report (images embedded as data URIs; also saved beside it). Prints cleanly to PDF.</summary>
public static partial class ReportHtml
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string F(FormattableString s) => s.ToString(Inv);

    [GeneratedRegex(@"REMESH #(\d+)")]
    private static partial Regex RemeshRx();

    [GeneratedRegex(@"auto-tune: .*|Linear solver: (?!Auto).*")]
    private static partial Regex SolverRx();

    public static string Build(ReportData r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var vm = r.Vm;
        var run = vm.CurrentRun;
        var settings = run?.Settings ?? vm.CaptureSettings();
        var rows = run?.Rows ?? vm.Snapshots.Select(TelemetryRow.From).ToList();
        var peak = rows.OrderByDescending(x => x.LoadTonnes).First();
        var last = rows[^1];
        string log = vm.RunLog;
        int remeshes = RemeshRx().Matches(log).Count;
        string solver = SolverRx().Matches(log).Select(m => m.Value).LastOrDefault() ?? settings.LinearBackend;

        var h = new StringBuilder();
        h.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        h.Append("<title>ImpressionFEA report – ").Append(E(vm.GeometryName)).Append("</title><style>").Append(Css).Append("</style></head><body>");

        // Header
        h.Append("<header><div><h1>Forging simulation report</h1><p class=\"sub\">").Append(E(vm.GeometryName)).Append(" · ")
         .Append(E(settings.Material)).Append(F($" · {settings.Temperature:F0} °C")).Append("</p></div><div class=\"meta\">ImpressionFEA ")
         .Append(E(AppVersion.Display)).Append("<br>").Append(E(DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv))).Append("</div></header>");

        // Key results
        h.Append("<section class=\"cards\">");
        Card(h, "Peak press load", F($"{peak.LoadTonnes:F1} t"), F($"{peak.LoadTonnes * 9.80665:F0} kN at {peak.Stroke:F2} mm"));
        Card(h, "Final cavity fill", F($"{last.FillPercent:F1} %"), F($"after {last.Stroke:F2} mm stroke"));
        Card(h, "Volume change", F($"{last.VolumeChangePercent:+0.00;-0.00;0.00} %"), "numerical (incompressible material)");
        Card(h, "Max effective strain", F($"{rows.Max(x => x.MaxEffectiveStrain):F2}"), F($"max von Mises {rows.Max(x => x.MaxVonMises):F0} MPa"));
        Card(h, "Status", E(run?.StatusText ?? "—"), F($"{rows.Count - 1} increments · {remeshes} remeshes · {(run?.Elapsed.TotalSeconds ?? 0):F1} s"));
        h.Append("</section>");

        // Setup
        h.Append("<section><h2>Setup</h2><div class=\"cols\"><table class=\"kv\">");
        Kv(h, "Analysis", settings.Analysis == "2D" ? (vm.IsPlaneStrainResult ? "2D plane strain" : "2D axisymmetric") : "3D (" + vm.ResultSymmetry + " model)");
        Kv(h, "Geometry", settings.StepFile is { } sf ? "STEP: " + System.IO.Path.GetFileName(sf) + F($" (top die body {settings.TopDieBody})") : settings.Preset ?? "");
        Kv(h, "Description", vm.DieDescriptionText);
        if (settings.BilletRadius is { } br) Kv(h, "Billet", F($"R {br:F2} mm, H {settings.BilletHeight ?? 0:F2} mm, L {settings.BilletLength ?? 0:F1} mm"));
        Kv(h, "Material", settings.Material);
        Kv(h, "Flow stress", vm.MaterialSummary);
        Kv(h, "Temperature", F($"{settings.Temperature:F0} °C (isothermal)"));
        Kv(h, "Friction", F($"m = {settings.Friction:F2} (constant shear)"));
        h.Append("</table><table class=\"kv\">");
        Kv(h, "Ram speed", F($"{settings.RamSpeed:F1} mm/s"));
        Kv(h, "Element size h", F($"{settings.ElementSize:F2} mm"));
        Kv(h, "Stroke increment", F($"{settings.StrokeIncrement:F3} mm{(settings.AutoStep ? " (auto time-stepping)" : "")}"));
        Kv(h, "Robustness", F($"≤ {settings.MaxCutbacks} cutbacks, refinement {settings.RefinementLevels} level(s){(settings.RefineThinFlash ? " + thin flash" : "")}, remesh below q {settings.RemeshQuality:F2}"));
        Kv(h, "Convergence", F($"|Δv|/|v| < {settings.Tolerance:0.0E+0}, ≤ {settings.MaxIterations} iterations"));
        Kv(h, "Linear solver", solver);
        Kv(h, "Press capacity", F($"{settings.PressCapacity:F0} t{(peak.LoadTonnes > settings.PressCapacity ? " — EXCEEDED" : "")}"));
        h.Append("</table></div></section>");

        // Telemetry chart
        h.Append("<section><h2>Load and cavity fill</h2>");
        Img(h, r.Chart, "Press load (solid) and cavity fill (dashed) against ram stroke");
        h.Append("</section>");

        // Fields
        h.Append("<section class=\"break\"><h2>Result fields at die closure</h2><div class=\"grid2\">");
        foreach (var (name, unit, image) in r.Fields) Img(h, image, name + " [" + unit + "]");
        h.Append("</div></section>");

        if (r.Stages.Count > 0)
        {
            h.Append("<section class=\"break\"><h2>Ram descent</h2><div class=\"grid3\">");
            foreach (var (caption, image) in r.Stages) Img(h, image, caption);
            h.Append("</div></section>");
        }

        if (r.Video is { } v && !v.EndsWith("_frames", StringComparison.Ordinal))
        {
            h.Append("<section><h2>Video</h2><video controls loop src=\"").Append(E(v)).Append("\"></video><p class=\"note\">")
             .Append(E(v)).Append(" — opens in any modern browser.</p></section>");
        }

        if (r.SurfaceTriangles > 0)
        {
            h.Append("<section><h2>Deformed part</h2><p>").Append(F($"Full deformed part exported as STL and PLY: {r.SurfaceTriangles:N0} triangles, enclosed volume {r.SurfaceVolume / 1000:F2} cm³ (mm units, z = press axis)."))
             .Append("</p></section>");
        }

        // Telemetry table
        h.Append("<section class=\"break\"><h2>Telemetry</h2><table class=\"data\"><thead><tr><th>Step</th><th>Stroke mm</th><th>Gap mm</th><th>Load t</th><th>Fill %</th><th>ΔV %</th><th>σ̄ max MPa</th><th>ε̄ max</th><th>Elements</th></tr></thead><tbody>");
        int stride = Math.Max(1, (int)Math.Ceiling(rows.Count / 80.0));
        for (int i = 0; i < rows.Count; i++)
        {
            if (i % stride != 0 && i != rows.Count - 1) continue;
            var x = rows[i];
            h.Append(F($"<tr><td>{x.Step}</td><td>{x.Stroke:F3}</td><td>{x.DieGap:F3}</td><td>{x.LoadTonnes:F2}</td><td>{x.FillPercent:F2}</td><td>{x.VolumeChangePercent:F3}</td><td>{x.MaxVonMises:F1}</td><td>{x.MaxEffectiveStrain:F3}</td><td>{x.Elements}</td></tr>"));
        }

        h.Append("</tbody></table>");
        if (stride > 1) h.Append(F($"<p class=\"note\">Every {stride}th increment shown; telemetry.csv has all {rows.Count}.</p>"));
        h.Append("</section>");

        // Runs comparison
        if (vm.Runs.Count > 1)
        {
            h.Append("<section><h2>Runs in this session</h2><table class=\"data\"><thead><tr><th>#</th><th>Run</th><th>Peak t</th><th>Fill %</th><th>Stroke mm</th><th>ΔV %</th><th>Time s</th><th>Status</th></tr></thead><tbody>");
            foreach (var x in vm.Runs.Reverse())
            {
                h.Append("<tr><td>").Append(x.Number).Append("</td><td class=\"l\">").Append(E(x.Label)).Append("</td>")
                 .Append(F($"<td>{x.PeakLoad:F1}</td><td>{x.FinalFill:F1}</td><td>{x.FinalStroke:F2}</td><td>{x.VolumeChange:F2}</td><td>{x.Elapsed.TotalSeconds:F1}</td><td>{x.StatusText}</td></tr>"));
            }

            h.Append("</tbody></table></section>");
        }

        // Files
        h.Append("<section><h2>Files</h2><ul class=\"files\">");
        foreach (var (path, what) in r.Files) h.Append("<li><a href=\"").Append(E(path)).Append("\">").Append(E(path)).Append("</a> — ").Append(E(what)).Append("</li>");
        h.Append("<li><a href=\"images/\">images/</a> — every figure of this report as PNG</li></ul></section>");

        h.Append("<footer>Generated by ImpressionFEA ").Append(E(AppVersion.Display))
         .Append(" · © 2026 Marconnet Technologies LLC · CC BY-NC 4.0<br>Rigid-viscoplastic FEM, isothermal, rigid dies, constant shear friction. Loads and fill are engineering estimates, not certified predictions.</footer>");
        h.Append("</body></html>");
        return h.ToString();
    }

    private static void Card(StringBuilder h, string label, string value, string note) =>
        h.Append("<div class=\"card\"><div class=\"label\">").Append(E(label)).Append("</div><div class=\"value\">").Append(value).Append("</div><div class=\"note\">").Append(E(note)).Append("</div></div>");

    private static void Kv(StringBuilder h, string k, string v) => h.Append("<tr><th>").Append(E(k)).Append("</th><td>").Append(E(v)).Append("</td></tr>");

    private static void Img(StringBuilder h, (string File, string Base64) img, string caption)
    {
        if (string.IsNullOrEmpty(img.Base64)) return;
        h.Append("<figure><img alt=\"").Append(E(caption)).Append("\" src=\"data:image/png;base64,").Append(img.Base64).Append("\"><figcaption>")
         .Append(E(caption)).Append("</figcaption></figure>");
    }

    private const string Css = """
        :root{--ink:#101418;--muted:#56606c;--line:#d5dae0;--accent:#0b5cad;--bg:#f4f6f8}
        *{box-sizing:border-box}body{margin:0;padding:24px 32px;font:14px/1.45 "Segoe UI",system-ui,sans-serif;color:var(--ink);background:#fff;max-width:1200px;margin-inline:auto}
        header{display:flex;justify-content:space-between;align-items:flex-end;border-bottom:3px solid var(--accent);padding-bottom:10px;margin-bottom:18px}
        h1{margin:0;font-size:26px}h2{font-size:17px;margin:26px 0 10px;border-bottom:1px solid var(--line);padding-bottom:4px}
        .sub{margin:2px 0 0;color:var(--muted);font-size:15px}.meta{text-align:right;color:var(--muted);font-size:12px}
        .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:10px}
        .card{background:var(--bg);border:1px solid var(--line);border-radius:6px;padding:10px 12px}
        .card .label{font-size:12px;color:var(--muted);text-transform:uppercase;letter-spacing:.04em}.card .value{font-size:22px;font-weight:600;margin:2px 0}
        .note{color:var(--muted);font-size:12px}
        .cols{display:grid;grid-template-columns:1fr 1fr;gap:18px}@media(max-width:800px){.cols{grid-template-columns:1fr}}
        table{border-collapse:collapse;width:100%}.kv th{text-align:left;color:var(--muted);font-weight:500;width:34%;vertical-align:top;padding:3px 8px 3px 0}.kv td{padding:3px 0}
        .data th,.data td{border-bottom:1px solid var(--line);padding:3px 6px;text-align:right;font-variant-numeric:tabular-nums;font-size:12.5px}.data th{background:var(--bg)}.data td.l{text-align:left}
        figure{margin:0 0 10px}figure img{width:100%;border:1px solid var(--line);border-radius:4px}figcaption{font-size:12px;color:var(--muted);margin-top:3px}
        .grid2{display:grid;grid-template-columns:1fr 1fr;gap:12px}.grid3{display:grid;grid-template-columns:repeat(3,1fr);gap:10px}
        video{width:100%;border:1px solid var(--line);border-radius:4px}ul.files{padding-left:18px}
        footer{margin-top:30px;border-top:1px solid var(--line);padding-top:8px;color:var(--muted);font-size:12px}
        @media print{body{padding:0}.break{break-before:page}video{display:none}}
        """;
}
