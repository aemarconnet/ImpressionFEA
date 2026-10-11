using ImpressionFEA.App.Infrastructure;
using ImpressionFEA.Core.Simulation;
using OxyPlot;
using OxyPlot.Series;

namespace ImpressionFEA.App.ViewModels;

/// <summary>A finished (or failed) run kept in the session history: its setup, telemetry and chart overlay.</summary>
public sealed class RunRecord : ObservableObject
{
    private bool _isShown = true;

    public RunRecord(int number, string label, RunSettings settings, IReadOnlyList<TelemetryRow> rows, SimulationStatus status, TimeSpan elapsed, OxyColor color)
    {
        Number = number;
        Label = label;
        Settings = settings;
        Rows = rows;
        Status = status;
        Elapsed = elapsed;
        Color = color;
        LoadSeries = new LineSeries
        {
            Title = $"#{number} load",
            YAxisKey = "load",
            Color = color,
            StrokeThickness = 1.6,
            ItemsSource = LoadPoints(rows),
        };
        FillSeries = new LineSeries
        {
            Title = $"#{number} fill",
            YAxisKey = "fill",
            Color = OxyColor.FromAColor(170, color),
            StrokeThickness = 1,
            LineStyle = LineStyle.Dot,
            RenderInLegend = false,
            ItemsSource = FillPoints(rows),
        };
    }

    public int Number { get; }

    public string Label { get; }

    public RunSettings Settings { get; }

    public IReadOnlyList<TelemetryRow> Rows { get; }

    public SimulationStatus Status { get; }

    public TimeSpan Elapsed { get; }

    public OxyColor Color { get; }

    public System.Windows.Media.Brush Swatch => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(Color.R, Color.G, Color.B));

    public LineSeries LoadSeries { get; }

    public LineSeries FillSeries { get; }

    public double PeakLoad => Rows.Count > 0 ? Rows.Max(r => r.LoadTonnes) : 0;

    public double FinalFill => Rows.Count > 0 ? Rows[^1].FillPercent : 0;

    public double FinalStroke => Rows.Count > 0 ? Rows[^1].Stroke : 0;

    public double VolumeChange => Rows.Count > 0 ? Rows[^1].VolumeChangePercent : 0;

    public string StatusText => Status switch
    {
        SimulationStatus.Completed => "done",
        SimulationStatus.Aborted => "aborted",
        _ => "failed",
    };

    public string Summary => Units.Current.Loadf(PeakLoad) + FormattableString.Invariant($" · {FinalFill:F1} % · {StatusText}");

    private static List<DataPoint> LoadPoints(IReadOnlyList<TelemetryRow> rows) =>
        rows.Select(r => new DataPoint(Units.Current.L(r.Stroke), Units.Current.LoadFromTonnes(r.LoadTonnes))).ToList();

    private static List<DataPoint> FillPoints(IReadOnlyList<TelemetryRow> rows) =>
        rows.Select(r => new DataPoint(Units.Current.L(r.Stroke), r.FillPercent)).ToList();

    /// <summary>Re-expresses the overlay curves and summary in the current display units.</summary>
    public void ApplyUnits()
    {
        LoadSeries.ItemsSource = LoadPoints(Rows);
        FillSeries.ItemsSource = FillPoints(Rows);
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Overlay this run's curves on the telemetry chart.</summary>
    public bool IsShown
    {
        get => _isShown;
        set
        {
            if (Set(ref _isShown, value)) ShownChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ShownChanged;
}
