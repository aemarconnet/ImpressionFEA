using System.Windows.Input;
using ImpressionFEA.App.Infrastructure;
using ImpressionFEA.Core.ThreeD;

namespace ImpressionFEA.App.ViewModels;

/// <summary>
/// Mesh convergence check of a finished 3D run: the end of the stroke (from the state stored at a
/// <see cref="MeshConvergence.CheckpointGap"/> mm die gap) is solved again at the run's element size and two finer
/// ones, and the closing load is extrapolated to a perfectly fine mesh. Runs in the background; Abort stops it.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _isCheckingMesh;
    private CancellationTokenSource? _meshCheckCts;

    /// <summary>A mesh convergence check is running; solving is blocked until it ends.</summary>
    public bool IsCheckingMesh
    {
        get => _isCheckingMesh;
        private set
        {
            if (!Set(ref _isCheckingMesh, value)) return;
            OnPropertyChanged(nameof(CanEditQueue));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public ICommand MeshCheckCommand => new RelayCommand(() => _ = CheckMeshAsync(SelectedRun!),
        () => SelectedRun is { CanCheckMesh: true } && !IsRunning && !IsSweeping && !IsExporting && !IsCheckingMesh);

    /// <summary>Runs the check for <paramref name="run"/> and stores the result on it.</summary>
    public async Task CheckMeshAsync(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Results is not { Config3D: { } config, Checkpoint: { } checkpoint } || IsCheckingMesh || IsRunning) return;
        IsCheckingMesh = true;
        _meshCheckCts = new CancellationTokenSource();
        var ct = _meshCheckCts.Token;
        double h = checkpoint.H;
        _logQueue.Enqueue(F($"=== Mesh check of {run.Label} started {DateTime.Now:yyyy-MM-dd HH:mm:ss}: the last {checkpoint.Gap:F2} mm of the stroke at h {h:F2}, {h * MeshConvergence.Ratio:F2} and {h * MeshConvergence.Ratio * MeshConvergence.Ratio:F2} mm in parallel ==="));
        StatusText = F($"Mesh check of run #{run.Number}…");
        Progress = 0;
        _clock.Restart();
        _eta.Reset();
        try
        {
            var result = await Task.Run(() => MeshConvergence.Run(config, checkpoint, _logQueue.Enqueue,
                f => _dispatcher.InvokeAsync(() =>
                {
                    Progress = 100 * f;
                    _eta.Add(_clock.Elapsed.TotalSeconds, f);
                }), ct), ct);
            run.MeshCheck = result;
            foreach (var l in result.Levels)
            {
                _logQueue.Enqueue("  h " + U.Lf(l.H) + F($": closing load {(l.ClosingLoad is { } c ? U.Loadf(c) : "–")}, peak {U.Loadf(l.PeakLoad)}, fill {l.FillPercent:F1} %, volume {l.VolumeChangePercent:+0.0;-0.0;0.0} %, {l.Nodes:N0} nodes, {l.Elapsed.TotalMinutes:F1} min ({l.Status})"));
            }

            string verdict = result.Describe(U.Loadf, U.Lf);
            _logQueue.Enqueue("MESH CHECK: " + verdict);
            StatusText = F($"Mesh check of run #{run.Number} — ") + verdict;
            Progress = 100;
        }
        catch (OperationCanceledException)
        {
            _logQueue.Enqueue("Mesh check aborted.");
            StatusText = F($"Mesh check of run #{run.Number} aborted.");
        }
        catch (Exception ex) when (ex is ImpressionFEA.Core.Solver.SimulationException or AggregateException or InvalidOperationException or ArgumentException)
        {
            _logQueue.Enqueue("MESH CHECK FAILED: " + (ex.InnerException ?? ex).Message);
            StatusText = "Mesh check failed — " + (ex.InnerException ?? ex).Message;
        }
        finally
        {
            _clock.Stop();
            EtaText = "";
            EtaTip = "Estimated time to completion";
            _meshCheckCts.Dispose();
            _meshCheckCts = null;
            IsCheckingMesh = false;
            Tick();
        }
    }
}
