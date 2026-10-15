using ImpressionFEA.Core.Simulation;

namespace ImpressionFEA.Core.Tests;

public class LoadMetricsTests
{
    [Fact]
    public void ClosingLoadFitsTheLastMillimetreAndIgnoresAnOutlyingFinalIncrement()
    {
        // Load rising linearly to 100 t at closure over the last mm (40 t / mm), final increment 10 % high.
        var h = new List<(double, double)>();
        for (double g = 3; g > 0.05; g -= 0.1) h.Add((g, g > 1 ? 30 : 100 - 40 * g));
        h.Add((0, 110));
        double fit = LoadMetrics.ClosingLoad(h)!.Value;
        Assert.InRange(fit, 100, 105); // the single 110 t point moves the fit by at most half its own error
    }

    [Fact]
    public void ClosingLoadNeedsDieClosureAndEnoughPoints()
    {
        Assert.Null(LoadMetrics.ClosingLoad([(2.0, 10.0), (1.0, 20.0), (0.5, 30.0)])); // dies not closed
        Assert.Null(LoadMetrics.ClosingLoad([(0.5, 30.0), (0.0, 40.0)])); // two points
        Assert.Equal(40, LoadMetrics.ClosingLoad([(1.0, 20.0), (0.5, 30.0), (0.0, 40.0)])!.Value, 9);
    }
}
