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
        vm.IsExporting = true;
        try
        {
            return await body();
        }
        finally
        {
            vm.SelectedField = field;
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
    public Task<string> GenerateReportAsync(ReportOptions o, CancellationToken ct = default) => WithViewStateAsync(() => ReportCoreAsync(o, ct));

    private async Task<string> ReportCoreAsync(ReportOptions o, CancellationToken ct)
    {
        var snaps = vm.Snapshots;
        if (snaps.Count == 0) throw new InvalidOperationException("There are no results to report.");
        Directory.CreateDirectory(o.Folder);
        string img = Directory.CreateDirectory(Path.Combine(o.Folder, "images")).FullName;
        var r = new ReportData { Vm = vm, Folder = o.Folder, Last = snaps[^1] };
        var originalField = vm.SelectedField;

        // Telemetry, setup and log.
        vm.SetStatus("Report: telemetry, setup and log…");
        string csv = Path.Combine(o.Folder, "telemetry.csv");
        if (vm.CurrentRun is { } run) vm.SaveTelemetry(csv, run);
        else TelemetryCsv.Write(csv, snaps.Select(TelemetryRow.From));
        r.Files.Add(("telemetry.csv", "Telemetry, one row per increment (CSV)"));
        r.Files.Add((Path.GetFileName(Path.ChangeExtension(csv, null)) + RunSettings.FileExtension, "Setup to reopen or rerun this job"));
        File.WriteAllText(Path.Combine(o.Folder, "solver.log"), vm.RunLog);
        r.Files.Add(("solver.log", "Complete solver log"));
        if (vm.Runs.Count > 1)
        {
            vm.SaveAllTelemetry(Path.Combine(o.Folder, "runs"));
            r.Files.Add(("runs/summary.csv", "All runs of the session: summary"));
        }

        // Chart (with the overlays currently shown).
        vm.ScrubIndex = snaps.Count - 1;
        await SettleAsync();
        r.Chart = Save(FrameCapture.RenderElement(window.LoadChart, 2), Path.Combine(img, "telemetry_chart.png"));

        // Every field at the final increment.
        foreach (var f in vm.Fields)
        {
            ct.ThrowIfCancellationRequested();
            vm.SetStatus("Report: " + f.Name + "…");
            vm.SelectedField = f;
            await SettleAsync();
            r.Fields.Add((f.Name, f.Unit, Save(FrameCapture.RenderElement(ActiveView, 1.5), Path.Combine(img, "final_" + FieldName(f.Field) + ".png"))));
        }

        // Stages of the descent for the field selected when the report was started.
        vm.SelectedField = originalField;
        if (o.IncludeStages)
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
                r.Stages.Add((string.Format(Inv, "{0:F0} % stroke · {1} · {2}", 100 * frac, units.Lf(s.Stroke), units.LoadfN(s.LoadNewton)), Save(FrameCapture.RenderElement(ActiveView, 1.2), Path.Combine(img, string.Format(Inv, "stage_{0:000}pct.png", 100 * frac)))));
            }
        }

        // Deformed part.
        if (o.IncludeSurface)
        {
            vm.SetStatus("Report: deformed part (STL / PLY)…");
            var mesh = SaveSurface(Path.Combine(o.Folder, "deformed_final.stl"), snaps[^1], both: true);
            r.SurfaceTriangles = mesh.TriangleCount;
            r.SurfaceVolume = DeformedSurface.Volume(mesh);
            r.Files.Add(("deformed_final.stl", "Deformed part at the final increment (binary STL, mm)"));
            r.Files.Add(("deformed_final.ply", "Same, with " + originalField.Name + " colours and values (PLY)"));
        }

        // Video.
        if (o.IncludeVideo)
        {
            string media = await VideoCoreAsync(new VideoOptions(Path.Combine(o.Folder, "ram_descent.webm"), VideoContent.ViewAndTelemetry, 10, 32, 1.0, false), ct);
            r.Video = Path.GetFileName(media);
            r.Files.Add((r.Video + (Directory.Exists(media) ? "/" : ""), Directory.Exists(media) ? "Ram descent as PNG frames (FFmpeg not found)" : "Ram descent video (WebM, VP9)"));
        }

        vm.SetStatus("Report: writing report.html…");
        string html = Path.Combine(o.Folder, "report.html");
        File.WriteAllText(html, ReportHtml.Build(r));
        vm.SetStatus("Report written to " + o.Folder + ".");
        return html;
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

    private static (string File, string Base64) Save(BitmapSource bmp, string path)
    {
        FrameCapture.SavePng(bmp, path);
        return ("images/" + Path.GetFileName(path), FrameCapture.PngBase64(bmp));
    }
}

/// <summary>Everything the HTML report shows.</summary>
public sealed class ReportData
{
    public required MainViewModel Vm { get; init; }

    public required string Folder { get; init; }

    public required ForgingSnapshot Last { get; init; }

    public (string File, string Base64) Chart { get; set; }

    public List<(string Name, string Unit, (string File, string Base64) Image)> Fields { get; } = [];

    public List<(string Caption, (string File, string Base64) Image)> Stages { get; } = [];

    public List<(string Path, string What)> Files { get; } = [];

    public string? Video { get; set; }

    public int SurfaceTriangles { get; set; }

    public double SurfaceVolume { get; set; }
}
