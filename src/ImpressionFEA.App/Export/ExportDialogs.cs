using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ImpressionFEA.App.Export;

/// <summary>Small helpers shared by the export dialogs.</summary>
internal static class DialogUi
{
    public static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x56, 0x60, 0x6C));

    public static Window Frame(Window w, string title)
    {
        w.Title = title;
        w.SizeToContent = SizeToContent.WidthAndHeight;
        w.ResizeMode = ResizeMode.NoResize;
        w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        w.Background = Brushes.White;
        return w;
    }

    public static TextBlock Label(string text, double top = 8) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 2) };

    public static TextBlock Note(string text) => new() { Text = text, Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };

    public static ComboBox Combo(params string[] items)
    {
        var c = new ComboBox { Margin = new Thickness(0, 0, 0, 2) };
        foreach (var i in items) c.Items.Add(i);
        c.SelectedIndex = 0;
        return c;
    }

    /// <summary>Text box with a Browse… button; <paramref name="browse"/> returns the chosen path or null.</summary>
    public static (DockPanel Row, TextBox Box) PathRow(string initial, Func<string, string?> browse)
    {
        var row = new DockPanel();
        var btn = new Button { Content = "Browse…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(btn, Dock.Right);
        var box = new TextBox { Text = initial, Padding = new Thickness(4, 3, 4, 3) };
        btn.Click += (_, _) =>
        {
            if (browse(box.Text) is { } p) box.Text = p;
        };
        row.Children.Add(btn);
        row.Children.Add(box);
        return (row, box);
    }

    public static StackPanel Buttons(Window w, string ok, Func<bool> validate)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var okb = new Button { Content = ok, IsDefault = true, MinWidth = 110, Padding = new Thickness(10, 4, 10, 4) };
        okb.Click += (_, _) =>
        {
            if (validate()) w.DialogResult = true;
        };
        buttons.Children.Add(okb);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 84, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) });
        return buttons;
    }

    public static string DefaultFolder()
    {
        string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ImpressionFEA");
        Directory.CreateDirectory(d);
        return d;
    }

    public static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture);
}

/// <summary>Options for a video of the ram descent.</summary>
public sealed class VideoExportDialog : Window
{
    private readonly ComboBox _content = DialogUi.Combo("3D / 2D view", "View + telemetry (chart, runs, read-outs)", "Whole window");
    private readonly ComboBox _fps = DialogUi.Combo("10 frames/s", "5 frames/s", "15 frames/s", "24 frames/s", "30 frames/s");
    private readonly ComboBox _quality = DialogUi.Combo("Standard", "High (larger file)", "Small file");
    private readonly ComboBox _size = DialogUi.Combo("Screen size", "2× (sharp, slower)");
    private readonly CheckBox _telemetry = new() { Content = "Also export the telemetry (CSV) and setup beside the video", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBox _path;

    public VideoExportDialog(string baseName)
    {
        DialogUi.Frame(this, "Save video of the ram descent");
        string? ffmpeg = FfmpegLocator.Find();
        var p = new StackPanel { Margin = new Thickness(18), Width = 520 };
        p.Children.Add(new TextBlock
        {
            Text = "Replays every increment of the current results and records it. Choose the field, view angle, lighting and edges in the main window first; they are used as shown.",
            TextWrapping = TextWrapping.Wrap,
        });
        p.Children.Add(DialogUi.Note(ffmpeg is not null
            ? "Encoder: FFmpeg (VP9 WebM) — " + ffmpeg
            : "FFmpeg was not found, so the frames are saved as a numbered PNG sequence. Install FFmpeg (e.g. winget install BtbN.FFmpeg.LGPL.Shared.9.0) or put ffmpeg.exe next to the app for WebM."));
        p.Children.Add(DialogUi.Label("Content"));
        p.Children.Add(_content);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        AddCell(grid, 0, "Frame rate", _fps);
        AddCell(grid, 2, "Quality", _quality);
        AddCell(grid, 4, "Resolution", _size);
        p.Children.Add(grid);
        p.Children.Add(_telemetry);
        p.Children.Add(DialogUi.Label("File", 12));
        var (row, box) = DialogUi.PathRow(Path.Combine(Infrastructure.AppSettings.Folder("video", DialogUi.DefaultFolder())!, baseName + ".webm"), cur =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "WebM video (*.webm)|*.webm", FileName = Path.GetFileName(cur), InitialDirectory = Path.GetDirectoryName(cur) };
            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
        });
        _path = box;
        p.Children.Add(row);
        p.Children.Add(DialogUi.Buttons(this, "Record", () => !string.IsNullOrWhiteSpace(_path.Text)));
        Content = p;
    }

    private static void AddCell(Grid g, int col, string label, Control c)
    {
        var s = new StackPanel();
        s.Children.Add(DialogUi.Label(label));
        s.Children.Add(c);
        Grid.SetColumn(s, col);
        g.Children.Add(s);
    }

    public VideoOptions Options => new(
        _path.Text.Trim(),
        (VideoContent)_content.SelectedIndex,
        _fps.SelectedIndex switch { 1 => 5, 2 => 15, 3 => 24, 4 => 30, _ => 10 },
        _quality.SelectedIndex switch { 1 => 24, 2 => 40, _ => 32 },
        _size.SelectedIndex == 1 ? 2.0 : 1.0,
        _telemetry.IsChecked == true);
}

