using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ImpressionFEA.App.ViewModels;

namespace ImpressionFEA.App;

/// <summary>Sets up a multi-run: which parameter to sweep, its values, and where to save the telemetry.</summary>
public sealed class MultiRunDialog : Window
{
    private readonly ComboBox _param;
    private readonly TextBox _values;
    private readonly TextBlock _unit;
    private readonly CheckBox _save;
    private readonly TextBox _folder;
    private readonly MainViewModel _vm;

    public MultiRunDialog(MainViewModel vm, SweepParameter? parameter = null, string? values = null)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;
        Title = "Multi-run (parameter sweep)";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        var muted = new SolidColorBrush(Color.FromRgb(0x56, 0x60, 0x6C));

        var panel = new StackPanel { Margin = new Thickness(18), Width = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = "Runs the current setup once per value. Every run is kept in the run history and its load and fill curves are overlaid on the telemetry chart. The original value is restored afterwards; Abort stops the batch.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(new TextBlock { Text = "Parameter", FontWeight = FontWeights.SemiBold });
        _param = new ComboBox { ItemsSource = MainViewModel.SweepParameters, Margin = new Thickness(0, 2, 0, 8) };
        panel.Children.Add(_param);

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        _unit = new TextBlock { Foreground = muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(_unit, Dock.Right);
        row.Children.Add(_unit);
        row.Children.Add(new TextBlock { Text = "Values (comma separated)", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(row);
        _values = new TextBox { Margin = new Thickness(0, 2, 0, 4), Padding = new Thickness(4, 3, 4, 3) };
        panel.Children.Add(_values);
        panel.Children.Add(new TextBlock { Text = "Material values are names, e.g. \"AISI 4140, 6061-T6\".", Foreground = muted, FontSize = 11, Margin = new Thickness(0, 0, 0, 10) });

        _save = new CheckBox { Content = "Save each run's telemetry (CSV + setup) and a summary.csv to:", Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(_save);
        var folderRow = new DockPanel();
        var browse = new Button { Content = "Browse…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        _folder = new TextBox
        {
            Padding = new Thickness(4, 3, 4, 3),
            Text = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ImpressionFEA", "MultiRun " + DateTime.Now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)),
        };
        folderRow.Children.Add(_folder);
        panel.Children.Add(folderRow);
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for multi-run telemetry" };
            if (dlg.ShowDialog(this) == true)
            {
                _folder.Text = dlg.FolderName;
                _save.IsChecked = true;
            }
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "Start multi-run", IsDefault = true, MinWidth = 120, Padding = new Thickness(10, 4, 10, 4) };
        ok.Click += (_, _) =>
        {
            if (Values.Count == 0)
            {
                MessageBox.Show(this, "Enter at least one value.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 84, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        _param.SelectionChanged += (_, _) => Suggest();
        _param.SelectedItem = parameter ?? SweepParameter.Temperature;
        if (values is not null) _values.Text = values;
    }

    public SweepParameter Parameter => (SweepParameter)_param.SelectedItem;

    public IReadOnlyList<string> Values => _values.Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public string? Folder => _save.IsChecked == true && !string.IsNullOrWhiteSpace(_folder.Text) ? _folder.Text.Trim() : null;

    private void Suggest()
    {
        var (unit, suggestion) = _vm.SweepDefaults(Parameter);
        _unit.Text = unit;
        _values.Text = suggestion;
    }
}
