using System.Windows.Input;
using System.Windows.Threading;
using ImpressionFEA.App.Controls;
using ImpressionFEA.App.Infrastructure;
using ImpressionFEA.Core.Geometry;
using ImpressionFEA.Core.Simulation;
using ImpressionFEA.Core.ThreeD;

namespace ImpressionFEA.App.ViewModels;

/// <summary>View settings (bodies, transparency, cut-away, flow arrows; all live) and animation playback of the increments.</summary>
public sealed partial class MainViewModel
{
    // ---------------------------------------------------------------- bodies and transparency

    private bool _partVisible = true;
    private double _partOpacity = 1;
    private bool _bottomDieVisible = true;
    private bool _topDieVisible = true;
    private double _bottomDieOpacity = 1.0;
    private double _topDieOpacity = 0.15;
    private bool _colorMap = true;
    private bool _quadView;

    public bool PartVisible { get => _partVisible; set => Set(ref _partVisible, value); }

    public double PartOpacity { get => _partOpacity; set => Set(ref _partOpacity, Math.Clamp(value, 0.05, 1)); }

    public bool BottomDieVisible { get => _bottomDieVisible; set => Set(ref _bottomDieVisible, value); }

    public bool TopDieVisible { get => _topDieVisible; set => Set(ref _topDieVisible, value); }

    /// <summary>Bottom die opacity (opaque by default).</summary>
    public double BottomDieOpacity { get => _bottomDieOpacity; set => Set(ref _bottomDieOpacity, Math.Clamp(value, 0.05, 1)); }

    /// <summary>Top die opacity (15 % by default so the workpiece shows through).</summary>
    public double TopDieOpacity { get => _topDieOpacity; set => Set(ref _topDieOpacity, Math.Clamp(value, 0.05, 1)); }

    /// <summary>Colour the workpiece by the selected result field; off = plain deforming solid.</summary>
    public bool ColorMap
    {
        get => _colorMap;
        set
        {
            if (!Set(ref _colorMap, value)) return;
            OnPropertyChanged(nameof(ShowFieldColors));
        }
    }

    /// <summary>Top / Isometric / Front / Right at once (3D).</summary>
    public bool QuadView { get => _quadView; set => Set(ref _quadView, value); }

    // ---------------------------------------------------------------- iso-clipping (drag the legend)

    private double _clipLow;
    private double _clipHigh = 1;

    /// <summary>Visible field range, as fractions of the colour range; elements outside are hidden.</summary>
    public double ClipLow { get => _clipLow; set => Set(ref _clipLow, Math.Clamp(value, 0, 1)); }

    public double ClipHigh { get => _clipHigh; set => Set(ref _clipHigh, Math.Clamp(value, 0, 1)); }

    public void ResetClip()
    {
        ClipLow = 0;
        ClipHigh = 1;
    }

    // ---------------------------------------------------------------- cut-away

    private CutPlaneAxis _cutAxis = CutPlaneAxis.Y;
    private bool _cutFlip;
    private double _cutOffset;

    public IReadOnlyList<CutPlaneAxis> CutAxisOptions { get; } = Enum.GetValues<CutPlaneAxis>();

    public CutPlaneAxis CutAxis
    {
        get => _cutAxis;
        set
        {
            if (Set(ref _cutAxis, value)) OnPropertyChanged(nameof(FieldTitle)); // grain flow end-grain hint
        }
    }

    public bool CutFlip { get => _cutFlip; set => Set(ref _cutFlip, value); }

    /// <summary>Cut plane position, −1 … 1 of the model half-extent (0 = through the press axis).</summary>
    public double CutOffset { get => _cutOffset; set => Set(ref _cutOffset, Math.Clamp(value, -1, 1)); }

    // ---------------------------------------------------------------- flow arrows

    private double _arrowSpacing = 1.6;
    private double _arrowScale = 1.0;