/// <summary>Options for the full report.</summary>
public sealed class ReportDialog : Window
{
    private readonly CheckBox _stages = new() { Content = "Snapshots of the ram descent (0 / 25 / 50 / 75 / 100 % stroke)", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
    private readonly CheckBox _surface = new() { Content = "Deformed part at die closure (STL + coloured PLY)", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
    private readonly CheckBox _video = new() { Content = "Video of the ram descent (WebM; takes longest)", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
    private readonly CheckBox _open = new() { Content = "Open the report when done", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBox _folder;

    public ReportDialog(string baseName)
    {
        DialogUi.Frame(this, "Generate report");
        var p = new StackPanel { Margin = new Thickness(18), Width = 520 };
        p.Children.Add(new TextBlock
        {
            Text = "Writes every output of the current results into one folder: report.html (setup, key results, load / fill chart, all result fields, telemetry table, run comparison), telemetry.csv, the setup, the solver log and the figures as PNG.",
            TextWrapping = TextWrapping.Wrap,
        });
        p.Children.Add(DialogUi.Note("The figures use the current view angle, lighting and edge settings. The report opens in any browser and prints to PDF."));
        p.Children.Add(DialogUi.Label("Also include", 12));
        p.Children.Add(_stages);
        p.Children.Add(_surface);
        p.Children.Add(_video);
        if (FfmpegLocator.Find() is null) p.Children.Add(DialogUi.Note("FFmpeg not found: the video is saved as PNG frames."));
        p.Children.Add(_open);
        p.Children.Add(DialogUi.Label("Folder", 12));
        var (row, box) = DialogUi.PathRow(Path.Combine(Infrastructure.AppSettings.Folder("report", DialogUi.DefaultFolder())!, baseName), cur =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Parent folder for the report", InitialDirectory = Path.GetDirectoryName(cur) ?? "" };
            return dlg.ShowDialog(this) == true ? Path.Combine(dlg.FolderName, Path.GetFileName(cur)) : null;
        });
        _folder = box;
        p.Children.Add(row);
        p.Children.Add(DialogUi.Buttons(this, "Generate", () => !string.IsNullOrWhiteSpace(_folder.Text)));
        Content = p;
    }

    public ReportOptions Options => new(_folder.Text.Trim(), _video.IsChecked == true, _stages.IsChecked == true, _surface.IsChecked == true, _open.IsChecked == true);
}
