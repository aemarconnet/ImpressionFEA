using System.Windows;
using System.Windows.Input;
using ImpressionFEA.App.ViewModels;

namespace ImpressionFEA.App;

public partial class MainWindow : Window
{
    private const int MaxLogChars = 2_000_000;

    public MainWindow()
    {
        InitializeComponent();
        InitChartInteraction(LoadChart);
        // Esc aborts whatever is running: solve, multi-run or export.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is MainViewModel m && m.AbortAll()) e.Handled = true;
        };
        Title = $"ImpressionFEA {AppVersion.Display} — Closed-Die Impression Forging FEA";
        var vm = new MainViewModel();
        DataContext = vm;
        // Raised on the UI thread by the view model's DispatcherTimer.
        vm.LogAppended += (_, text) =>
        {
            if (LogBox.Text.Length + text.Length > MaxLogChars)
                LogBox.Text = LogBox.Text[(LogBox.Text.Length / 2)..];
            LogBox.AppendText(text);
            LogBox.ScrollToEnd();
        };
        vm.LogCleared += (_, _) => LogBox.Clear();
        Closing += (_, _) =>
        {
            if (vm.AbortCommand.CanExecute(null)) vm.AbortCommand.Execute(null);
        };

        // Headless smoke test:  ImpressionFEA.App.exe --capture out.png [--solve] [--die N]
        var args = Environment.GetCommandLineArgs();
        int cap = Array.IndexOf(args, "--capture");
        if (cap >= 0 && cap + 1 < args.Length)
        {
            int dieArg = Array.IndexOf(args, "--die");
            if (dieArg >= 0 && dieArg + 1 < args.Length && int.TryParse(args[dieArg + 1], out int d)) vm.SelectedDie = vm.DieSets[d];
            vm.Is3D = args.Contains("--3d");
            if (args.Contains("--analysis3d")) vm.Analysis3D = true;
            int die3 = Array.IndexOf(args, "--die3d");
            if (die3 >= 0 && die3 + 1 < args.Length && int.TryParse(args[die3 + 1], out int d3)) vm.SelectedDie3D = vm.DieSets3D[d3];
            int imp = Array.IndexOf(args, "--import");
            string? stepFile = imp >= 0 && imp + 1 < args.Length ? args[imp + 1] : null;
            int demoArg = Array.IndexOf(args, "--demo");
            int demoIndex = demoArg >= 0 && demoArg + 1 < args.Length && int.TryParse(args[demoArg + 1], out int di) ? di : -1;
            int tabArg = Array.IndexOf(args, "--tab");
            int tabIndex = tabArg >= 0 && tabArg + 1 < args.Length && int.TryParse(args[tabArg + 1], out int ti) ? ti : 0;
            Loaded += async (_, _) =>
            {
                if (stepFile is not null) await vm.ImportStepAsync(stepFile, a => a.SuggestedTop);
                // Smoke test of the CAD loop: --import a.step --replace-with b.step copies b over a, then re-imports.
                int rep = Array.IndexOf(args, "--replace-with");
                if (stepFile is not null && rep >= 0 && rep + 1 < args.Length)
                {
                    vm.Temperature = 1111; // a non-default value that must survive the re-import
                    System.IO.File.Copy(args[rep + 1], stepFile, overwrite: true);
                    await Task.Delay(2000); // let the file watcher notice
                    await vm.ReimportAsync(solveAfter: false);
                }
                if (demoIndex >= 0 && demoIndex < vm.Demos.Count)
                {
                    var demo = vm.Demos[demoIndex];
                    await vm.LoadDemoAsync(demo);
                    if (args.Contains("--sweep") && demo.Sweep is { } sw && Enum.TryParse<SweepParameter>(sw.Parameter, true, out var sp))
                        await vm.RunSweepAsync(sp, vm.DemoSweepText(sw).Split(", "), null);
                }

                if (tabIndex > 0 && FindName("ConfigTabs") is System.Windows.Controls.TabControl tabs) tabs.SelectedIndex = tabIndex;
                if (Array.IndexOf(args, "--list-tab") is int lt && lt >= 0 && lt + 1 < args.Length && int.TryParse(args[lt + 1], out int li)) ListTabs.SelectedIndex = li;
                ApplySetArgs(vm, args);
                int show = Array.IndexOf(args, "--show-run");
                if (show >= 0 && show + 1 < args.Length && int.TryParse(args[show + 1], out int runNo) && vm.Runs.FirstOrDefault(r => r.Number == runNo) is { } run)
                    vm.SelectedRun = run;
                await CaptureAsync(vm, args[cap + 1], args.Contains("--solve"));
            };
        }
    }

    private async void ImportStep_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import STEP assembly (bottom die, workpiece, top die)",
            Filter = "STEP files (*.step;*.stp)|*.step;*.stp|All files (*.*)|*.*",
        };
        dlg.InitialDirectory = Infrastructure.AppSettings.Folder("step", FindSamples());
        if (!Ask(dlg, "step")) return;
        await ImportStepFile(dlg.FileName);
    }

    /// <summary>Imports a STEP assembly (from the menu or a drop), asking which outer body is the moving top die.</summary>
    private async Task ImportStepFile(string path)
    {
        Infrastructure.AppSettings.Remember("step", path);
        string? error = await Vm.ImportStepAsync(path, asm =>
        {
            var pick = new TopDieDialog(asm) { Owner = this };
            return pick.ShowDialog() == true ? pick.SelectedTop : null;
        });
        if (error is not null) MessageBox.Show(this, error, "STEP import", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void Warn(string? error, string title)
    {
        if (error is not null) MessageBox.Show(this, error, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void Reimport_Click(object sender, RoutedEventArgs e) =>
        Warn(await Vm.ReimportAsync(solveAfter: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)), "Re-import");

    private async void ReimportSolve_Click(object sender, RoutedEventArgs e) => Warn(await Vm.ReimportAsync(solveAfter: true), "Re-import");

    private static string SetupFilter => $"ImpressionFEA setup (*{Core.Simulation.RunSettings.FileExtension})|*{Core.Simulation.RunSettings.FileExtension}|JSON (*.json)|*.json";

    private async void OpenSetup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Open setup", Filter = SetupFilter };
        if (!Ask(dlg, "setup")) return;
        Warn(await Vm.OpenSetupAsync(dlg.FileName), "Open setup");
    }

    private void SaveSetup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save setup", Filter = SetupFilter, FileName = "setup" + Core.Simulation.RunSettings.FileExtension };
        if (!Ask(dlg, "setup")) return;
        try
        {
            Vm.SaveSetup(dlg.FileName);
        }
        catch (System.IO.IOException ex)
        {
            Warn(ex.Message, "Save setup");
        }
    }

    /// <summary>RUNS right-click → Delete run: removes that run (the one under the mouse, not necessarily the selected one).</summary>
    private void DeleteRun_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RunRecord run) DeleteRun(run);
    }

    /// <summary>Del in RUNS deletes the selected run.</summary>
    private void Runs_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Vm.SelectedRun is not { } run) return;
        DeleteRun(run);
        e.Handled = true;
    }

    private void DeleteRun(RunRecord run)
    {
        if (!Vm.DeleteRun(run)) Warn("Runs can't be deleted while a simulation, multi-run or export is in progress.", "Delete run");
    }

    private void SaveTelemetry_Click(object sender, RoutedEventArgs e)
    {
        var run = Vm.SelectedRun ?? Vm.Runs.FirstOrDefault();
        if (run is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = $"Save telemetry of run #{run.Number}",
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"run{run.Number:000}.csv",
        };
        if (!Ask(dlg, "telemetry")) return;
        try
        {
            Vm.SaveTelemetry(dlg.FileName, run);
        }
        catch (System.IO.IOException ex)
        {
            Warn(ex.Message, "Save telemetry");
        }
    }

    private void SaveAllTelemetry_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for all runs (CSV per run + summary.csv)" };
        if (!Ask(dlg, "telemetry")) return;
        try
        {
            Vm.SaveAllTelemetry(dlg.FolderName);
        }
        catch (System.IO.IOException ex)
        {
            Warn(ex.Message, "Save telemetry");
        }
    }

    private async void MultiRun_Click(object sender, RoutedEventArgs e) => await StartMultiRun(null, null);

    private async Task StartMultiRun(SweepParameter? parameter, string? values)
    {
        var dlg = new MultiRunDialog(Vm, parameter, values) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (dlg.ToQueue)
        {
            Warn(await Vm.AddSweepToQueueAsync(dlg.Parameter, dlg.Values), "Multi-run to queue");
            ShowQueueTab();
            return;
        }

        Infrastructure.AppSettings.Remember("multirun", dlg.Folder);
        Warn(await Vm.RunSweepAsync(dlg.Parameter, dlg.Values, dlg.Folder), "Multi-run");
    }

    private async void LoadDemo_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedDemo is not { } demo) return;
        Warn(await Vm.LoadDemoAsync(demo), "Load demo");
    }

    private async void DemoSweep_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedDemo is not { } demo) return;
        string? err = await Vm.LoadDemoAsync(demo);
        if (err is not null)
        {
            Warn(err, "Load demo");
            return;
        }

        SweepParameter? p = demo.Sweep is { } s && Enum.TryParse<SweepParameter>(s.Parameter, true, out var sp) ? sp : null;
        await StartMultiRun(p, demo.Sweep is { } sw ? Vm.DemoSweepText(sw) : null);
    }

    private void OpenDemoFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = Infrastructure.DemoCatalog.EnsureUserFolder();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    // ------------------------------------------------------------------ exports

    private Export.ExportController? _exporter;

    private Export.ExportController Exporter => _exporter ??= new Export.ExportController(this, Vm);

    /// <summary>File-name stem for exports: geometry, material, temperature and time.</summary>
    private string ExportBaseName()
    {
        string geo = string.Concat(Vm.GeometryName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')).Trim('_');
        var s = Vm.CurrentRun?.Settings ?? Vm.CaptureSettings();
        return FormattableString.Invariant($"{geo}_{s.Temperature:F0}C_{DateTime.Now:yyyyMMdd_HHmm}");
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: { } menu } b)
        {
            menu.PlacementTarget = b;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.DataContext = DataContext;
            menu.IsOpen = true;
        }
    }

    private void CompareReport_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Runs.Count == 0) return;
        var dlg = new Export.ComparisonReportDialog(Vm.Runs, ExportBaseName() + "_comparison") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        RunComparisonReport(dlg.Options);
    }

    /// <summary>Run context menu: a report of that one run (standalone report in its folder).</summary>
    private void RunReport_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RunRecord run) return;
        var dlg = new Export.ReportDialog(ExportBaseName() + FormattableString.Invariant($"_run{run.Number:00}_report")) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var o = dlg.Options;
        RunComparisonReport(new Export.ComparisonOptions(o.Folder, [run], true, o.IncludeStages, o.IncludeSurface, o.IncludeVideo, o.OpenWhenDone), single: true);
    }

    private void RunComparisonReport(Export.ComparisonOptions o, bool single = false)
    {
        Infrastructure.AppSettings.Remember("report", System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(o.Folder)));
        RunExport(async ct =>
        {
            string html = await Exporter.GenerateComparisonAsync(o, ct);
            if (single) html = System.IO.Path.Combine(o.Folder, FormattableString.Invariant($"run{o.Runs[0].Number:00}"), "report.html");
            if (o.OpenWhenDone) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(html) { UseShellExecute = true });
            return html;
        }, single ? "Run report" : "Comparison report");
    }

    private void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save image", Filter = "PNG image (*.png)|*.png", FileName = ExportBaseName() + ".png" };
        if (!Ask(dlg, "image")) return;
        RunExport(_ =>
        {
            Exporter.SaveImage(dlg.FileName, Export.VideoContent.View, 2);
            return Task.FromResult<string?>(null);
        }, "Save image");
    }

    private void SaveVideo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Export.VideoExportDialog(ExportBaseName()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var o = dlg.Options;
        Infrastructure.AppSettings.Remember("video", o.Path);
        RunExport(async ct => await Exporter.SaveVideoAsync(o, ct), "Save video");
    }

    private void SaveSurface_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save deformed part (increment shown)",
            Filter = "STL, binary (*.stl)|*.stl|PLY with result colours (*.ply)|*.ply|Both STL and PLY (*.stl)|*.stl",
            FileName = ExportBaseName() + $"_step{Vm.CurrentSnapshot?.Step ?? 0}.stl",
        };
        if (!Ask(dlg, "surface")) return;
        RunExport(_ =>
        {
            Exporter.SaveSurface(dlg.FileName, both: dlg.FilterIndex == 3);
            return Task.FromResult<string?>(null);
        }, "Save deformed part");
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Export.ReportDialog(ExportBaseName() + "_report") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var o = dlg.Options;
        Infrastructure.AppSettings.Remember("report", System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(o.Folder)));
        RunExport(async ct =>
        {
            string html = await Exporter.GenerateReportAsync(o, ct);
            if (o.OpenWhenDone) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(html) { UseShellExecute = true });
            return html;
        }, "Generate report");
    }

    private async void RunExport(Func<CancellationToken, Task<string?>> work, string title)
    {
        using var cts = new CancellationTokenSource();
        Vm.ExportCancellation = cts;
        try
        {
            await work(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Vm.SetStatus(title + " cancelled (Esc). Files written so far are kept.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            Vm.SetStatus(title + " failed: " + ex.Message);
            Warn(ex.Message, title);
        }
        finally
        {
            Vm.ExportCancellation = null;
        }
    }

    /// <summary>
    /// Shows a file / folder dialog starting in the folder last used for this kind of file, and remembers the new
    /// choice. Returns true when the user confirmed.
    /// </summary>
    private bool Ask(Microsoft.Win32.CommonItemDialog dlg, string kind)
    {
        if (string.IsNullOrEmpty(dlg.InitialDirectory)) dlg.InitialDirectory = Infrastructure.AppSettings.Folder(kind) ?? "";
        if (dlg.ShowDialog(this) != true) return false;
        Infrastructure.AppSettings.Remember(kind, dlg is Microsoft.Win32.OpenFolderDialog f ? f.FolderName : ((Microsoft.Win32.FileDialog)dlg).FileName);
        return true;
    }

    /// <summary>The repository / install "samples" folder, if one is found above the executable.</summary>
    private static string? FindSamples()
    {
        for (var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string p = System.IO.Path.Combine(d.FullName, "samples");
            if (System.IO.Directory.Exists(p)) return p;
        }

        return null;
    }

    /// <summary>
    /// Jobs from CAD (SolidWorks add-in), from this process's command line or forwarded from a second launch:
    /// --job &lt;file.step | setup.ifea.json&gt; opens one configuration; --configs &lt;folder&gt; [--template &lt;setup&gt;] runs them all.
    /// </summary>
    public async Task HandleExternalAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; // bring to front over SolidWorks
        Topmost = false;
        if (Vm.IsRunning || Vm.IsSweeping)
        {
            Warn("A simulation is running. Finish or abort it (Esc), then send the job again.", "ImpressionFEA");
            return;
        }

        Core.Cad.CadBody? Choose(Core.Cad.ForgingAssembly asm)
        {
            var pick = new TopDieDialog(asm) { Owner = this };
            return pick.ShowDialog() == true ? pick.SelectedTop : null;
        }

        string? Arg(string name) => Array.IndexOf(args, name) is int i && i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        if (Arg("--configs") is { } folder) Warn(await Vm.RunConfigurationsAsync(folder, Arg("--template"), Choose), "Configurations");
        else if (Arg("--job") is { } job) Warn(await Vm.OpenJobAsync(job, Choose), "CAD job");
    }

    /// <summary>Smoke tests: --set Name=Value (repeatable) sets a view-model property (bool, number, enum or text).</summary>
    private static void ApplySetArgs(MainViewModel vm, string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] != "--set") continue;
            var kv = args[i + 1].Split('=', 2);
            var prop = typeof(MainViewModel).GetProperty(kv[0]);
            if (prop is null || !prop.CanWrite || kv.Length < 2) continue;
            var t = prop.PropertyType;
            object value = t.IsEnum ? Enum.Parse(t, kv[1], true) : Convert.ChangeType(kv[1], t, System.Globalization.CultureInfo.InvariantCulture);
            prop.SetValue(vm, value);
        }
    }

    private async Task CaptureAsync(MainViewModel vm, string path, bool solve)
    {
        // --window 1344x756 sizes the window first (layout checks at small screen sizes).
        var size = Environment.GetCommandLineArgs().SkipWhile(a => a != "--window").Skip(1).FirstOrDefault()?.Split('x');
        if (size is [{ } w, { } h] && double.TryParse(w, out double ww) && double.TryParse(h, out double hh))
        {
            WindowState = WindowState.Normal;
            Width = ww;
            Height = hh;
            UpdateLayout();
        }

        if (solve)
        {
            vm.SolveCommand.Execute(null);
            await Task.Delay(500);
            while (vm.IsRunning) await Task.Delay(200);
            await Task.Delay(500);
        }

        // --scrub N shows increment N (after the solve) for the capture.
        var argv = Environment.GetCommandLineArgs();
        int sc = Array.IndexOf(argv, "--scrub");
        if (sc >= 0 && sc + 1 < argv.Length && int.TryParse(argv[sc + 1], out int scrub)) vm.ScrubIndex = scrub;

        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // --chart-card out.png: the hold card for the middle of the first curve (popups are not in the window capture).
        if (Environment.GetCommandLineArgs().SkipWhile(a => a != "--chart-card").Skip(1).FirstOrDefault() is { } cardPng
            && vm.LoadPlot.Series.OfType<OxyPlot.Series.LineSeries>().FirstOrDefault(s => s.Points.Count > 2) is { } curve)
        {
            var at = curve.Transform(curve.Points[curve.Points.Count * 2 / 3]);
            if (vm.HitCurve(at) is { } hit)
            {
                var card = CardContent(hit);
                card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                card.Arrange(new Rect(card.DesiredSize));
                card.UpdateLayout();
                var cb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)card.ActualWidth + 20, (int)card.ActualHeight + 20, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                cb.Render(card);
                var ce = new System.Windows.Media.Imaging.PngBitmapEncoder();
                ce.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(cb));
                await using var cf = System.IO.File.Create(cardPng);
                ce.Save(cf);
            }
        }

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var root = (FrameworkElement)Content;
        root.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        await using (var fs = System.IO.File.Create(path)) enc.Save(fs);

        // --runs-folder DIR: every run's telemetry and a summary.csv (status, peak load, fill per run) for batch checks.
        if (Environment.GetCommandLineArgs().SkipWhile(a => a != "--runs-folder").Skip(1).FirstOrDefault() is { } runsFolder && vm.Runs.Count > 0)
            vm.SaveAllTelemetry(runsFolder);
        Application.Current.Shutdown(vm.State == RunState.Failed ? 1 : 0);
    }
}
