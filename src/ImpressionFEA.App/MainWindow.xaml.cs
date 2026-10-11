using System.Windows;
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
            Loaded += async (_, _) =>
            {
                if (stepFile is not null) await vm.ImportStepAsync(stepFile, a => a.SuggestedTop);
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
