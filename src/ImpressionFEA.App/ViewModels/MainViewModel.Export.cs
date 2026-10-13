using System.Text;
using ImpressionFEA.App.Controls;
using ImpressionFEA.Core.Simulation;

namespace ImpressionFEA.App.ViewModels;

/// <summary>View settings and the state the exporters (image, video, STL / PLY, report) need.</summary>
public sealed partial class MainViewModel
{
    private ViewLighting _lighting = ViewLighting.Shaded;
    private bool _isExporting;
    private readonly StringBuilder _runLog = new();

    public IReadOnlyList<ViewLighting> LightingOptions { get; } = Enum.GetValues<ViewLighting>();

    /// <summary>3D view lighting; Shaded by default (Uniform = ambient only, colours exactly as on the colour bar).</summary>
    public ViewLighting Lighting { get => _lighting; set => Set(ref _lighting, value); }

    private bool _showEdges;
    private bool _showVelocity;

    /// <summary>Show arrows of the material flow velocity (2D section and 3D volume).</summary>
    public bool ShowVelocity { get => _showVelocity; set => Set(ref _showVelocity, value); }

    /// <summary>Cancels the running export (video / report frames), if any. Set by the view.</summary>
    public CancellationTokenSource? ExportCancellation { get; set; }

    /// <summary>Esc: aborts whatever is running (solve, multi-run, export). Returns true if something was running.</summary>
    public bool AbortAll()
    {
        bool any = IsRunning || IsSweeping || IsExporting;
        _sweepAbort = true;
        _cts?.Cancel();
        ExportCancellation?.Cancel();
        if (any) StatusText = "Aborting (Esc)…";
        return any;
    }

    /// <summary>
    /// Starts fresh for an unrelated job: clears every run and its chart overlay, the current results, the telemetry
    /// and the log. Inputs (geometry, material, settings) are kept.
    /// </summary>
    public System.Windows.Input.ICommand ResetCommand => new ImpressionFEA.App.Infrastructure.RelayCommand(() =>
    {
        Runs.Clear();
        SelectedRun = null;
        CurrentRun = null;
        _runCounter = 0;
        SyncOverlays();
        OnPropertyChanged(nameof(HasRuns));
        ResetResults();
        State = RunState.Idle;
        ClearRunLog();
        LogCleared?.Invoke(this, EventArgs.Empty);
        StatusText = "Reset: runs, telemetry and results cleared. Inputs are unchanged.";
        BuildPreview();
    }, () => !IsRunning && !IsSweeping && !IsExporting);

    /// <summary>Show element edges on the 3D part surface (off by default).</summary>
    public bool ShowEdges { get => _showEdges; set => Set(ref _showEdges, value); }

    /// <summary>An export is replaying the increments; inputs and solving are locked.</summary>
    public bool IsExporting
    {
        get => _isExporting;
        set
        {
            if (!Set(ref _isExporting, value)) return;
            OnPropertyChanged(nameof(CanEdit));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Solver log of the current run (for reports).</summary>
    public string RunLog => _runLog.ToString();

    /// <summary>The run whose increments are loaded in the viewport (the latest run).</summary>
    public RunRecord? CurrentRun { get; private set; }

    /// <summary>Increments of the current result set (for frame-by-frame export).</summary>
    public IReadOnlyList<ForgingSnapshot> Snapshots => _snapshots;

    public bool IsPlaneStrainResult => ActiveDie2D?.IsPlaneStrain == true;

    public string GeometryName => CurrentSource is { } src ? System.IO.Path.GetFileName(src.Path) : _analysis3D ? _selectedDie3D.Name : _selectedDie.Name;

    public string DieDescriptionText => DieDescription;

    public string MaterialSummary => MaterialText;

    public ImpressionFEA.Core.ThreeD.Symmetry3D ResultSymmetry => ActiveDie3D?.Symmetry ?? ImpressionFEA.Core.ThreeD.Symmetry3D.None;

    public double PartLength => ShownPartLength;

    /// <summary>
    /// A 2D result drawn in the 3D view (revolved, or extruded for plane strain): element edges, die contact, the section
    /// offset and iso-clipping belong to true 3D meshes and are off here (the 2D section view shows mesh and contact).
    /// </summary>
    public bool IsRevolved2D => _is3D && ActiveDie3D is null;

    /// <summary>Something to export (results in the viewport or stored runs) and nothing running.</summary>
    public bool CanExport => (HasResults || HasRuns) && !IsRunning && !IsSweeping && !IsExporting;

    /// <summary>Analysis type of the current inputs, as worded in reports.</summary>
    public string AnalysisText => _analysis3D
        ? "3D (" + _selectedDie3D.Symmetry + " model)"
        : _selectedDie.IsPlaneStrain ? "2D plane strain" : "2D axisymmetric";

    /// <summary>Press capacity of the current inputs, t.</summary>
    public double PressCapacityTonnes => _pressCapacity;

    /// <summary>The run whose results are in the viewport (null: none, e.g. inputs changed since).</summary>
    public RunRecord? ShownRun => _shown;

    /// <summary>Loads a stored run into the viewport, slider and chart while exporting (selecting a run is locked then).</summary>
    public void ShowRunForExport(RunRecord run)
    {
        if (!run.CanShow) throw new InvalidOperationException($"The full results of {run.Label} were released to save memory.");
        ShowRun(run);
    }

    /// <summary>Puts back the run that was in the viewport before an export (or the setup preview when there was none).</summary>
    public void RestoreShownRun(RunRecord? run)
    {
        if (run is { CanShow: true } && Runs.Contains(run))
        {
            if (run != _shown) ShowRun(run);
            return;
        }

        if (_shown is null) return;
        ResetResults();
        State = RunState.Idle;
        BuildPreview();
    }

    private void AppendRunLog(string text)
    {
        if (_runLog.Length > 8_000_000) _runLog.Remove(0, _runLog.Length / 2);
        _runLog.Append(text);
    }

    private void ClearRunLog() => _runLog.Clear();

    /// <summary>Redraws the telemetry chart now (exports step through increments faster than the UI timer).</summary>
    public void FlushPlot()
    {
        _plotDirty = false;
        LoadPlot.InvalidatePlot(true);
    }

    /// <summary>Report status text while exporting.</summary>
    public void SetStatus(string text) => StatusText = text;
}
