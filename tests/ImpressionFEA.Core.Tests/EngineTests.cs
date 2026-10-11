using ImpressionFEA.Core.Geometry;
using ImpressionFEA.Core.Materials;
using ImpressionFEA.Core.Simulation;

namespace ImpressionFEA.Core.Tests;

public class EngineTests
{
    private sealed class Recorder(Action<ForgingSnapshot>? onStep = null) : ISimulationObserver
    {
        public List<string> Lines { get; } = [];

        public int Steps { get; private set; }

        public void OnLog(string line)
        {
            lock (Lines) Lines.Add(line);
        }

        public void OnStarted(double totalStroke, int nominalSteps) { }

        public void OnStep(ForgingSnapshot snapshot)
        {
            Steps++;
            onStep?.Invoke(snapshot);
        }
    }

    private static SimulationConfig Upset() => new()
    {
        Die = DiePresets.SimpleUpsetBlock(),
        Material = MaterialPresets.Al6061,
        Temperature = 450,
        BilletRadius = 20,
        BilletHeight = 51.2,
        FrictionFactor = 0.3,
        ElementSize = 2,
        StrokeIncrement = 0.5,
        RamSpeed = 50,
    };

    [Fact]
    public async Task InvalidInputIsReportedNotThrown()
    {
        var rec = new Recorder();
        var result = await new NativeFemEngine().RunAsync(Upset() with { BilletRadius = -1 }, rec, CancellationToken.None);
        Assert.Equal(SimulationStatus.Failed, result.Status);
        Assert.Contains("radius", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Snapshots);
        Assert.Contains(rec.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AbortKeepsPartialResults()
    {
        using var cts = new CancellationTokenSource();
        var rec = new Recorder(s =>
        {
            if (s.Step == 5) cts.Cancel();
        });
        var result = await new NativeFemEngine().RunAsync(Upset(), rec, cts.Token);
        Assert.Equal(SimulationStatus.Aborted, result.Status);
        Assert.InRange(result.Snapshots.Count, 6, 7);
        Assert.True(result.Snapshots[^1].Stroke > 0);
    }

    [Fact]
    public async Task CompletedRunStreamsSolverOutput()
    {
        var rec = new Recorder();
        var result = await new NativeFemEngine().RunAsync(Upset(), rec, CancellationToken.None);
        Assert.Equal(SimulationStatus.Completed, result.Status);
        Assert.Equal(result.Snapshots.Count, rec.Steps);
        Assert.Contains(rec.Lines, l => l.Contains("|R|", StringComparison.Ordinal) && l.Contains("chol", StringComparison.Ordinal));
        Assert.Equal(0, result.Snapshots[^1].DieGap, 9);
    }
}