    /// <summary>Arrow spacing in element sizes (0.6 = dense … 4 = sparse).</summary>
    public double ArrowSpacing { get => _arrowSpacing; set => Set(ref _arrowSpacing, Math.Clamp(value, 0.5, 6)); }

    public double ArrowScale { get => _arrowScale; set => Set(ref _arrowScale, Math.Clamp(value, 0.1, 5)); }

    // ---------------------------------------------------------------- grain flow (a "field" of the results list)

    private FiberDirection _flowFiber = FiberDirection.Auto;
    private int _flowLineCount = 16;
    private GrainFlowTracer? _flowTracer;
    private GrainFlow3DTracer? _flowTracer3D;

    public IReadOnlyList<FiberDirection> FiberOptions { get; } = Enum.GetValues<FiberDirection>();

    /// <summary>Grain flow is the selected result: fibre lines instead of contour colours.</summary>
    public bool IsGrainFlow => _selectedField.GrainFlow;

    /// <summary>Fibre direction of the bar stock in the starting billet (Auto: from the billet shape).</summary>
    public FiberDirection FlowFiber
    {
        get => _flowFiber;
        set
        {
            if (!Set(ref _flowFiber, value)) return;
            OnPropertyChanged(nameof(DisplayFlowLines));
            OnPropertyChanged(nameof(ResolvedFiber));
            OnPropertyChanged(nameof(FieldTitle));
        }
    }

    /// <summary>Number of flow lines across the billet.</summary>
    public int FlowLineCount
    {
        get => _flowLineCount;
        set
        {
            if (Set(ref _flowLineCount, Math.Clamp(value, 4, 60))) OnPropertyChanged(nameof(DisplayFlowLines));
        }
    }

    /// <summary>Fibre direction actually used: Auto follows a horizontal 3D bar, otherwise the press axis.</summary>
    public FiberDirection ResolvedFiber => _flowFiber != FiberDirection.Auto ? _flowFiber
        : ActiveDie3D?.Billet == BilletShape.HorizontalBar && _currentSnapshot is Snapshot3D ? FiberDirection.Horizontal : FiberDirection.Vertical;

    /// <summary>Title while grain flow is shown, with a hint when the lines cannot be seen in the current view.</summary>
    private string GrainFlowTitle
    {
        get
        {
            const string title = "Grain flow · fibre lines traced from the billet";
            if (!_is3D || IsPlaneStrainResult) return title;
            if (!_cutAway) return title + "  —  section the part (View ▾ → Cut-away) to see them";
            if (_currentSnapshot is Snapshot3D && ResolvedFiber == FiberDirection.Horizontal && _cutAxis == CutPlaneAxis.X)
                return title + "  —  this section is across the fibre (end grain): cut along Y";
            return title;
        }
    }

    /// <summary>Flow lines of the shown 2D increment (traced on demand and cached for the run), or null.</summary>
    public IReadOnlyList<Vec2[]>? DisplayFlowLines
    {
        get
        {
            if (!IsGrainFlow || _currentSnapshot is not StepSnapshot || _scrubIndex < 0 || _scrubIndex >= _snapshots.Count) return null;
            var fiber = ResolvedFiber;
            if (_flowTracer is null || _flowTracer.Direction != fiber || _flowTracer.LineCount != _flowLineCount)
                _flowTracer = new GrainFlowTracer(fiber, _flowLineCount);
            try
            {
                return _flowTracer.LinesAt(_snapshots, _scrubIndex);
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException)
            {
                _flowTracer = null; // a view overlay must never take the app down
                return null;
            }
        }
    }

    /// <summary>Reference (billet) positions of the shown 3D increment, for flow lines on a section; or null.</summary>
    public GrainReference3D? DisplayFlowField3D
    {
        get
        {
            if (!IsGrainFlow || _currentSnapshot is not Snapshot3D || _scrubIndex < 0 || _scrubIndex >= _snapshots.Count) return null;
            _flowTracer3D ??= new GrainFlow3DTracer();
            try
            {
                return _flowTracer3D.At(_snapshots, _scrubIndex);
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException)
            {
                _flowTracer3D = null;
                return null;
            }
        }
    }

