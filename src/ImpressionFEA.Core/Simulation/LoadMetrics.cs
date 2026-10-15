namespace ImpressionFEA.Core.Simulation;

/// <summary>Robust load figures from a load–stroke history.</summary>
public static class LoadMetrics
{
    /// <summary>Gap window (mm before closure) of the closing-load fit.</summary>
    public const double ClosingWindow = 1.0;

    /// <summary>
    /// Closing load: a least-squares straight line through load against die gap over the last
    /// <see cref="ClosingWindow"/> mm, evaluated at closure (gap 0). The single closure increment scatters by about ±3 %
    /// between runs that differ only in solver settings; the fit scatters by about ±1.3 % (hex bolt head studies), so it
    /// is the figure to compare runs and meshes with. Null when the dies did not close or fewer than three increments
    /// fall in the window.
    /// </summary>
    public static double? ClosingLoad(IReadOnlyList<(double Gap, double Load)> history, double window = ClosingWindow)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0 || history[^1].Gap > 1e-6) return null;
        double n = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (gap, load) in history)
        {
            if (gap > window) continue;
            n++;
            sx += gap;
            sy += load;
            sxx += gap * gap;
            sxy += gap * load;
        }

        double det = n * sxx - sx * sx;
        if (n < 3 || Math.Abs(det) < 1e-12) return null;
        double slope = (n * sxy - sx * sy) / det;
        return (sy - slope * sx) / n;
    }

    /// <summary>Closing load (t) of a telemetry history; see <see cref="ClosingLoad(IReadOnlyList{ValueTuple{double, double}}, double)"/>.</summary>
    public static double? ClosingLoad(IReadOnlyList<TelemetryRow> rows) =>
        ClosingLoad(rows.Select(r => (r.DieGap, r.LoadTonnes)).ToList());
}
