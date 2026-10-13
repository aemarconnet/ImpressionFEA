using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ImpressionFEA.App.ViewModels;
using ImpressionFEA.Core.Simulation;

namespace ImpressionFEA.App;

/// <summary>Panel collapsing, the pop-out chart, drag and drop of files, and the queue list.</summary>
public partial class MainWindow
{
    // ------------------------------------------------------------------ collapsible panels

    private readonly Dictionary<string, GridLength> _expandedSize = [];

    /// <summary>Double-click on a panel's drag handle collapses that panel; again restores it.</summary>
    private void Splitter_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string which }) return;
        e.Handled = true;
        switch (which)
        {
            case "Left":
                ToggleColumn(which, LeftCol, 250, 330);
                break;
            case "Right":
                ToggleColumn(which, RightCol, 300, 390);
                break;
            case "List":
                ToggleRow(which, ListRow, collapsedHeight: 0, defaultHeight: 150);
                break;
            case "Bottom":
                ToggleRow(which, BottomRow, collapsedHeight: ExecBar.ActualHeight + 2, defaultHeight: 170);
                break;
        }
    }

    private void ToggleColumn(string key, ColumnDefinition col, double minWidth, double defaultWidth)
    {
        if (col.ActualWidth < 30)
        {
            col.MinWidth = minWidth;
            col.Width = _expandedSize.Remove(key, out var w) && w.Value >= minWidth ? w : new GridLength(defaultWidth);
        }
        else
        {
            _expandedSize[key] = col.Width;
            col.MinWidth = 0;
            col.Width = new GridLength(0);
        }
    }

    private void ToggleRow(string key, RowDefinition row, double collapsedHeight, double defaultHeight)
    {
        if (row.ActualHeight < collapsedHeight + 30)
        {
            row.Height = _expandedSize.Remove(key, out var h) && h.Value > collapsedHeight + 30 ? h : new GridLength(defaultHeight);
        }
        else
        {
            _expandedSize[key] = row.Height;
            row.Height = new GridLength(collapsedHeight);
        }
    }

    /// <summary>Buttons with a drop-down menu (File ▾, ⋯, Export): open the button's context menu below it.</summary>
    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } b) return;
        menu.PlacementTarget = b;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.DataContext = DataContext;
        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------ pop-out chart

    private Window? _chartWindow;
    private OxyPlot.Wpf.PlotView? _chartWindowPlot;
    private GridLength _listRowBeforePopOut;

    /// <summary>The chart as currently shown (in its own window or in the panel), for exports.</summary>
    internal FrameworkElement ChartElement => _chartWindowPlot ?? (FrameworkElement)LoadChart;

    private void PopOutChart_Click(object sender, RoutedEventArgs e)
    {
        if (_chartWindow is not null)
        {
            _chartWindow.Activate();
            return;
        }

        LoadChart.Model = null; // a plot model can only be shown by one view at a time
        _chartWindowPlot = new OxyPlot.Wpf.PlotView { Model = Vm.LoadPlot, Background = System.Windows.Media.Brushes.White, Margin = new Thickness(6) };
        InitChartInteraction(_chartWindowPlot);
        _chartWindow = new Window
        {
            Title = "ImpressionFEA — load and cavity fill (double-click a curve to load that run)",
            Owner = this,
            Width = 960,
            Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Icon = Icon,
            Content = _chartWindowPlot,
        };
        _chartWindow.Closed += (_, _) => DockChart();
        _chartWindow.Show();

        LoadChart.Visibility = Visibility.Collapsed;
        ChartDockedNote.Visibility = Visibility.Visible;
        PopOutText.Text = "Show chart window";
        ChartRow.MinHeight = 0;
        ChartRow.Height = GridLength.Auto;
        _listRowBeforePopOut = ListRow.Height;
        ListRow.Height = new GridLength(1, GridUnitType.Star);
    }

    private void DockChart_Click(object sender, RoutedEventArgs e) => _chartWindow?.Close();

    private void DockChart()
    {
        if (_chartWindowPlot is not null)
        {
            DetachChartInteraction(_chartWindowPlot);
            _chartWindowPlot.Model = null;
        }

        _chartWindow = null;
        _chartWindowPlot = null;
        LoadChart.SetBinding(OxyPlot.Wpf.PlotView.ModelProperty, new Binding(nameof(MainViewModel.LoadPlot)));
        LoadChart.Visibility = Visibility.Visible;
        ChartDockedNote.Visibility = Visibility.Collapsed;
        PopOutText.Text = "Pop out chart";
        ChartRow.MinHeight = 110;
        ChartRow.Height = new GridLength(1, GridUnitType.Star);
        ListRow.Height = _listRowBeforePopOut.IsStar || _listRowBeforePopOut.Value <= 0 ? new GridLength(150) : _listRowBeforePopOut;
        Vm.FlushPlot();
    }

    // ------------------------------------------------------------------ drag and drop

    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files ? files : [];

    private enum DropKind { None, Step, Setup, Queue }

    private static DropKind KindOf(string path) =>
        path.EndsWith(".step", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".stp", StringComparison.OrdinalIgnoreCase) ? DropKind.Step
        : path.EndsWith(MainViewModel.QueueFileExtension, StringComparison.OrdinalIgnoreCase) ? DropKind.Queue
        : path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? DropKind.Setup
        : DropKind.None;

    /// <summary>What a drop would do, or null when the files can't be dropped.</summary>
    private static string? DropAction(string[] files)
    {
        if (files.Length == 0) return null;
        var kinds = files.Select(KindOf).ToList();
        if (kinds.Contains(DropKind.None)) return null;
        int steps = kinds.Count(k => k == DropKind.Step);
        if (steps > 0) return files.Length == 1 ? "Import the STEP assembly" : null;
        if (kinds.All(k => k == DropKind.Queue)) return files.Length == 1 ? "Add the queue file to the queue" : "Add the queue files to the queue";
        if (kinds.All(k => k == DropKind.Setup)) return files.Length == 1 ? "Open the setup" : F($"Add {files.Length} setups to the queue");
        return "Add the setups and queues to the queue";
    }

    private static string F(FormattableString s) => s.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void Window_DragEnter(object sender, DragEventArgs e) => Window_DragOver(sender, e);

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        var files = DroppedFiles(e);
        if (files.Length == 0) return; // not files: let text boxes handle text drags
        string? action = DropAction(files);
        bool ok = action is not null && Vm.CanEditQueue;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropTitle.Text = ok ? action! : !Vm.CanEditQueue ? "Busy — wait for the simulation to finish" : "These files can't be opened";
        DropOverlay.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(this);
        if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight) DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        var files = DroppedFiles(e);
        if (files.Length == 0) return;
        e.Handled = true;
        if (DropAction(files) is null)
        {
            Warn("Drop one STEP assembly (.step / .stp), setups (.ifea.json) or queue files (" + MainViewModel.QueueFileExtension + ").", "Drop files");
            return;
        }

        if (!Vm.CanEditQueue)
        {
            Warn("Files can't be opened while a simulation, multi-run, queue or export is running.", "Drop files");
            return;
        }

        Activate();
        var kinds = files.Select(KindOf).ToList();
        if (kinds[0] == DropKind.Step)
        {
            await ImportStepFile(files[0]);
        }
        else if (files.Length == 1 && kinds[0] == DropKind.Setup)
        {
            Infrastructure.AppSettings.Remember("setup", files[0]);
            Warn(await Vm.OpenSetupAsync(files[0]), "Open setup");
        }
        else
        {
            foreach (var q in files.Where(f => KindOf(f) == DropKind.Queue)) Warn(Vm.LoadQueueFile(q, replace: false), "Open queue");
            var setups = files.Where(f => KindOf(f) == DropKind.Setup).ToList();
            if (setups.Count > 0) Warn(Vm.AddSetupsToQueue(setups), "Add setups to the queue");
            ShowQueueTab();
        }
    }

    // ------------------------------------------------------------------ queue list

    private void ShowQueueTab() => ListTabs.SelectedIndex = 1;

    private static QueueItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as QueueItem;

    private async void QueueList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not QueueItem item) return;
        if (item.IsPending) Warn(await Vm.LoadQueueItemAsync(item), "Queue");
        else Vm.ShowQueueResult(item);
    }

    private void QueueList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Vm.SelectedQueueItem is not { } item) return;
        Vm.RemoveQueueItem(item);
        e.Handled = true;
    }

    private async void QueueLoad_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Warn(await Vm.LoadQueueItemAsync(item), "Queue");
    }

    private void QueueShow_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Vm.ShowQueueResult(item);
    }

    private void QueueDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Vm.DuplicateQueueItem(item);
    }

    private void QueueUp_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Vm.MoveQueueItem(item, -1);
    }

    private void QueueDown_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Vm.MoveQueueItem(item, 1);
    }

    private void QueueRequeue_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Vm.RequeueItem(item);
    }

    private void QueueRemove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Vm.RemoveQueueItem(item);
    }

    /// <summary>RUNS right-click: queue that run's exact setup again.</summary>
    private void RunToQueue_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RunRecord run || !Vm.CanEditQueue) return;
        Vm.Queue.Add(new QueueItem(System.Text.RegularExpressions.Regex.Replace(run.Label, @"^#\d+\s*", ""), run.Settings));
        ShowQueueTab();
    }

    private static string SetupFiles => $"ImpressionFEA setups (*{RunSettings.FileExtension})|*{RunSettings.FileExtension}|JSON (*.json)|*.json";

    private static string QueueFiles => $"ImpressionFEA queue (*{MainViewModel.QueueFileExtension})|*{MainViewModel.QueueFileExtension}";

    private void QueueAddSetups_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Add setups to the queue", Filter = SetupFiles, Multiselect = true };
        if (!Ask(dlg, "setup")) return;
        Warn(Vm.AddSetupsToQueue(dlg.FileNames), "Add setups to the queue");
        ShowQueueTab();
    }

    private void QueueOpen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Open queue (added to the current queue)", Filter = QueueFiles };
        if (!Ask(dlg, "queue")) return;
        Warn(Vm.LoadQueueFile(dlg.FileName, replace: false), "Open queue");
        ShowQueueTab();
    }

    private void QueueSave_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save queue", Filter = QueueFiles, FileName = "queue" + MainViewModel.QueueFileExtension };
        if (!Ask(dlg, "queue")) return;
        try
        {
            Vm.SaveQueueFile(dlg.FileName);
            Vm.SetStatus("Queue saved to " + Path.GetFileName(dlg.FileName) + ".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn(ex.Message, "Save queue");
        }
    }

    // ------------------------------------------------------------------ materials

    /// <summary>Opens (creating it with an example if needed) the user materials file in the default editor.</summary>
    private void EditMaterials_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = Infrastructure.MaterialLibrary.EnsureUserFile();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            Vm.SetStatus("Edit materials.json, save it, then press Reload under the material list.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Warn("Could not open the materials file: " + ex.Message + Environment.NewLine + Infrastructure.MaterialLibrary.UserFile, "My materials");
        }
    }
}
