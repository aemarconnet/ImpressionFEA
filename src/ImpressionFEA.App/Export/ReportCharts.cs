using ImpressionFEA.App.Infrastructure;
using ImpressionFEA.Core.Simulation;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;

namespace ImpressionFEA.App.Export;

/// <summary>One curve set for a report chart: a run's telemetry and its colour.</summary>
public sealed record ChartRun(string Name, IReadOnlyList<TelemetryRow> Rows, OxyColor Color);

/// <summary>
/// Report charts drawn off screen (independent of the live chart and its overlays), in the current display units.
/// The x axis is the die position from closure, as on the main chart: 0 = dies closed, on the right.
/// </summary>
public static class ReportCharts
{
    private static readonly OxyColor Ink = OxyColor.FromRgb(0x10, 0x14, 0x18);
    private static readonly OxyColor Grid = OxyColor.FromAColor(36, OxyColors.Black);
    private static readonly OxyColor Capacity = OxyColor.FromRgb(0xB4, 0x23, 0x18);

    private static UnitSet U => Units.Current;

    private static PlotModel Model(string? title) => new()
    {
        Title = title,
        TitleFontSize = 13,
        TitleFontWeight = 600,
        DefaultFont = "Segoe UI",
        DefaultFontSize = 11,
        TextColor = Ink,
        PlotAreaBorderColor = OxyColor.FromRgb(0xAE, 0xB5, 0xBF),
        Padding = new OxyThickness(6, 8, 10, 6),
        Background = OxyColors.White,
    };

    private static LinearAxis PositionAxis() => new()
    {
        Position = AxisPosition.Bottom, Title = "Die position from closure (" + U.Length + ")", Maximum = 0,
        MajorGridlineStyle = LineStyle.Solid, MajorGridlineColor = Grid,
    };

    private static LinearAxis LoadAxis(string? key = null) => new()
    {
        Position = AxisPosition.Left, Key = key, Title = "Press load (" + U.Load + ")", Minimum = 0, MaximumPadding = 0.08,
        MajorGridlineStyle = LineStyle.Solid, MajorGridlineColor = Grid,
    };

    private static LinearAxis FillAxis(AxisPosition position, string? key = null, bool grid = false) => new()
    {
        Position = position, Key = key, Title = "Cavity fill (%)", Minimum = 0, Maximum = 105, MajorStep = 20,
        MajorGridlineStyle = grid ? LineStyle.Solid : LineStyle.None, MajorGridlineColor = Grid,
    };

    private static Legend Legend(LegendPosition position = LegendPosition.TopLeft) => new()
    {
        LegendPosition = position, LegendPlacement = LegendPlacement.Inside, LegendBackground = OxyColor.FromAColor(225, OxyColors.White),
        LegendBorder = OxyColor.FromAColor(60, OxyColors.Black), LegendFontSize = 10.5,
    };

    private static IEnumerable<DataPoint> Load(IReadOnlyList<TelemetryRow> rows) =>
        rows.Select(r => new DataPoint(-U.L(r.DieGap), U.LoadFromTonnes(r.LoadTonnes)));

    private static IEnumerable<DataPoint> Fill(IReadOnlyList<TelemetryRow> rows) =>
        rows.Select(r => new DataPoint(-U.L(r.DieGap), r.FillPercent));