    // ---------------------------------------------------------------- die edges (3D view)

    private bool _showDieEdges;

    /// <summary>Thin lines on the sharp edges of the die geometry (helps when shading is flat).</summary>
    public bool ShowDieEdges { get => _showDieEdges; set => Set(ref _showDieEdges, value); }

    /// <summary>Restores the default view (bodies, transparency, cut-away, arrows, lighting).</summary>
    public ICommand ResetViewCommand => new RelayCommand(() =>
    {
        PartVisible = BottomDieVisible = TopDieVisible = ShowDies = true;
        PartOpacity = 1;
        BottomDieOpacity = 1;
        TopDieOpacity = 0.15;
        CutAway = false;
        ColorMap = true;
        QuadView = false;
        CutAxis = CutPlaneAxis.Y;
        CutFlip = false;
        CutOffset = 0;
        ArrowSpacing = 1.6;
        ArrowScale = 1;
        Lighting = ViewLighting.Shaded;
        ShowEdges = ShowContact = ShowVelocity = ShowDieEdges = false;
        FlowFiber = FiberDirection.Auto;
        FlowLineCount = 16;
    });

    // ---------------------------------------------------------------- playback

    private DispatcherTimer? _playTimer;
    private bool _isPlaying;
    private bool _loop = true;
    private int _playFps = 10;

    public IReadOnlyList<int> PlaybackRates { get; } = [2, 5, 10, 20, 30];

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (!Set(ref _isPlaying, value)) return;
            OnPropertyChanged(nameof(PlayGlyph));
            OnPropertyChanged(nameof(PlayTip));
        }
    }

    /// <summary>Play / pause button glyph (Segoe MDL2: Play E768, Pause E769).</summary>
    public string PlayGlyph => _isPlaying ? "" : "";

    public string PlayTip => _isPlaying ? "Pause" : "Play the ram descent";

    public bool LoopPlayback { get => _loop; set => Set(ref _loop, value); }

    /// <summary>Increments per second during playback.</summary>
    public int PlaybackRate
    {
        get => _playFps;
        set
        {
            if (!Set(ref _playFps, value)) return;
            if (_playTimer is not null) _playTimer.Interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, value));
        }
    }

    private bool CanPlay => CanScrub && !IsExporting;

    public ICommand PlayPauseCommand => new RelayCommand(() =>
    {
        if (IsPlaying) StopPlayback();
        else StartPlayback();
    }, () => CanPlay);

    public ICommand StepBackCommand => new RelayCommand(() => { StopPlayback(); ScrubIndex--; }, () => CanPlay && ScrubIndex > 0);

    public ICommand StepForwardCommand => new RelayCommand(() => { StopPlayback(); ScrubIndex++; }, () => CanPlay && ScrubIndex < ScrubMaximum);

    public ICommand FirstCommand => new RelayCommand(() => { StopPlayback(); ScrubIndex = 0; }, () => CanPlay);

    public ICommand LastCommand => new RelayCommand(() => { StopPlayback(); ScrubIndex = ScrubMaximum; }, () => CanPlay);

    private void StartPlayback()
    {
        if (!CanPlay) return;
        if (ScrubIndex >= ScrubMaximum) ScrubIndex = 0; // replay from the start
        _playTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1.0 / Math.Max(1, _playFps)), DispatcherPriority.Background, (_, _) => PlayTick(), _dispatcher);
        _playTimer.Start();
        IsPlaying = true;
    }

    public void StopPlayback()
    {
        _playTimer?.Stop();
        IsPlaying = false;
    }

    private void PlayTick()
    {
        if (!CanPlay)
        {
            StopPlayback();
            return;
        }

        if (ScrubIndex < ScrubMaximum) ScrubIndex++;
        else if (_loop) ScrubIndex = 0;
        else StopPlayback();
    }
}
