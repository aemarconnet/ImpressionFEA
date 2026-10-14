using System.Globalization;
using ImpressionFEA.App.Infrastructure;
using ImpressionFEA.Core.Simulation;
using ImpressionFEA.Core.ThreeD;

namespace ImpressionFEA.App.ViewModels;

/// <summary>3D mesh quality levels: accuracy against run time (Advanced tab).</summary>
public enum MeshQuality
{
    Fast,
    Balanced,
    Accurate,
    Custom,
}

/// <summary>
/// Automatic mesh settings. The element size h (3D: about <see cref="TargetNodes"/> nodes in the modelled workpiece;
/// 2D: the preset value) and the stroke increment Δs (0.2·h) follow the geometry until the user types a value, which
/// then stays as an override; clearing the box returns the field to automatic.
/// </summary>
public sealed partial class MainViewModel
{
    private const string TargetNodesKey = "mesh.targetNodes";

    private bool _elementSizeAuto = true;
    private bool _strokeIncrementAuto = true;
    private int _targetNodes = LoadTargetNodes();

    private static int LoadTargetNodes() =>
        int.TryParse(AppSettings.Get(TargetNodesKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n is >= 1000 and <= 60000
            ? n
            : SimulationConfig3D.DefaultTargetNodes;

    /// <summary>Node count the automatic 3D element size aims for (persistent preference).</summary>
    public int TargetNodes
    {
        get => _targetNodes;
        set
        {
            value = Math.Clamp(value, 1000, 60000);
            if (!Set(ref _targetNodes, value)) return;
            AppSettings.Set(TargetNodesKey, value.ToString(CultureInfo.InvariantCulture));
            RefreshDerived();
        }
    }

    /// <summary>h is calculated automatically (grey); false once the user typed a value (bold override).</summary>
    public bool ElementSizeIsAuto
    {
        get => _elementSizeAuto;
        set
        {
            if (!Set(ref _elementSizeAuto, value)) return;
            RefreshDerived();
        }
    }

    /// <summary>Δs is calculated automatically from h (grey); false once the user typed a value (bold override).</summary>
    public bool StrokeIncrementIsAuto
    {
        get => _strokeIncrementAuto;
        set
        {
            if (!Set(ref _strokeIncrementAuto, value)) return;
            RefreshDerived();
        }
    }

    // ---------------------------------------------------------------- mesh quality (3D)

    private const string MeshQualityKey = "mesh.quality";

    /// <summary>A 3D run that gains or loses more volume than this (%) gets a coarse-mesh warning.</summary>
    private const double VolumeWarningPercent = 3.0;

    public IReadOnlyList<MeshQuality> MeshQualities { get; } = Enum.GetValues<MeshQuality>();

    /// <summary>Target nodes and node budget of each quality level (thin-flash refinement off, one hot-spot level).</summary>
    private static (int Target, int Budget) Level(MeshQuality q) => q switch
    {
        MeshQuality.Fast => (4000, SimulationConfig3D.DefaultMaxNodes),
        MeshQuality.Accurate => (20000, 40000),
        _ => (SimulationConfig3D.DefaultTargetNodes, SimulationConfig3D.DefaultMaxNodes),
    };

    /// <summary>The quality level the current settings match, or Custom.</summary>
    public MeshQuality MeshQuality
    {
        get
        {
            if (_refineThinFlash || _refinementLevels != 1) return MeshQuality.Custom;
            foreach (var q in new[] { MeshQuality.Fast, MeshQuality.Balanced, MeshQuality.Accurate })
            {
                if (Level(q) == (_targetNodes, _maxNodeCount)) return q;
            }

            return MeshQuality.Custom;
        }
        set
        {
            if (value == MeshQuality.Custom || value == MeshQuality) return;
            var (target, budget) = Level(value);
            _suspendDerived = true;
            try
            {
                MaxNodeCount = budget;
                TargetNodes = target;
                RefineThinFlash = false;
                RefinementLevels = 1;
            }
            finally
            {
                _suspendDerived = false;
            }

            AppSettings.Set(MeshQualityKey, value.ToString());
            OnPropertyChanged();
            OnPropertyChanged(nameof(MeshQualityHint));
            RefreshDerived();
        }
    }

    public string MeshQualityHint => MeshQuality switch
    {
        MeshQuality.Fast => "Fast: about 4,000 nodes. About a minute for the built-in parts, but thin flash loses volume and the load can come out 30 % low; check the volume change.",
        MeshQuality.Balanced => "Balanced: about 8,000 nodes (default). Volume within about 2 % on the built-in parts in a few minutes.",
        MeshQuality.Accurate => "Accurate: about 20,000 nodes, budget 40,000. For your own parts when run time matters less; tens of minutes or more.",
        _ => "Custom: target nodes, node budget or refinement were set by hand.",
    };

    /// <summary>Restores the saved quality level at start-up (Accurate also needs its larger node budget).</summary>
    private void ApplySavedMeshQuality()
    {
        if (Enum.TryParse<MeshQuality>(AppSettings.Get(MeshQualityKey), out var q) && q != MeshQuality.Custom)
        {
            var (target, budget) = Level(q);
            _maxNodeCount = budget;
            _targetNodes = target;
        }
    }

    public string ElementSizeHint { get; private set; } = "";

    public string StrokeIncrementHint { get; private set; } = "";

    /// <summary>A typed value: the field becomes an override, then the value is set.</summary>
    private void OverrideMesh(ref bool auto, string autoName, Action set)
    {
        if (auto)
        {
            auto = false;
            OnPropertyChanged(autoName);
        }

        set();
    }

    /// <summary>Back to automatic h and Δs (a different die, or a setup saved with automatic values).</summary>
    private void ResetAutoMesh()
    {
        _elementSizeAuto = _strokeIncrementAuto = true;
        OnPropertyChanged(nameof(ElementSizeIsAuto));
        OnPropertyChanged(nameof(StrokeIncrementIsAuto));
    }

    /// <summary>Recomputes the automatic values (called first thing by <see cref="RefreshDerived"/>) and the field hints.</summary>
    private void ApplyAutoMesh()
    {
        if (_elementSizeAuto)
        {
            double h = _analysis3D ? Auto3D() : _selectedDie.RecommendedElementSize;
            if (double.IsFinite(h) && h > 0) SetMeshValue(ref _elementSize, h, nameof(ElementSize));
        }

        if (_strokeIncrementAuto && _elementSize > 0)
        {
            double ds = _analysis3D
                ? SimulationConfig3D.AutoStrokeIncrement(_elementSize)
                : Math.Min(_selectedDie.RecommendedStrokeIncrement, Math.Max(0.01, Math.Round(0.4 * _elementSize, 2)));
            SetMeshValue(ref _strokeIncrement, ds, nameof(StrokeIncrement));
        }

        ElementSizeHint = MeshHint();
        StrokeIncrementHint = _analysis3D
            ? $"Ram travel per increment. Automatic: 0.2·h = {Lh(SimulationConfig3D.AutoStrokeIncrement(_elementSize))}. Must not exceed 0.4·h = {Lh(0.4 * _elementSize)}."
            : $"Ram travel per increment. Automatic: the preset value, at most 0.4·h. Must not exceed h/2 = {Lh(0.5 * _elementSize)}.";
        OnPropertyChanged(nameof(ElementSizeHint));
        OnPropertyChanged(nameof(StrokeIncrementHint));
    }

    private void SetMeshValue(ref double field, double value, string name)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged(name);
        OnPropertyChanged(name + "U");
    }

    private static string Lh(double mm) => U.L(mm).ToString(U.FineLengthFormat, CultureInfo.InvariantCulture) + " " + U.Length;

    private (object Die, object? Billet, double R, double Dim, int Target, int Budget) _autoKey;
    private double _autoH;

    /// <summary>Automatic 3D h, calibrated on the real initial mesh; cached until the workpiece, the target or the budget changes.</summary>
    private double Auto3D()
    {
        var cfg = BuildConfig3D();
        var key = ((object)cfg.Die, (object?)cfg.Die.ImportedBillet, cfg.Die.IsImported ? 0 : cfg.BilletRadius, cfg.Die.IsImported ? 0 : cfg.BilletDimension, _targetNodes, _maxNodeCount);
        if (!key.Equals(_autoKey) || !(_autoH > 0))
        {
            _autoKey = key;
            _autoH = cfg.CalibratedAutoElementSize(_targetNodes);
        }

        return _autoH;
    }

    private string MeshHint()
    {
        if (!_analysis3D)
        {
            double coarse = Math.Min(_billetRadius, _billetHeight) / 3;
            return $"Target edge length of the quadrilateral elements. Automatic: the preset value {Lh(_selectedDie.RecommendedElementSize)}. "
                + $"For this billet: h at most {Lh(coarse)} (3 elements across), at least about {Lh(Math.Sqrt(_billetRadius * _billetHeight / SimulationConfig.MaxElements))} ({SimulationConfig.MaxElements:N0} elements).";
        }

        var cfg = BuildConfig3D();
        double lo = cfg.FinestElementSize, hi = cfg.CoarsestElementSize;
        string why = cfg.Die.IsImported ? "4 elements across the thinnest extent of the workpiece" : "billet radius ≥ 2.5·h";
        string range = lo <= hi
            ? $"For this workpiece: h from {Lh(lo)} (node budget {_maxNodeCount:N0}) to {Lh(hi)} ({why})."
            : $"No valid h for this workpiece: the node budget ({_maxNodeCount:N0}) needs h ≥ {Lh(lo)} but {why} needs h ≤ {Lh(hi)}. Raise the node budget (Advanced) or enlarge the workpiece.";
        return $"Target tetrahedron edge length. Automatic: about {_targetNodes:N0} nodes (Advanced tab) → {Lh(Auto3D())}. "
            + range + $" Now ≈ {cfg.EstimatedNodes:N0} nodes.";
    }
}