    private static void AddCapacity(PlotModel m, double capacityTonnes, string? yKey = null)
    {
        if (capacityTonnes <= 0) return;
        m.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Horizontal, YAxisKey = yKey, Y = U.LoadFromTonnes(capacityTonnes), Color = Capacity,
            LineStyle = LineStyle.Dash, Text = "Press capacity", TextColor = Capacity,
        });
    }

    /// <summary>One run: load (solid, left axis) and cavity fill (dashed, right axis), as on the main chart.</summary>
    public static PlotModel Run(ChartRun run, double capacityTonnes)
    {
        var m = Model(null);
        m.Axes.Add(PositionAxis());
        m.Axes.Add(LoadAxis("load"));
        m.Axes.Add(FillAxis(AxisPosition.Right, "fill"));
        var line = new LineSeries { Title = "Load", YAxisKey = "load", Color = run.Color, StrokeThickness = 2 };
        line.Points.AddRange(Load(run.Rows));
        var fill = new LineSeries { Title = "Fill", YAxisKey = "fill", Color = OxyColor.FromAColor(200, run.Color), StrokeThickness = 1.5, LineStyle = LineStyle.Dash };
        fill.Points.AddRange(Fill(run.Rows));
        m.Series.Add(line);
        m.Series.Add(fill);
        AddCapacity(m, capacityTonnes, "load");
        m.Legends.Add(Legend());
        return m;
    }

    /// <summary>Press load of every run against the die position.</summary>
    public static PlotModel LoadOverlay(IReadOnlyList<ChartRun> runs, double capacityTonnes)
    {
        var m = Model("Press load");
        m.Axes.Add(PositionAxis());
        m.Axes.Add(LoadAxis());
        foreach (var r in runs)
        {
            var s = new LineSeries { Title = r.Name, Color = r.Color, StrokeThickness = 1.8 };
            s.Points.AddRange(Load(r.Rows));
            m.Series.Add(s);
        }

        AddCapacity(m, capacityTonnes);
        m.Legends.Add(Legend());
        return m;
    }

    /// <summary>Cavity fill of every run against the die position.</summary>
    public static PlotModel FillOverlay(IReadOnlyList<ChartRun> runs)
    {
        var m = Model("Cavity fill");
        m.Axes.Add(PositionAxis());
        m.Axes.Add(FillAxis(AxisPosition.Left, grid: true));
        foreach (var r in runs)
        {
            var s = new LineSeries { Title = r.Name, Color = r.Color, StrokeThickness = 1.8 };
            s.Points.AddRange(Fill(r.Rows));
            m.Series.Add(s);
        }

        m.Legends.Add(Legend());
        return m;
    }

    /// <summary>Peak load and final fill against the one parameter that differs between the runs.</summary>
    public static PlotModel Sweep(string parameter, IReadOnlyList<(double X, double PeakTonnes, double Fill)> points, double capacityTonnes)
    {
        var m = Model("Peak load and final fill against " + parameter.ToLowerInvariant());
        m.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = parameter, MajorGridlineStyle = LineStyle.Solid, MajorGridlineColor = Grid, MinimumPadding = 0.06, MaximumPadding = 0.06 });
        m.Axes.Add(LoadAxis("load"));
        m.Axes.Add(FillAxis(AxisPosition.Right, "fill"));
        var blue = OxyColor.FromRgb(0x0B, 0x5C, 0xAD);
        var green = OxyColor.FromRgb(0x1E, 0x7B, 0x34);
        var load = new LineSeries { Title = "Peak load", YAxisKey = "load", Color = blue, StrokeThickness = 2, MarkerType = MarkerType.Circle, MarkerSize = 4, MarkerFill = blue };
        var fill = new LineSeries { Title = "Final fill", YAxisKey = "fill", Color = green, StrokeThickness = 1.5, LineStyle = LineStyle.Dash, MarkerType = MarkerType.Square, MarkerSize = 3.5, MarkerFill = green };
        foreach (var p in points.OrderBy(p => p.X))
        {
            load.Points.Add(new DataPoint(p.X, U.LoadFromTonnes(p.PeakTonnes)));
            fill.Points.Add(new DataPoint(p.X, p.Fill));
        }

        m.Series.Add(load);
        m.Series.Add(fill);
        AddCapacity(m, capacityTonnes, "load");
        m.Legends.Add(Legend(LegendPosition.BottomRight));
        return m;
    }

    /// <summary>Renders a chart (layout size in px at 96 dpi) at <paramref name="scale"/> times the screen resolution.</summary>
    public static System.Windows.Media.Imaging.BitmapSource Render(PlotModel model, double scale, int width = 900, int height = 400) =>
        new OxyPlot.Wpf.PngExporter { Width = width, Height = height, Resolution = 96 * scale }.ExportToBitmap(model);
}
