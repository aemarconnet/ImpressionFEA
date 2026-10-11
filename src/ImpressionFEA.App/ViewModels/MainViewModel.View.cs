using System.Windows.Input;
using System.Windows.Threading;
using ImpressionFEA.App.Controls;
using ImpressionFEA.App.Infrastructure;

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

    // ---------------------------------------------------------------- cut-away

    private CutPlaneAxis _cutAxis = CutPlaneAxis.Y;
    private bool _cutFlip;
    private double _cutOffset;

    public IReadOnlyList<CutPlaneAxis> CutAxisOptions { get; } = Enum.GetValues<CutPlaneAxis>();

    public CutPlaneAxis CutAxis { get => _cutAxis; set => Set(ref _cutAxis, value); }

    public bool CutFlip { get => _cutFlip; set => Set(ref _cutFlip, value); }

    /// <summary>Cut plane position, −1 … 1 of the model half-extent (0 = through the press axis).</summary>
    public double CutOffset { get => _cutOffset; set => Set(ref _cutOffset, Math.Clamp(value, -1, 1)); }

    // ---------------------------------------------------------------- flow arrows

    private double _arrowSpacing = 1.6;
    private double _arrowScale = 1.0;

    /// <summary>Arrow spacing in element sizes (0.6 = dense … 4 = sparse).</summary>
    public double ArrowSpacing { get => _arrowSpacing; set => Set(ref _arrowSpacing, Math.Clamp(value, 0.5, 6)); }

    public double ArrowScale { get => _arrowScale; set => Set(ref _arrowScale, Math.Clamp(value, 0.1, 5)); }

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
        ShowEdges = ShowContact = ShowVelocity = false;
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
