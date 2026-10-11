using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using ImpressionFEA.App.Infrastructure;
using ImpressionFEA.Core.Cad;
using ImpressionFEA.Core.Geometry;
using ImpressionFEA.Core.Materials;
using ImpressionFEA.Core.Simulation;
using ImpressionFEA.Core.Solver;
using ImpressionFEA.Core.Solver.Linear;
using ImpressionFEA.Core.ThreeD;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;

namespace ImpressionFEA.App.ViewModels;

public enum RunState
{
    Idle,
    Running,
    Completed,
    Aborted,
    Failed,
}

public sealed record FieldOption(ResultField Field, string Name, string Unit)
{
    public override string ToString() => Name;
}

public sealed record BackendOption(LinearBackend Backend, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Main dashboard view model. The solver runs as a background batch job; its observer callbacks arrive on a worker
/// thread and are marshalled to the UI thread: log lines through a concurrent queue drained by a DispatcherTimer,
/// increment snapshots through <see cref="Dispatcher.InvokeAsync(Action)"/>. Handles both the 2D (axisymmetric /
/// plane strain) and the 3D analysis.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, ISimulationObserver
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Dispatcher _dispatcher;
    private readonly ISimulationEngine _engine = new NativeFemEngine();
    private readonly ConcurrentQueue<string> _logQueue = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly List<ForgingSnapshot> _snapshots = [];
    private readonly double[] _fieldMin = new double[Enum.GetValues<ResultField>().Length];
    private readonly double[] _fieldMax = new double[Enum.GetValues<ResultField>().Length];
    private readonly Dictionary<DieSet3D, double> _cavity3D = [];
    private CancellationTokenSource? _cts;
    private bool _plotDirty;
    private bool _suspendDerived;

    private bool _analysis3D;
    private DieSet _selectedDie;
    private DieSet3D _selectedDie3D;
    private MaterialPreset _selectedMaterial;
    private double _billetRadius;
    private double _billetHeight;
    private double _billetLength;
    private double _temperature;
    private double _friction = 0.3;
    private double _elementSize;
    private double _strokeIncrement;
    private double _ramSpeed = 50;
    private double _pressCapacity = 1000;

    private RunState _state = RunState.Idle;
    private string _statusText = "Ready. Set parameters and press Solve.";
    private string _elapsedText = "00:00.0";
    private double _progress;
    private string _stepText = "—";
    private int _scrubIndex;
    private FieldOption _selectedField;
    private bool _showMesh = true;
    private bool _mirror = true;
    private bool _showContact;
    private bool _is3D;
    private bool _cutAway;
    private bool _showDies = true;
    private ForgingSnapshot? _currentSnapshot;
    private ForgingSnapshot? _preview;
    private Rect _viewBounds = new(0, -10, 50, 60);
    private Rect3D _bounds3D = new(-50, -50, -10, 100, 100, 60);
    private double _totalStroke;
    private int _nominalSteps;

    public MainViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        DieSets = DiePresets.All;
        DieSets3D = new ObservableCollection<DieSet3D>(DiePresets3D.All);
        Materials = MaterialPresets.All;
        Fields =
        [
            new(ResultField.VonMisesStress, "Von Mises stress σ̄", "MPa"),
            new(ResultField.EffectiveStrain, "Effective strain ε̄", "–"),
            new(ResultField.EffectiveStrainRate, "Effective strain rate ε̄̇", "1/s"),
            new(ResultField.MeanStress, "Mean (hydrostatic) stress σm", "MPa"),
            new(ResultField.Damage, "Damage (Cockcroft–Latham)", "–"),
        ];
        _selectedField = Fields[0];
        _selectedMaterial = Materials[0];
        _temperature = _selectedMaterial.DefaultTemperature;
        _selectedDie = DieSets[1];
        _selectedDie3D = DieSets3D[0];
        ApplyDieDefaults();

        SolveCommand = new RelayCommand(() => _ = SolveAsync(), () => !IsRunning && !IsSweeping && !IsExporting);
        AbortCommand = new RelayCommand(() =>
        {
            _sweepAbort = true;
            _cts?.Cancel();
        }, () => IsRunning || IsSweeping);
        LoadPlot = BuildPlot();

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick(), _dispatcher);
        _timer.Start();
        RefreshDerived();
        LoadDemos();
        InitUnits();
        _ = ProbeBackendsAsync();
    }

    /// <summary>Raised on the UI thread with a batch of new log text.</summary>
    public event EventHandler<string>? LogAppended;

    public event EventHandler? LogCleared;

    public IReadOnlyList<DieSet> DieSets { get; }

    /// <summary>3D presets plus die sets imported from STEP during this session.</summary>
    public ObservableCollection<DieSet3D> DieSets3D { get; }

    public IReadOnlyList<MaterialPreset> Materials { get; }

    public IReadOnlyList<FieldOption> Fields { get; }

    public ICommand SolveCommand { get; }

    public ICommand AbortCommand { get; }

    public PlotModel LoadPlot { get; }

    public string EngineName => _engine.Name;

    // ------------------------------------------------------------------ analysis type

    /// <summary>True for the full 3D analysis, false for the 2D (axisymmetric / plane-strain) analysis.</summary>
    public bool Analysis3D
    {
        get => _analysis3D;
        set
        {
            if (!Set(ref _analysis3D, value)) return;
            OnPropertyChanged(nameof(Analysis2D));
            OnWorkflowChanged();
            ClearResults();
            ApplyDieDefaults();
            if (value) Is3D = true;
            RefreshDerived();
        }
    }

    public bool Analysis2D
    {
        get => !_analysis3D;
        set => Analysis3D = !value;
    }

    // ------------------------------------------------------------------ inputs

    public DieSet SelectedDie
    {
        get => _selectedDie;
        set
        {
            if (!Set(ref _selectedDie, value)) return;
            ClearResults();
            ApplyDieDefaults();
            RefreshDerived();
        }
    }

    public DieSet3D SelectedDie3D
    {
        get => _selectedDie3D;
        set
        {
            if (value is null) return; // the ComboBox pushes null while its item list changes
            if (!Set(ref _selectedDie3D, value)) return;
            ClearResults();
            if (_keepSettings) OnDieChangedKeepingSettings();
            else ApplyDieDefaults();
            SourceChanged = false;
            OnWorkflowChanged();
            RefreshDerived();
        }
    }

    public MaterialPreset SelectedMaterial
    {
        get => _selectedMaterial;
        set
        {
            if (!Set(ref _selectedMaterial, value)) return;
            _temperature = value.DefaultTemperature;
            OnPropertyChanged(nameof(Temperature));
            RefreshDerived();
        }
    }

    public double BilletRadius { get => _billetRadius; set => SetInput(ref _billetRadius, value); }

    public double BilletHeight { get => _billetHeight; set => SetInput(ref _billetHeight, value); }

    public double BilletLength { get => _billetLength; set => SetInput(ref _billetLength, value); }

    public double Temperature { get => _temperature; set => SetInput(ref _temperature, value); }

    public double Friction { get => _friction; set => SetInput(ref _friction, value); }

    public double ElementSize { get => _elementSize; set => SetInput(ref _elementSize, value); }

    public double StrokeIncrement { get => _strokeIncrement; set => SetInput(ref _strokeIncrement, value); }

    public double RamSpeed { get => _ramSpeed; set => SetInput(ref _ramSpeed, value); }

    public double PressCapacity
    {
        get => _pressCapacity;
        set
        {
            if (!Set(ref _pressCapacity, value)) return;
            OnPropertyChanged(nameof(PressCapacityU));
            UpdateCapacityLine();
        }
    }

    private void SetInput(ref double field, double value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name)) return;
        OnPropertyChanged(name + "U"); // the display-unit twin of the input
        RefreshDerived();
    }

    private bool IsBar => _analysis3D && _selectedDie3D.Billet == BilletShape.HorizontalBar;

    /// <summary>Re-import with settings kept: only refresh the derived labels for the (same kind of) die.</summary>
    private void OnDieChangedKeepingSettings()
    {
        foreach (var n in new[] { nameof(IsPlaneStrain), nameof(RadiusLabel), nameof(LengthLabel), nameof(SymmetryText), nameof(HeightEnabled), nameof(LengthEnabled), nameof(Show2DDies), nameof(Show3DDies), nameof(ActiveDie2D), nameof(ActiveDie3D), nameof(BilletEditable), nameof(IsImported) })
            OnPropertyChanged(n);
    }

    private void ApplyDieDefaults()
    {
        _suspendDerived = true;
        if (_analysis3D)
        {
            var d = _selectedDie3D;
            var (r, dim) = d.RecommendedBillet();
            BilletRadius = Math.Round(r, 2);
            if (d.Billet == BilletShape.HorizontalBar) BilletLength = Math.Round(dim, 1);
            else BilletHeight = Math.Round(dim, 2);
            ElementSize = d.RecommendedElementSize;
            StrokeIncrement = d.RecommendedStrokeIncrement;
        }
        else
        {
            var d = _selectedDie;
            BilletRadius = d.RecommendedBilletRadius;
            BilletLength = d.RecommendedLength;
            BilletHeight = Math.Round(d.RecommendedHeight(d.RecommendedBilletRadius, d.RecommendedLength), 2);
            ElementSize = d.RecommendedElementSize;
            StrokeIncrement = d.RecommendedStrokeIncrement;
        }

        _suspendDerived = false;
        foreach (var n in new[] { nameof(IsPlaneStrain), nameof(RadiusLabel), nameof(LengthLabel), nameof(SymmetryText), nameof(HeightEnabled), nameof(LengthEnabled), nameof(Show2DDies), nameof(Show3DDies), nameof(ActiveDie2D), nameof(ActiveDie3D), nameof(BilletEditable), nameof(IsImported) })
            OnPropertyChanged(n);
    }

    /// <summary>Die drawn by the viewports: exactly one of these is non-null.</summary>
    public DieSet? ActiveDie2D => _shown?.Results is { } sr ? sr.Die2D : _analysis3D ? null : _selectedDie;

    public DieSet3D? ActiveDie3D => _shown?.Results is { } sr ? sr.Die3D : _analysis3D ? _selectedDie3D : null;

    // ------------------------------------------------------------------ derived info

    public bool IsPlaneStrain => !_analysis3D && _selectedDie.IsPlaneStrain;

    public bool Show2DDies => !_analysis3D;

    public bool Show3DDies => _analysis3D;

    public bool HeightEnabled => !IsBar;

    /// <summary>The 3D workpiece is an imported CAD solid (no billet dimensions to edit).</summary>
    public bool IsImported => _analysis3D && _selectedDie3D.IsImported;

    public bool BilletEditable => !IsImported;

    public bool LengthEnabled => IsPlaneStrain || IsBar;

    public string RadiusLabel => IsPlaneStrain ? "Billet half-width W" : IsBar ? "Bar radius R" : "Billet radius R";

    public string LengthLabel => IsBar ? "Bar length L" : "Billet length L";

    public string SymmetryText => _analysis3D
        ? _selectedDie3D.Symmetry switch
        {
            Symmetry3D.Quarter => "3D · quarter model (x ≥ 0, y ≥ 0) · mirrored for display",
            Symmetry3D.Half => "3D · half model (y ≥ 0) · mirrored for display",
            _ => "3D · full model (imported CAD, no symmetry) · z = press axis, top die moves −z",
        }
        : IsPlaneStrain ? "Plane strain · half-section x ≥ 0 · length L out of plane" : "Axisymmetric · meridian half-section r ≥ 0";

    public string DieDescription => _analysis3D ? _selectedDie3D.Description : _selectedDie.Description;

    public string VolumeText { get; private set; } = "";

    public string MeshText { get; private set; } = "";

    public string PressText { get; private set; } = "";

    public string MaterialText { get; private set; } = "";

    public bool HasInputWarning { get; private set; }

    public string InputWarning { get; private set; } = "";

    private SimulationConfig BuildConfig() => new()
    {
        Die = _selectedDie,
        Material = _selectedMaterial,
        BilletRadius = _billetRadius,
        BilletHeight = _billetHeight,
        BilletLength = _billetLength,
        Temperature = _temperature,
        FrictionFactor = _friction,
        ElementSize = _elementSize,
        StrokeIncrement = _strokeIncrement,
        RamSpeed = _ramSpeed,
    };

    public IReadOnlyList<BackendOption> Backends { get; } =
    [
        new(LinearBackend.Auto, "Auto (benchmark GPU vs CPU)"),
        new(LinearBackend.CudaGpu, "NVIDIA GPU — cuDSS"),
        new(LinearBackend.MklCpu, "CPU multithreaded — MKL PARDISO"),
        new(LinearBackend.Managed, "CPU managed — CSparse (slow)"),
    ];

    private BackendOption? _selectedBackend;

    public BackendOption SelectedBackend
    {
        get => _selectedBackend ?? Backends[0];
        set => Set(ref _selectedBackend, value);
    }

    private string _backendInfo = "Probing GPU / CPU solvers…";

    /// <summary>Availability of the solver backends on this machine (probed in the background at start-up).</summary>
    public string BackendInfo { get => _backendInfo; private set => Set(ref _backendInfo, value); }

    private async Task ProbeBackendsAsync()
    {
        var lines = await Task.Run(SpdSolverFactory.Describe);
        BackendInfo = string.Join(Environment.NewLine, lines);
    }

    private SimulationConfig3D BuildConfig3D() => new()
    {
        LinearBackend = SelectedBackend.Backend,
        Die = _selectedDie3D,
        Material = _selectedMaterial,
        BilletRadius = _billetRadius,
        BilletDimension = _selectedDie3D.Billet == BilletShape.HorizontalBar ? _billetLength : _billetHeight,
        Temperature = _temperature,
        FrictionFactor = _friction,
        ElementSize = _elementSize,
        StrokeIncrement = _strokeIncrement,
        RamSpeed = _ramSpeed,
        AutoStep = _autoStep,
        MaxCutbacks = _maxCutbacks,
        RefinementLevels = _refinementLevels,
        RefineThinFlash = _refineThinFlash,
        RemeshQuality = _remeshQuality,
        MaxNodeCount = _maxNodeCount,
        MaxIterations = _maxIterations,
        Tolerance = _tolerance,
    };

    private double Cavity3D(DieSet3D die)
    {
        if (!_cavity3D.TryGetValue(die, out double v)) _cavity3D[die] = v = die.CavityVolume();
        return v;
    }

    private void RefreshDerived()
    {
        if (_suspendDerived) return;
        List<string> errors;
        double rateSize;
        if (_analysis3D)
        {
            var cfg = BuildConfig3D();
            double vb = _selectedDie3D.BilletVolumeFor(_billetRadius, cfg.BilletDimension);
            double vc = Cavity3D(_selectedDie3D);
            VolumeText = $"Billet {U.Vf(vb)} · cavity {U.Vf(vc)} · ratio {F($"{(vc > 0 ? vb / vc : 0):F3}")}";
            MeshText = F($"≈ {cfg.EstimatedNodes:N0} nodes, {5 * cfg.EstimatedNodes:N0} tetrahedra (3D, modelled portion)");
            errors = cfg.Validate().ToList();
            double gap = errors.Count == 0 ? _selectedDie3D.Place(_billetRadius, cfg.BilletDimension).Gap : 0;
            PressText = _ramSpeed > 0 && _strokeIncrement > 0
                ? $"Stroke {U.Lf(gap)} · Δt {F($"{1000 * cfg.TimeIncrement:F2}")} ms · ~{F($"{Math.Ceiling(gap / _strokeIncrement):F0}")} increments"
                : "—";
            rateSize = _selectedDie3D.Billet == BilletShape.HorizontalBar ? 2 * _billetRadius : _billetHeight;
        }
        else
        {
            var cfg = BuildConfig();
            double vb = _selectedDie.BilletVolume(_billetRadius, _billetHeight, _billetLength);
            double vc = _selectedDie.CavityVolume(_billetLength);
            VolumeText = $"Billet {U.Vf(vb)} · cavity {U.Vf(vc)} · ratio {F($"{(vc > 0 ? vb / vc : 0):F3}")}";
            MeshText = F($"{cfg.ElementsX} × {cfg.ElementsZ} = {cfg.ElementsX * cfg.ElementsZ} Q4 elements initially");
            double gap = _selectedDie.InitialGap(_billetRadius, _billetHeight);
            PressText = _ramSpeed > 0 && _strokeIncrement > 0
                ? $"Stroke {U.Lf(gap)} · Δt {F($"{1000 * cfg.TimeIncrement:F2}")} ms · ~{F($"{Math.Ceiling(gap / _strokeIncrement):F0}")} increments"
                : "—";
            errors = cfg.Validate().ToList();
            rateSize = _billetHeight;
        }

        double rate = rateSize > 0 ? _ramSpeed / rateSize : 0;
        var law = _selectedMaterial.At(_temperature);
        MaterialText = $"C(T) = {U.Sf(law.C)} · n {F($"{law.N:F2}")} · m {F($"{law.M:F2}")} · σ̄(0.2, {F($"{rate:F2}")}/s) = {U.Sf(law.Stress(0.2, rate))}";
        if (!_selectedMaterial.InRange(_temperature))
            errors.Add($"Temperature outside calibrated window {U.Tf(_selectedMaterial.TMin)} – {U.Tf(_selectedMaterial.TMax)} (flow stress extrapolated).");
        HasInputWarning = errors.Count > 0;
        InputWarning = string.Join(Environment.NewLine, errors);
        foreach (var n in new[] { nameof(VolumeText), nameof(MeshText), nameof(PressText), nameof(MaterialText), nameof(HasInputWarning), nameof(InputWarning), nameof(DieDescription) })
            OnPropertyChanged(n);

        if (!IsRunning && _snapshots.Count == 0) BuildPreview();
    }

    /// <summary>Initial billet in the open dies, shown before a run.</summary>
    private void BuildPreview()
    {
        try
        {
            if (_analysis3D)
            {
                var cfg = BuildConfig3D();
                _preview = cfg.Validate().Count == 0 ? new Forging3DSolver(cfg).Initial() : null;
                Bounds3D = ComputeBounds3D(cfg);
            }
            else
            {
                var cfg = BuildConfig();
                _preview = cfg.Validate().Count == 0 ? new ForgingSolver(cfg).InitialSnapshot() : null;
                ViewBounds = ComputeBounds(cfg);
            }
        }
        catch (Exception ex) when (ex is SimulationException or InvalidOperationException)
        {
            _preview = null;
        }

        OnPropertyChanged(nameof(DisplaySnapshot));
        OnPropertyChanged(nameof(ShowFieldColors));
    }

    private static Rect ComputeBounds(SimulationConfig cfg)
    {
        var die = cfg.Die;
        double ext = die.ViewExtent;
        double yMin = die.Bottom.HeightRange(0, ext).Min;
        double yMax = die.SeatHeight(Math.Max(cfg.BilletRadius, 0.1)) + Math.Max(cfg.BilletHeight, 1);
        yMax = Math.Max(yMax, die.TopClosed.HeightRange(0, ext).Max);
        double pad = 0.04 * Math.Max(ext, yMax - yMin);
        return new Rect(0, yMin - pad, ext, yMax - yMin + 2 * pad);
    }

    private static Rect3D ComputeBounds3D(SimulationConfig3D cfg)
    {
        var d = cfg.Die;
        var (seat, gap) = d.Place(cfg.BilletRadius, cfg.BilletDimension);
        if (d.IsImported)
        {
            // Whole die blocks, top die at its starting position.
            double lo = d.Bottom.BoundsMin.Z, hi = d.TopClosed.BoundsMax.Z + Math.Max(gap, 0);
            return new Rect3D(d.XMin, -d.YMax, lo, d.XMax - d.XMin, 2 * d.YMax, hi - lo);
        }

        double zMin = d.Bottom.BoundsMin.Z;
        double zMax = d.TopClosed.BoundsMax.Z;
        zMax = Math.Max(zMax, d.Billet == BilletShape.VerticalCylinder ? seat + cfg.BilletDimension : seat + cfg.BilletRadius);
        return new Rect3D(d.XMin, -d.YMax, zMin, d.XMax - d.XMin, 2 * d.YMax, zMax - zMin);
    }

    // ------------------------------------------------------------------ run state

    public RunState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanScrub));
            OnPropertyChanged(nameof(CanReimport));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsRunning => _state == RunState.Running;

    public bool CanEdit => !IsRunning && !IsExporting;

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }

    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    public string StepText { get => _stepText; private set => Set(ref _stepText, value); }

    // ------------------------------------------------------------------ results / scrubber

    public bool HasResults => _snapshots.Count > 0;

    public bool CanScrub => !IsRunning && _snapshots.Count > 1;

    public int ScrubMaximum => Math.Max(0, _snapshots.Count - 1);

    public int ScrubIndex
    {
        get => _scrubIndex;
        set
        {
            value = Math.Clamp(value, 0, ScrubMaximum);
            if (!Set(ref _scrubIndex, value)) return;
            CurrentSnapshot = _snapshots.Count > 0 ? _snapshots[value] : null;
        }
    }

    public ForgingSnapshot? CurrentSnapshot
    {
        get => _currentSnapshot;
        private set
        {
            if (!Set(ref _currentSnapshot, value)) return;
            OnPropertyChanged(nameof(DisplaySnapshot));
            OnPropertyChanged(nameof(ShowFieldColors));
            OnPropertyChanged(nameof(FieldTitle));
            UpdateReadouts();
            UpdateCursor();
        }
    }

    /// <summary>What the viewport draws: the selected increment, or the pre-run preview.</summary>
    public ForgingSnapshot? DisplaySnapshot => _currentSnapshot ?? _preview;

    public bool ShowFieldColors => _currentSnapshot is not null && _colorMap;

    public Rect ViewBounds { get => _viewBounds; private set => Set(ref _viewBounds, value); }

    public Rect3D Bounds3D { get => _bounds3D; private set => Set(ref _bounds3D, value); }

    public FieldOption SelectedField
    {
        get => _selectedField;
        set
        {
            if (!Set(ref _selectedField, value)) return;
            ResetClip(); // a clip range belongs to one field
            OnPropertyChanged(nameof(RangeMin));
            OnPropertyChanged(nameof(RangeMax));
            OnPropertyChanged(nameof(FieldTitle));
            OnPropertyChanged(nameof(FieldLabelFactor));
            OnPropertyChanged(nameof(FieldLabelDecimals));
        }
    }

    public string FieldTitle => _currentSnapshot is null
        ? "Setup preview · billet seated in the open dies"
        : $"{_selectedField.Name}  [{DisplayUnit(_selectedField.Field)}]";

    public double RangeMin => _snapshots.Count > 0 ? _fieldMin[(int)_selectedField.Field] : 0;

    public double RangeMax
    {
        get
        {
            if (_snapshots.Count == 0) return 1;
            double lo = _fieldMin[(int)_selectedField.Field], hi = _fieldMax[(int)_selectedField.Field];
            return hi > lo ? hi : lo + 1;
        }
    }

    public bool ShowMesh { get => _showMesh; set => Set(ref _showMesh, value); }

    public bool Mirror { get => _mirror; set => Set(ref _mirror, value); }

    public bool ShowContact { get => _showContact; set => Set(ref _showContact, value); }

    /// <summary>Viewport mode: 3D solid instead of the 2D section (always on for the 3D analysis).</summary>
    public bool Is3D
    {
        get => _is3D;
        set
        {
            if (_analysis3D) value = true;
            if (!Set(ref _is3D, value)) return;
            OnPropertyChanged(nameof(Is2D));
        }
    }

    public bool Is2D
    {
        get => !_is3D;
        set => Is3D = !value;
    }

    public bool CutAway { get => _cutAway; set => Set(ref _cutAway, value); }

    public bool ShowDies { get => _showDies; set => Set(ref _showDies, value); }

    public string ScrubText { get; private set; } = "No results";

    public string ReadStroke { get; private set; } = "—";

    public string ReadGap { get; private set; } = "—";

    public string ReadLoad { get; private set; } = "—";

    public string ReadPeak { get; private set; } = "—";

    public string ReadFill { get; private set; } = "—";

    public string ReadVonMises { get; private set; } = "—";

    public string ReadStrain { get; private set; } = "—";

    public string ReadVolume { get; private set; } = "—";

    public string ReadMesh { get; private set; } = "—";

    private void UpdateReadouts()
    {
        var s = _currentSnapshot;
        if (s is null)
        {
            ScrubText = "No results";
            ReadStroke = ReadGap = ReadLoad = ReadPeak = ReadFill = ReadVonMises = ReadStrain = ReadVolume = ReadMesh = "—";
        }
        else
        {
            ScrubText = $"Step {s.Step} / {Math.Max(ScrubMaximum, _snapshots[^1].Step)}   ·   stroke {U.L(s.Stroke).ToString(U.LengthFormat, Inv)} / {U.Lf(_totalStroke)}   ·   t = {F($"{s.Time:F3}")} s";
            ReadStroke = U.Lf(s.Stroke);
            ReadGap = U.Lf(s.DieGap);
            ReadLoad = U.System == UnitSystem.US ? $"{U.LoadfN(s.LoadNewton)}  ({F($"{s.LoadNewton / 4448.2216:F0}")} kip)" : $"{U.LoadfN(s.LoadNewton)}  ({F($"{s.LoadNewton / 1000:F0}")} kN)";
            double peak = 0;
            for (int i = 0; i <= _scrubIndex && i < _snapshots.Count; i++) peak = Math.Max(peak, _snapshots[i].LoadTonnes);
            ReadPeak = U.Loadf(peak);
            ReadFill = F($"{s.FillPercent:F2} %");
            ReadVonMises = U.Sf(s.VonMises.Length > 0 ? s.VonMises.Max() : 0);
            ReadStrain = F($"{(s.EffectiveStrain.Length > 0 ? s.EffectiveStrain.Max() : 0):F3}");
            ReadVolume = F($"{s.VolumeChangePercent:+0.000;-0.000;0.000} %");
            ReadMesh = s is Snapshot3D ? F($"{s.ElementCount:N0} tetrahedra · {s.NodeCount:N0} nodes") : F($"{s.ElementCount:N0} quadrilaterals · {s.NodeCount:N0} nodes");
        }

        foreach (var n in new[] { nameof(ScrubText), nameof(ReadStroke), nameof(ReadGap), nameof(ReadLoad), nameof(ReadPeak), nameof(ReadFill), nameof(ReadVonMises), nameof(ReadStrain), nameof(ReadVolume), nameof(ReadMesh) })
            OnPropertyChanged(n);
    }

    // ------------------------------------------------------------------ solve

    private async Task SolveAsync()
    {
        var settings = CaptureSettings();
        IReadOnlyList<string> errors = _analysis3D ? BuildConfig3D().Validate() : BuildConfig().Validate();
        if (errors.Count > 0)
        {
            foreach (var e in errors) _logQueue.Enqueue("INPUT ERROR: " + e);
            State = RunState.Failed;
            StatusText = "Input error — " + errors[0];
            return;
        }

        var probe = _engine.Probe();
        if (!probe.Available)
        {
            _logQueue.Enqueue("ENGINE UNAVAILABLE: " + probe.Detail);
            State = RunState.Failed;
            StatusText = "Solver engine unavailable — " + probe.Detail;
            return;
        }

        ResetResults();
        LogCleared?.Invoke(this, EventArgs.Empty);
        ClearRunLog();
        if (_analysis3D) Bounds3D = ComputeBounds3D(BuildConfig3D());
        else ViewBounds = ComputeBounds(BuildConfig());
        _logQueue.Enqueue(F($"=== {(_analysis3D ? "3D" : "2D")} run started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==="));
        StopPlayback();
        State = RunState.Running;
        StatusText = _analysis3D ? "Solving (3D)…" : "Solving…";
        _cts = new CancellationTokenSource();
        _clock.Restart();

        SimulationResult result;
        try
        {
            result = _analysis3D
                ? await _engine.RunAsync(BuildConfig3D(), this, _cts.Token)
                : await _engine.RunAsync(BuildConfig(), this, _cts.Token);
        }
        finally
        {
            _clock.Stop();
            _cts.Dispose();
            _cts = null;
        }

        // Authoritative result set (the live increments were marshalled one by one while running).
        _snapshots.Clear();
        _snapshots.AddRange(result.Snapshots);
        RecomputeRanges();
        RebuildSeries();
        State = result.Status switch
        {
            SimulationStatus.Completed => RunState.Completed,
            SimulationStatus.Aborted => RunState.Aborted,
            _ => RunState.Failed,
        };
        StatusText = result.Status switch
        {
            SimulationStatus.Completed => "Completed — " + result.Message,
            SimulationStatus.Aborted => "Aborted — " + result.Message,
            _ => "Failed — " + result.Message + (result.Snapshots.Count > 1 ? " Partial results are available." : ""),
        };
        Progress = result.Status == SimulationStatus.Completed ? 100 : Progress;
        OnPropertyChanged(nameof(ScrubMaximum));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(CanScrub));
        _scrubIndex = -1;
        ScrubIndex = ScrubMaximum;
        AddRunRecord(result, settings, _clock.Elapsed);
        Tick();
    }

    private void ResetResults()
    {
        if (_shown is not null)
        {
            _shown = null;
            OnPropertyChanged(nameof(ActiveDie2D));
            OnPropertyChanged(nameof(ActiveDie3D));
            OnPropertyChanged(nameof(ShownPartLength));
        }

        _snapshots.Clear();
        _preview = null;
        Array.Fill(_fieldMin, double.MaxValue);
        Array.Fill(_fieldMax, double.MinValue);
        _scrubIndex = 0;
        CurrentSnapshot = null;
        Progress = 0;
        StepText = "—";
        RebuildSeries();
        OnPropertyChanged(nameof(ScrubIndex));
        OnPropertyChanged(nameof(ScrubMaximum));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(CanScrub));
    }

    /// <summary>Discards results when the setup changes to a different analysis or die (they no longer match the inputs).</summary>
    private void ClearResults()
    {
        if (IsRunning || _snapshots.Count == 0) return;
        ResetResults();
        State = RunState.Idle;
        StatusText = "Ready. Set parameters and press Solve.";
    }

    // ISimulationObserver — called on the solver thread.
    void ISimulationObserver.OnLog(string line) => _logQueue.Enqueue(line);

    void ISimulationObserver.OnStarted(double totalStroke, int nominalSteps) =>
        _dispatcher.InvokeAsync(() =>
        {
            _totalStroke = totalStroke;
            _nominalSteps = nominalSteps;
        });

    void ISimulationObserver.OnStep(ForgingSnapshot snapshot) => _dispatcher.InvokeAsync(() => AppendLive(snapshot));

    private void AppendLive(ForgingSnapshot s)
    {
        if (!IsRunning) return;
        _snapshots.Add(s);
        Accumulate(s);
        AddPoint(s);
        _plotDirty = true;
        Progress = _totalStroke > 0 ? 100 * s.Stroke / _totalStroke : 0;
        StepText = F($"step {s.Step} / ~{_nominalSteps}");
        OnPropertyChanged(nameof(ScrubMaximum));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(RangeMin));
        OnPropertyChanged(nameof(RangeMax));
        _scrubIndex = _snapshots.Count - 1;
        OnPropertyChanged(nameof(ScrubIndex));
        CurrentSnapshot = s;
    }

    private void Accumulate(ForgingSnapshot s)
    {
        foreach (ResultField f in Enum.GetValues<ResultField>())
        {
            var v = s.Field(f);
            if (s.Step == 0 && f != ResultField.EffectiveStrain) continue;
            for (int i = 0; i < v.Length; i++)
            {
                if (v[i] < _fieldMin[(int)f]) _fieldMin[(int)f] = v[i];
                if (v[i] > _fieldMax[(int)f]) _fieldMax[(int)f] = v[i];
            }
        }
    }

    private void RecomputeRanges()
    {
        Array.Fill(_fieldMin, double.MaxValue);
        Array.Fill(_fieldMax, double.MinValue);
        foreach (var s in _snapshots) Accumulate(s);
        for (int f = 0; f < _fieldMin.Length; f++)
        {
            if (_fieldMin[f] > _fieldMax[f]) (_fieldMin[f], _fieldMax[f]) = (0, 1);
        }

        OnPropertyChanged(nameof(RangeMin));
        OnPropertyChanged(nameof(RangeMax));
    }

    private void Tick()
    {
        if (!_logQueue.IsEmpty)
        {
            var sb = new StringBuilder();
            int count = 0;
            while (count++ < 4000 && _logQueue.TryDequeue(out var line)) sb.AppendLine(line);
            string text = sb.ToString();
            AppendRunLog(text);
            LogAppended?.Invoke(this, text);
        }

        CheckSourceFile();
        var t = _clock.Elapsed;
        ElapsedText = F($"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}");
        if (_plotDirty)
        {
            _plotDirty = false;
            LoadPlot.InvalidatePlot(true);
        }
    }

    // ------------------------------------------------------------------ telemetry chart

    private LineSeries _loadSeries = null!;
    private LineSeries _fillSeries = null!;
    private LineAnnotation _capacityLine = null!;
    private LineAnnotation _cursor = null!;

    private PlotModel BuildPlot()
    {
        var ink = OxyColor.FromRgb(0x10, 0x14, 0x18);
        var grid = OxyColor.FromAColor(36, OxyColors.Black);
        var model = new PlotModel
        {
            DefaultFont = "Segoe UI",
            DefaultFontSize = 11,
            TextColor = ink,
            PlotAreaBorderColor = OxyColor.FromRgb(0xAE, 0xB5, 0xBF),
            Padding = new OxyThickness(4, 8, 4, 4),
        };
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom, Key = "stroke", Title = "Ram stroke (" + U.Length + ")", Minimum = 0, MajorGridlineStyle = LineStyle.Solid, MajorGridlineColor = grid,
        });
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left, Key = "load", Title = "Press load (" + U.Load + ")", Minimum = 0, MajorGridlineStyle = LineStyle.Solid, MajorGridlineColor = grid,
            MaximumPadding = 0.08,
        });
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Right, Key = "fill", Title = "Cavity fill (%)", Minimum = 0, Maximum = 105, MajorStep = 20,
        });
        _loadSeries = new LineSeries { Title = "Load", YAxisKey = "load", Color = OxyColor.FromRgb(0x0B, 0x5C, 0xAD), StrokeThickness = 2 };
        _fillSeries = new LineSeries { Title = "Fill", YAxisKey = "fill", Color = OxyColor.FromRgb(0x1E, 0x7B, 0x34), StrokeThickness = 1.5, LineStyle = LineStyle.Dash };
        model.Series.Add(_loadSeries);
        model.Series.Add(_fillSeries);
        _capacityLine = new LineAnnotation
        {
            Type = LineAnnotationType.Horizontal, YAxisKey = "load", Y = _pressCapacity, Color = OxyColor.FromRgb(0xB4, 0x23, 0x18),
            LineStyle = LineStyle.Dash, Text = "Press capacity", TextColor = OxyColor.FromRgb(0xB4, 0x23, 0x18),
        };
        _cursor = new LineAnnotation { Type = LineAnnotationType.Vertical, X = 0, Color = OxyColor.FromAColor(150, ink), LineStyle = LineStyle.Solid, StrokeThickness = 1 };
        model.Annotations.Add(_capacityLine);
        model.Annotations.Add(_cursor);
        model.Legends.Add(new Legend { LegendPosition = LegendPosition.TopLeft, LegendPlacement = LegendPlacement.Inside, LegendBackground = OxyColor.FromAColor(220, OxyColors.White) });
        return model;
    }

    private void AddPoint(ForgingSnapshot s)
    {
        _loadSeries.Points.Add(new DataPoint(U.L(s.Stroke), U.LoadFromNewton(s.LoadNewton)));
        _fillSeries.Points.Add(new DataPoint(U.L(s.Stroke), s.FillPercent));
    }

    private void RebuildSeries()
    {
        _loadSeries.Points.Clear();
        _fillSeries.Points.Clear();
        foreach (var s in _snapshots) AddPoint(s);
        UpdateCapacityLine();
    }

    private void UpdateCapacityLine()
    {
        _capacityLine.Y = U.LoadFromTonnes(_pressCapacity);
        double peak = _snapshots.Count > 0 ? _snapshots.Max(s => s.LoadTonnes) : 0;
        if (peak > _pressCapacity)
            _logQueue.Enqueue($"WARNING: peak load {U.Loadf(peak)} exceeds press capacity {U.Loadf(_pressCapacity)}.");
        LoadPlot.InvalidatePlot(true);
    }

    private void UpdateCursor()
    {
        _cursor.X = U.L(_currentSnapshot?.Stroke ?? 0);
        _plotDirty = true;
    }

    private static string F(FormattableString s) => s.ToString(Inv);
}
