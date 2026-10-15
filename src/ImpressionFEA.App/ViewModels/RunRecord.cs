using ImpressionFEA.App.Infrastructure;
using ImpressionFEA.Core.Simulation;
using OxyPlot;
using OxyPlot.Series;

namespace ImpressionFEA.App.ViewModels;

/// <summary>
/// Everything needed to show a finished run again: its increments, dies and view framing; for completed 3D runs also
/// the solver configuration and the state near closure, for a mesh convergence check.
/// </summary>
public sealed record RunResults(
    IReadOnlyList<ForgingSnapshot> Snapshots,
    ImpressionFEA.Core.Geometry.DieSet? Die2D,
    ImpressionFEA.Core.ThreeD.DieSet3D? Die3D,
    System.Windows.Rect ViewBounds,
    System.Windows.Media.Media3D.Rect3D Bounds3D,
    double PartLength,
    string Log = "",
    ImpressionFEA.Core.ThreeD.SimulationConfig3D? Config3D = null,
    ImpressionFEA.Core.ThreeD.Forging3DCheckpoint? Checkpoint = null);

/// <summary>A point on a telemetry-chart curve: the run (null: results not stored as a run), increment, telemetry and, when kept, the increment's results.</summary>
public sealed record CurveHit(
    RunRecord? Run,
    int Index,
    TelemetryRow Row,
    ForgingSnapshot? Snapshot,
    ImpressionFEA.Core.Geometry.DieSet? Die2D,
    System.Windows.Rect ViewBounds,
    string Name,
    OxyColor Color,
    bool OnLoadCurve);

/// <summary>What the inputs looked like when the run finished (the inputs may have changed since): for reports.</summary>
public sealed record RunContext(string Geometry, string Description, string MaterialSummary, string Analysis);

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

    /// <summary>Geometry, material and analysis as they were for this run (null for runs recorded without it).</summary>
    public RunContext? Context { get; init; }

    private RunResults? _results;

    /// <summary>Full results for re-display (kept for the most recent runs only; null when released to save memory).</summary>
    public RunResults? Results
    {
        get => _results;
        set
        {
            if (!Set(ref _results, value)) return;
            OnPropertyChanged(nameof(CanShow));
            OnPropertyChanged(nameof(CanCheckMesh));
            OnPropertyChanged(nameof(Tip));
        }
    }

    public bool CanShow => _results is not null;

    /// <summary>A mesh convergence check is possible: a completed 3D run whose state near closure is still stored.</summary>
    public bool CanCheckMesh => _results is { Checkpoint: not null, Config3D: not null };

    private ImpressionFEA.Core.ThreeD.ConvergenceResult? _meshCheck;

    /// <summary>Result of this run's mesh convergence check (null: not checked).</summary>
    public ImpressionFEA.Core.ThreeD.ConvergenceResult? MeshCheck
    {
        get => _meshCheck;
        set
        {
            if (Set(ref _meshCheck, value)) OnPropertyChanged(nameof(Summary));
        }
    }

    public string Tip => Label + (CanShow ? "  ·  click to show this run's results again" : "  ·  telemetry only (full results released to save memory)");

    /// <summary>Draws this run's curves bold (the run being looked at).</summary>
    public void SetHighlight(bool on)
    {
        LoadSeries.StrokeThickness = on ? 3.2 : 1.6;
        FillSeries.StrokeThickness = on ? 2.0 : 1.0;
    }

    public System.Windows.Media.Brush Swatch => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(Color.R, Color.G, Color.B));

    public LineSeries LoadSeries { get; }

    public LineSeries FillSeries { get; }

    public double PeakLoad => Rows.Count > 0 ? Rows.Max(r => r.LoadTonnes) : 0;

    /// <summary>Closing load, t: straight-line fit of load against die gap over the last mm, at closure (steadier than the peak increment); null if the dies did not close.</summary>
    public double? ClosingLoad => LoadMetrics.ClosingLoad(Rows);

    public double FinalFill => Rows.Count > 0 ? Rows[^1].FillPercent : 0;

    public double FinalStroke => Rows.Count > 0 ? Rows[^1].Stroke : 0;

    public double VolumeChange => Rows.Count > 0 ? Rows[^1].VolumeChangePercent : 0;

    public string StatusText => Status switch
    {
        SimulationStatus.Completed => "done",
        SimulationStatus.Aborted => "aborted",
        _ => "failed",
    };

    public string Summary => Units.Current.Loadf(ClosingLoad ?? PeakLoad) + FormattableString.Invariant($" · {FinalFill:F1} % · {StatusText}")
        + _meshCheck switch
        {
            { Kind: ImpressionFEA.Core.ThreeD.ConvergenceKind.StillChanging, ErrorOfRunPercent: { } e } m => FormattableString.Invariant($" · mesh {(m.Rising ? "≤" : "≥")}{e:+0;-0;0} %"),
            { ErrorOfRunPercent: { } e } => FormattableString.Invariant($" · mesh {e:+0;-0;0} %"),
            { Kind: ImpressionFEA.Core.ThreeD.ConvergenceKind.Uncertain } => " · mesh ?",
            _ => "",
        };

    private static List<DataPoint> LoadPoints(IReadOnlyList<TelemetryRow> rows) =>
        rows.Select(r => new DataPoint(-Units.Current.L(r.DieGap), Units.Current.LoadFromTonnes(r.LoadTonnes))).ToList();

    private static List<DataPoint> FillPoints(IReadOnlyList<TelemetryRow> rows) =>
        rows.Select(r => new DataPoint(-Units.Current.L(r.DieGap), r.FillPercent)).ToList();

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
