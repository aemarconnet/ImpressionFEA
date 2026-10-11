using System.Text;
using ImpressionFEA.App.Controls;
using ImpressionFEA.Core.Simulation;

namespace ImpressionFEA.App.ViewModels;

/// <summary>View settings and the state the exporters (image, video, STL / PLY, report) need.</summary>
public sealed partial class MainViewModel
{
    private ViewLighting _lighting = ViewLighting.Uniform;
    private bool _isExporting;
    private readonly StringBuilder _runLog = new();

    public IReadOnlyList<ViewLighting> LightingOptions { get; } = Enum.GetValues<ViewLighting>();

    /// <summary>3D view lighting; Uniform (ambient only, no shading) by default.</summary>
    public ViewLighting Lighting { get => _lighting; set => Set(ref _lighting, value); }

    private bool _showEdges;

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

    public bool IsPlaneStrainResult => !_analysis3D && _selectedDie.IsPlaneStrain;

    public string GeometryName => CurrentSource is { } src ? System.IO.Path.GetFileName(src.Path) : _analysis3D ? _selectedDie3D.Name : _selectedDie.Name;

    public string DieDescriptionText => DieDescription;

    public string MaterialSummary => MaterialText;

    public ImpressionFEA.Core.ThreeD.Symmetry3D ResultSymmetry => _analysis3D ? _selectedDie3D.Symmetry : ImpressionFEA.Core.ThreeD.Symmetry3D.None;

    public double PartLength => _billetLength;

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
