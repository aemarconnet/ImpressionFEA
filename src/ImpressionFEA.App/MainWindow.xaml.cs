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
                        await vm.RunSweepAsync(sp, sw.Values, null);
                }

                if (tabIndex > 0 && FindName("ConfigTabs") is System.Windows.Controls.TabControl tabs) tabs.SelectedIndex = tabIndex;
                await CaptureAsync(vm, args[cap + 1], args.Contains("--solve"));
            };
        }
    }

    private async void ImportStep_Click(object sender, RoutedEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import STEP assembly (bottom die, workpiece, top die)",
            Filter = "STEP files (*.step;*.stp)|*.step;*.stp|All files (*.*)|*.*",
        };
        string? samples = FindSamples();
        if (samples is not null) dlg.InitialDirectory = samples;
        if (dlg.ShowDialog(this) != true) return;
        string? error = await vm.ImportStepAsync(dlg.FileName, asm =>
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
        if (dlg.ShowDialog(this) != true) return;
        Warn(await Vm.OpenSetupAsync(dlg.FileName), "Open setup");
    }

    private void SaveSetup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save setup", Filter = SetupFilter, FileName = "setup" + Core.Simulation.RunSettings.FileExtension };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Vm.SaveSetup(dlg.FileName);
        }
        catch (System.IO.IOException ex)
        {
            Warn(ex.Message, "Save setup");
        }
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
        if (dlg.ShowDialog(this) != true) return;
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
        if (dlg.ShowDialog(this) != true) return;
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
        await StartMultiRun(p, demo.Sweep is { } sw ? string.Join(", ", sw.Values) : null);
    }

    private void OpenDemoFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = Vm.DemoFolder;
        if (!System.IO.Directory.Exists(folder)) System.IO.Directory.CreateDirectory(folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
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

    private async Task CaptureAsync(MainViewModel vm, string path, bool solve)
    {
        if (solve)
        {
            vm.SolveCommand.Execute(null);
            await Task.Delay(500);
            while (vm.IsRunning) await Task.Delay(200);
            await Task.Delay(500);
        }

        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var root = (FrameworkElement)Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        await using (var fs = System.IO.File.Create(path)) enc.Save(fs);
        Application.Current.Shutdown(vm.State == RunState.Failed ? 1 : 0);
    }
}
