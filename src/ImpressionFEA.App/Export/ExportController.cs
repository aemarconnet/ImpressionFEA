using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImpressionFEA.App.Controls;
using ImpressionFEA.App.ViewModels;
using ImpressionFEA.Core.Simulation;

namespace ImpressionFEA.App.Export;

public enum VideoContent
{
    /// <summary>The 3D / 2D results view only.</summary>
    View,

    /// <summary>Results view and telemetry panel (chart, run list, read-outs) side by side.</summary>
    ViewAndTelemetry,

    /// <summary>The whole application window.</summary>
    Window,
}

public sealed record VideoOptions(string Path, VideoContent Content, double Fps, int Crf, double Scale, bool ExportTelemetry);

public sealed record ReportOptions(string Folder, bool IncludeVideo, bool IncludeStages, bool IncludeSurface, bool OpenWhenDone);

/// <summary>A comparison report: the runs to compare and what each run's own report includes.</summary>
public sealed record ComparisonOptions(
    string Folder, IReadOnlyList<RunRecord> Runs, bool IndividualReports, bool IncludeStages, bool IncludeSurface, bool IncludeVideo, bool OpenWhenDone);

/// <summary>
/// Exports results by replaying the increments through the live views: PNG images, WebM videos of the ram descent
/// (FFmpeg; PNG frames without it), the deformed part as STL / PLY, and a self-contained HTML report with every output.
/// The view state (increment, field) is restored afterwards.
/// </summary>
public sealed class ExportController(MainWindow window, MainViewModel vm)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private FrameworkElement ActiveView => vm.Is3D ? window.View3D : window.View2D;

    private FrameworkElement[] Content(VideoContent c) => c switch
    {
        VideoContent.ViewAndTelemetry => [window.ResultsPanel, window.TelemetryPanel],
        VideoContent.Window => [(FrameworkElement)window.Content],
        _ => [ActiveView],
    };

    /// <summary>Lets bindings, layout and rendering catch up after the view state changed.</summary>
    private async Task SettleAsync()
    {
        vm.FlushPlot();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        window.UpdateLayout();
    }

    private async Task<T> WithViewStateAsync<T>(Func<Task<T>> body)
    {
        int scrub = vm.ScrubIndex;
        var field = vm.SelectedField;
        bool cut = vm.CutAway; // selecting Grain flow switches the section on
        vm.IsExporting = true;
        try
        {
            return await body();
        }
        finally
        {
            vm.SelectedField = field;
            vm.CutAway = cut;
            vm.ScrubIndex = scrub;
            vm.IsExporting = false;
            await SettleAsync();
        }
    }

    // ------------------------------------------------------------------ image

    public void SaveImage(string path, VideoContent content, double scale)
    {
        var els = Content(content);
        FrameCapture.SavePng(els.Length == 1 ? FrameCapture.RenderElement(els[0], scale) : FrameCapture.Render(scale, els), path);
        vm.SetStatus("Image saved to " + Path.GetFileName(path) + ".");
    }

    // ------------------------------------------------------------------ video

    /// <summary>Writes the ram descent as WebM (or PNG frames without FFmpeg). Returns the main output path.</summary>
    public Task<string> SaveVideoAsync(VideoOptions o, CancellationToken ct = default) => WithViewStateAsync(() => VideoCoreAsync(o, ct));

    private async Task<string> VideoCoreAsync(VideoOptions o, CancellationToken ct)
    {
        var snaps = vm.Snapshots;
        if (snaps.Count == 0) throw new InvalidOperationException("There are no results to record.");
        var els = Content(o.Content);
        BitmapSource Grab() => els.Length == 1 ? FrameCapture.RenderElement(els[0], o.Scale) : FrameCapture.Render(o.Scale, els);

        vm.ScrubIndex = 0;
        await SettleAsync();
        var first = Grab();
        int w = first.PixelWidth, h = first.PixelHeight;
        int holdStart = (int)Math.Round(0.5 * o.Fps), holdEnd = (int)Math.Round(2.0 * o.Fps);
        string? ffmpeg = FfmpegLocator.Find();
        string output;
        if (ffmpeg is not null)
        {
            output = Path.ChangeExtension(o.Path, ".webm");
            using var writer = WebmWriter.Start(ffmpeg, output, w, h, o.Fps, o.Crf);
            for (int i = 0; i < snaps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                vm.ScrubIndex = i;
                await SettleAsync();
                var frame = FrameCapture.Bgra(i == 0 ? first : Grab(), w, h);
                int repeat = 1 + (i == 0 ? holdStart : 0) + (i == snaps.Count - 1 ? holdEnd : 0);
                for (int r = 0; r < repeat; r++) await writer.WriteFrameAsync(frame);
                vm.SetStatus(string.Format(Inv, "Recording video… frame {0} / {1}", i + 1, snaps.Count));
            }

            await writer.FinishAsync();
        }
        else
        {
            output = Path.Combine(Path.GetDirectoryName(o.Path) ?? "", Path.GetFileNameWithoutExtension(o.Path) + "_frames");
            Directory.CreateDirectory(output);
            for (int i = 0; i < snaps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                vm.ScrubIndex = i;
                await SettleAsync();
                FrameCapture.SavePng(Grab(), Path.Combine(output, string.Format(Inv, "frame_{0:0000}.png", i)));
                vm.SetStatus(string.Format(Inv, "Saving frames… {0} / {1} (FFmpeg not found: PNG sequence)", i + 1, snaps.Count));
            }
        }

        if (o.ExportTelemetry) SaveTelemetryBeside(output);
        vm.SetStatus($"Video saved: {Path.GetFileName(output)}{(o.ExportTelemetry ? " (+ telemetry CSV and setup)" : "")}.");
        return output;
    }

    private void SaveTelemetryBeside(string mediaPath)
    {
        string csv = Path.Combine(Path.GetDirectoryName(mediaPath) ?? "", Path.GetFileNameWithoutExtension(mediaPath) + "_telemetry.csv");
        if (vm.CurrentRun is { } run) vm.SaveTelemetry(csv, run);
        else TelemetryCsv.Write(csv, vm.Snapshots.Select(TelemetryRow.From));
    }

    // ------------------------------------------------------------------ deformed part

    /// <summary>Writes the deformed part of an increment (default: the one shown) as STL and / or PLY (by extension).</summary>
    public SurfaceMesh SaveSurface(string path, ForgingSnapshot? snapshot = null, bool both = false)
    {
        var s = snapshot ?? vm.CurrentSnapshot ?? vm.Snapshots.LastOrDefault() ?? throw new InvalidOperationException("There are no results to export.");
        var field = vm.SelectedField.Field;
        var mesh = DeformedSurface.Build(s, field, vm.ResultSymmetry, vm.IsPlaneStrainResult, vm.PartLength);
        double lo = vm.RangeMin, hi = vm.RangeMax;
        (byte, byte, byte) Color(double v)
        {
            double t = hi > lo ? Math.Clamp((v - lo) / (hi - lo), 0, 0.999) : 0;
            var c = ContourPalette.ColorOf((int)(t * ContourPalette.Bands));
            return (c.R, c.G, c.B);
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();
        string header = string.Format(Inv, "ImpressionFEA {0} step {1} stroke {2:F3} mm, mm units", AppVersion.Display, s.Step, s.Stroke);
        if (ext == ".ply" || both) DeformedSurface.WritePly(Path.ChangeExtension(path, ".ply"), mesh, Color, FieldName(field));
        if (ext != ".ply" || both) DeformedSurface.WriteStl(Path.ChangeExtension(path, ".stl"), mesh, header);
        vm.SetStatus(string.Format(Inv, "Deformed part (step {0}, {1:N0} triangles, {2:F2} cm³) saved to {3}.", s.Step, mesh.TriangleCount, DeformedSurface.Volume(mesh) / 1000, Path.GetFileName(path)));
        return mesh;
    }

    private static string FieldName(ResultField f) => f switch
    {
        ResultField.VonMisesStress => "von_mises_MPa",
        ResultField.EffectiveStrain => "effective_strain",
        ResultField.EffectiveStrainRate => "effective_strain_rate_1_s",
        ResultField.Damage => "damage_cockcroft_latham",
        _ => "mean_stress_MPa",
    };

    // ------------------------------------------------------------------ report

    /// <summary>Writes every output of the current results into a folder with a self-contained report.html; returns its path.</summary>
    public Task<string> GenerateReportAsync(ReportOptions o, CancellationToken ct = default) => WithViewStateAsync(async () =>
    {
        if (vm.Snapshots.Count == 0) throw new InvalidOperationException("There are no results to report.");
        var r = Describe(vm.CurrentRun);
        await CaptureRunAsync(r, o.Folder, fullResults: true, o.IncludeStages, o.IncludeSurface, o.IncludeVideo, liveChart: true, "Report", ct);
        if (vm.Runs.Count > 1)
        {
            vm.SaveAllTelemetry(Path.Combine(o.Folder, "runs"));
            r.Files.Add(("runs/summary.csv", "All runs of the session: summary"));
        }

        vm.SetStatus("Report: writing report.html…");
        string html = Path.Combine(o.Folder, "report.html");
        File.WriteAllText(html, ReportHtml.BuildSingle(r, vm.Runs.Reverse().ToList()));
        vm.SetStatus("Report written to " + o.Folder + ".");
        return html;
    });

    /// <summary>
    /// Writes a comparison report for several runs: report.html in <see cref="ComparisonOptions.Folder"/> holds the
    /// comparison page followed by each run's report in turn; each run's files (and its own standalone report) go into
    /// a runNN subfolder. Every run with full results is loaded into the viewport in turn; the run shown before is put back.
    /// </summary>
    public Task<string> GenerateComparisonAsync(ComparisonOptions o, CancellationToken ct = default) => WithViewStateAsync(async () =>
    {
        var shownBefore = vm.ShownRun;
        try
        {
            return await ComparisonCoreAsync(o, ct);
        }
        finally
        {
            vm.RestoreShownRun(shownBefore);
        }
    });

    private async Task<string> ComparisonCoreAsync(ComparisonOptions o, CancellationToken ct)
    {
        var runs = o.Runs.OrderBy(r => r.Number).ToList();
        if (runs.Count == 0) throw new InvalidOperationException("No runs selected for the report.");
        Directory.CreateDirectory(o.Folder);
        string img = Directory.CreateDirectory(Path.Combine(o.Folder, "images")).FullName;
        var c = new ComparisonReport { Field = vm.SelectedField.Name };
        var varied = RunComparison.Split(runs.Select(r => r.Settings)).Varied;

        for (int i = 0; i < runs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var run = runs[i];
            var r = Describe(run);
            r.Anchor = string.Format(Inv, "run{0}", run.Number);
            r.LinkPrefix = string.Format(Inv, "run{0:00}/", run.Number);
            r.LegendName = RunComparison.LegendName(run, varied);
            string folder = Path.Combine(o.Folder, r.LinkPrefix.TrimEnd('/'));
            string prefix = string.Format(Inv, "Report {0} / {1} (run #{2})", i + 1, runs.Count, run.Number);
            if (run.CanShow)
            {
                vm.ShowRunForExport(run);
                await SettleAsync();
            }

            if (o.IndividualReports)
            {
                await CaptureRunAsync(r, folder, run.CanShow, o.IncludeStages, o.IncludeSurface, o.IncludeVideo, liveChart: false, prefix, ct);
                File.WriteAllText(Path.Combine(folder, "report.html"), ReportHtml.BuildSingle(r, []));
            }
            else
            {
                await CaptureBasicsAsync(r, folder, liveChart: false, prefix);
                if (run.CanShow) await CaptureShapeAsync(r, Path.Combine(folder, "images"));
            }

            c.Runs.Add(r);
        }

        // Comparison charts and summary.
        vm.SetStatus("Report: comparison charts…");
        var chartRuns = c.Runs.Select(r => new ChartRun(r.LegendName, r.Rows, r.Run!.Color)).ToList();
        var capacities = runs.Select(r => r.Settings.PressCapacity).Distinct().ToList();
        c.Capacity = capacities.Count == 1 ? capacities[0] : 0;
        c.LoadChart = Chart(() => ReportCharts.LoadOverlay(chartRuns, c.Capacity), Path.Combine(img, "compare_load.png"));
        c.FillChart = Chart(() => ReportCharts.FillOverlay(chartRuns), Path.Combine(img, "compare_fill.png"));
        if (runs.Count > 2 && RunComparison.SweepParameter(varied) is { } p)
        {
            var points = runs.Select(r => (p.Value!(r.Settings)!.Value, r.PeakLoad, r.FinalFill)).ToList();
            c.SweepParameter = p.Name;
            c.SweepChart = Chart(() => ReportCharts.Sweep(p.AxisTitle, points, c.Capacity), Path.Combine(img, "compare_sweep.png"));
        }

        MainViewModel.WriteRunSummary(Path.Combine(o.Folder, "summary.csv"), runs);
        c.Files.Add(("summary.csv", "Summary of the compared runs (one row per run; mm, t, °C)"));
        foreach (var r in c.Runs)
            c.Files.Add((r.LinkPrefix, r.Run!.Label + (o.IndividualReports ? " — standalone report.html, telemetry, setup, log and figures" : " — telemetry and setup")));

        vm.SetStatus("Report: writing report.html…");
        string html = Path.Combine(o.Folder, "report.html");
        File.WriteAllText(html, ReportHtml.BuildComparison(c));
        vm.SetStatus(string.Format(Inv, "Comparison report of {0} runs written to {1}.", runs.Count, o.Folder));
        return html;
    }

    /// <summary>Report data of a run (null: the results in the viewport that are not a stored run).</summary>
    private RunReport Describe(RunRecord? run)
    {
        var ctx = run?.Context;
        return new RunReport
        {
            Run = run,
            Settings = run?.Settings ?? vm.CaptureSettings(),
            Rows = run?.Rows ?? vm.Snapshots.Select(TelemetryRow.From).ToList(),
            Geometry = ctx?.Geometry ?? vm.GeometryName,
            Description = ctx?.Description ?? vm.DieDescriptionText,
            MaterialSummary = ctx?.MaterialSummary ?? vm.MaterialSummary,
            Analysis = ctx?.Analysis ?? vm.AnalysisText,
            MaterialSource = vm.Materials.FirstOrDefault(m => m.Name == (run?.Settings ?? vm.CaptureSettings()).Material)?.Source ?? "",
            Log = run is null ? vm.RunLog : run.Results?.Log ?? "",
        };
    }

    /// <summary>Telemetry, setup, log and load / fill chart of a run.</summary>
    private async Task CaptureBasicsAsync(RunReport r, string folder, bool liveChart, string prefix)
    {
        Directory.CreateDirectory(folder);
        string img = Directory.CreateDirectory(Path.Combine(folder, "images")).FullName;
        vm.SetStatus(prefix + ": telemetry, setup and log…");
        string csv = Path.Combine(folder, "telemetry.csv");
        if (r.Run is { } run)
        {
            vm.SaveTelemetry(csv, run);
        }
        else
        {
            TelemetryCsv.Write(csv, r.Rows);
            r.Settings.Save(Path.ChangeExtension(csv, null) + RunSettings.FileExtension);
        }

        r.Files.Add(("telemetry.csv", "Telemetry, one row per increment (CSV)"));
        r.Files.Add(("telemetry" + RunSettings.FileExtension, "Setup to reopen or rerun this job"));
        if (r.Log.Length > 0)
        {
            File.WriteAllText(Path.Combine(folder, "solver.log"), r.Log);
            r.Files.Add(("solver.log", "Complete solver log"));
        }

        if (liveChart)
        {
            // The live chart, with the overlays currently shown.
            vm.ScrubIndex = vm.Snapshots.Count - 1;
            await SettleAsync();
            r.Chart = Figure(k => FrameCapture.RenderElement(window.ChartElement, k), 3, 2, Path.Combine(img, "telemetry_chart.png"));
        }
        else
        {
            var color = r.Run?.Color ?? OxyPlot.OxyColor.FromRgb(0x0B, 0x5C, 0xAD);
            r.Chart = Chart(() => ReportCharts.Run(new ChartRun(r.LegendName, r.Rows, color), r.Settings.PressCapacity), Path.Combine(img, "telemetry_chart.png"));
        }
    }

    /// <summary>The field selected in the main window at die closure (the comparison page's shape gallery).</summary>
    private async Task CaptureShapeAsync(RunReport r, string img)
    {
        vm.ScrubIndex = vm.Snapshots.Count - 1;
        await SettleAsync();
        r.Shape = Figure(k => FrameCapture.RenderElement(ActiveView, k), 3, 1, Path.Combine(img, "final_shape.png"));
    }

    /// <summary>
    /// Writes a run's outputs into <paramref name="folder"/> and fills <paramref name="r"/>. With
    /// <paramref name="fullResults"/> the run must be the one in the viewport: its fields, stages, part and video are added.
    /// </summary>
    private async Task CaptureRunAsync(RunReport r, string folder, bool fullResults, bool stages, bool surface, bool video, bool liveChart, string prefix, CancellationToken ct)
    {
        await CaptureBasicsAsync(r, folder, liveChart, prefix);
        if (!fullResults) return;

        string img = Path.Combine(folder, "images");
        var snaps = vm.Snapshots;
        var originalField = vm.SelectedField;
        await CaptureShapeAsync(r, img);

        // Every field at the final increment.
        bool cutBefore = vm.CutAway;
        foreach (var f in vm.Fields)
        {
            ct.ThrowIfCancellationRequested();
            vm.SetStatus(prefix + ": " + f.Name + "…");
            vm.SelectedField = f;
            await SettleAsync();
            r.Fields.Add((f.Name, f.Unit, Figure(k => FrameCapture.RenderElement(ActiveView, k), 3, 1, Path.Combine(img, "final_" + (f.GrainFlow ? "grain_flow" : FieldName(f.Field)) + ".png"))));
        }

        vm.CutAway = cutBefore; // Grain flow (last) switched the section on

        // Stages of the descent for the field selected when the report was started.
        vm.SelectedField = originalField;
        if (stages)
        {
            double total = snaps[^1].Stroke;
            foreach (double frac in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
            {
                ct.ThrowIfCancellationRequested();
                int idx = Nearest(snaps, frac * total);
                vm.ScrubIndex = idx;
                await SettleAsync();
                var s = snaps[idx];
                var units = ImpressionFEA.App.Infrastructure.Units.Current;
                r.Stages.Add((string.Format(Inv, "{0:F0} % stroke · {1} · {2}", 100 * frac, units.Lf(s.Stroke), units.LoadfN(s.LoadNewton)), Figure(k => FrameCapture.RenderElement(ActiveView, k), 3, 0.75, Path.Combine(img, string.Format(Inv, "stage_{0:000}pct.png", 100 * frac)))));
            }
        }

        // Deformed part.
        if (surface)
        {
            vm.SetStatus(prefix + ": deformed part (STL / PLY)…");
            var mesh = SaveSurface(Path.Combine(folder, "deformed_final.stl"), snaps[^1], both: true);
            r.SurfaceTriangles = mesh.TriangleCount;
            r.SurfaceVolume = DeformedSurface.Volume(mesh);
            r.Files.Add(("deformed_final.stl", "Deformed part at the final increment (binary STL, mm)"));
            r.Files.Add(("deformed_final.ply", "Same, with " + originalField.Name + " colours and values (PLY)"));
        }

        // Video.
        if (video)
        {
            string media = await VideoCoreAsync(new VideoOptions(Path.Combine(folder, "ram_descent.webm"), VideoContent.ViewAndTelemetry, 10, 32, 1.0, false), ct);
            r.Video = Path.GetFileName(media);
            r.Files.Add((r.Video + (Directory.Exists(media) ? "/" : ""), Directory.Exists(media) ? "Ram descent as PNG frames (FFmpeg not found)" : "Ram descent video (WebM, VP9)"));
        }
    }

    private static int Nearest(IReadOnlyList<ForgingSnapshot> snaps, double stroke)
    {
        int best = 0;
        for (int i = 1; i < snaps.Count; i++)
        {
            if (Math.Abs(snaps[i].Stroke - stroke) < Math.Abs(snaps[best].Stroke - stroke)) best = i;
        }

        return best;
    }

    /// <summary>
    /// Saves a figure as a high-resolution PNG (<paramref name="fileScale"/> × screen) and returns a lighter copy
    /// (<paramref name="embedScale"/> ×, still sharp in print) to embed in the report, which links to the full file.
    /// </summary>
    private static (string File, string Base64) Figure(Func<double, BitmapSource> render, double fileScale, double embedScale, string path)
    {
        FrameCapture.SavePng(render(fileScale), path);
        return ("images/" + Path.GetFileName(path), FrameCapture.PngBase64(render(embedScale)));
    }

    private static (string File, string Base64) Chart(Func<OxyPlot.PlotModel> model, string path) =>
        Figure(k => ReportCharts.Render(model(), k), 3, 2, path);
}

/// <summary>Everything a run's report shows (standalone, or as one section of a comparison report).</summary>
public sealed class RunReport
{
    public RunRecord? Run { get; init; }

    public required RunSettings Settings { get; init; }

    public required IReadOnlyList<TelemetryRow> Rows { get; init; }

    public required string Geometry { get; init; }

    public required string Description { get; init; }

    public required string MaterialSummary { get; init; }

    public required string Analysis { get; init; }

    /// <summary>Where the material constants come from.</summary>
    public string MaterialSource { get; init; } = "";

    public string Log { get; init; } = "";

    /// <summary>Folder of this run's files relative to the comparison report ("" for a standalone report).</summary>
    public string LinkPrefix { get; set; } = "";

    /// <summary>HTML id of the run's section in a comparison report.</summary>
    public string Anchor { get; set; } = "run";

    /// <summary>Short name in chart legends.</summary>
    public string LegendName { get; set; } = "Load";

    public (string File, string Base64) Chart { get; set; }

    /// <summary>The field selected in the main window, at die closure (comparison gallery).</summary>
    public (string File, string Base64) Shape { get; set; }

    public List<(string Name, string Unit, (string File, string Base64) Image)> Fields { get; } = [];

    public List<(string Caption, (string File, string Base64) Image)> Stages { get; } = [];

    public List<(string Path, string What)> Files { get; } = [];

    public string? Video { get; set; }

    public int SurfaceTriangles { get; set; }

    public double SurfaceVolume { get; set; }
}

/// <summary>The comparison page of a multi-run report and the run reports that follow it.</summary>
public sealed class ComparisonReport
{
    public List<RunReport> Runs { get; } = [];

    /// <summary>Field of the shape gallery (the one selected in the main window).</summary>
    public required string Field { get; init; }

    /// <summary>Press capacity shared by every run, t (0: they differ).</summary>
    public double Capacity { get; set; }

    public (string File, string Base64) LoadChart { get; set; }

    public (string File, string Base64) FillChart { get; set; }

    public string? SweepParameter { get; set; }

    public (string File, string Base64) SweepChart { get; set; }

    public List<(string Path, string What)> Files { get; } = [];
}

