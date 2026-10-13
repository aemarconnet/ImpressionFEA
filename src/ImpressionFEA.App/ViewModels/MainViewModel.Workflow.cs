using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Input;
using ImpressionFEA.App.Infrastructure;
using ImpressionFEA.Core.Cad;
using ImpressionFEA.Core.Simulation;
using ImpressionFEA.Core.Solver.Linear;
using ImpressionFEA.Core.ThreeD;
using OxyPlot;

namespace ImpressionFEA.App.ViewModels;

/// <summary>Parameters that a multi-run can sweep.</summary>
public enum SweepParameter
{
    Temperature,
    Friction,
    RamSpeed,
    ElementSize,
    StrokeIncrement,
    Material,

    /// <summary>Die-only imports: plan position x of the user workpiece.</summary>
    WorkpieceOffsetX,
}

/// <summary>Workflow features: setups, STEP import / re-import, advanced settings, run history, multi-run and demos.</summary>
public sealed partial class MainViewModel
{
    // ================================================================== advanced (3D robustness) settings

    private bool _autoStep = true;
    private int _maxCutbacks = 5;
    private int _refinementLevels = 1;
    private bool _refineThinFlash;
    private double _remeshQuality = 0.12;
    private int _maxNodeCount = SimulationConfig3D.DefaultMaxNodes;
    private int _maxIterations = 70;
    private double _tolerance = 1e-4;

    public bool AutoStep { get => _autoStep; set => SetAdvanced(ref _autoStep, value); }

    public int MaxCutbacks { get => _maxCutbacks; set => SetAdvanced(ref _maxCutbacks, value); }

    public int RefinementLevels { get => _refinementLevels; set => SetAdvanced(ref _refinementLevels, value); }

    public bool RefineThinFlash { get => _refineThinFlash; set => SetAdvanced(ref _refineThinFlash, value); }

    public double RemeshQualityThreshold { get => _remeshQuality; set => SetAdvanced(ref _remeshQuality, value); }

    public int MaxNodeCount { get => _maxNodeCount; set => SetAdvanced(ref _maxNodeCount, value); }

    public int MaxIterations { get => _maxIterations; set => SetAdvanced(ref _maxIterations, value); }

    public double Tolerance { get => _tolerance; set => SetAdvanced(ref _tolerance, value); }

    public IReadOnlyList<int> CutbackChoices { get; } = [0, 1, 2, 3, 4, 5, 6, 7, 8];

    public IReadOnlyList<int> RefinementChoices { get; } = [0, 1, 2, 3];

    private void SetAdvanced<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Set(ref field, value, name)) RefreshDerived();
    }

    /// <summary>Restores the recommended advanced settings.</summary>
    public ICommand ResetAdvancedCommand => new RelayCommand(() =>
    {
        AutoStep = true;
        MaxCutbacks = 5;
        RefinementLevels = 1;
        RefineThinFlash = false;
        RemeshQualityThreshold = 0.12;
        MaxNodeCount = SimulationConfig3D.DefaultMaxNodes;
        MaxIterations = 70;
        Tolerance = 1e-4;
    });

    // ================================================================== STEP import / re-import

    /// <summary>Where an imported die set came from, to re-import it with the same choices.</summary>
    private sealed record ImportSource(string Path, int TopBody, Vec3 TopCog, DateTime Stamp, string Hash);

    private readonly Dictionary<DieSet3D, ImportSource> _sources = [];
    private bool _sourceChanged;
    private int _sourceCheckTicks;

    private ImportSource? CurrentSource => _analysis3D && _sources.TryGetValue(_selectedDie3D, out var s) ? s : null;

    /// <summary>The selected die set was imported from STEP and can be re-imported.</summary>
    public bool CanReimport => CurrentSource is not null && !IsRunning;

    /// <summary>The imported STEP file was modified on disk since it was read.</summary>
    public bool SourceChanged { get => _sourceChanged; private set => Set(ref _sourceChanged, value); }

    public string SourceText => CurrentSource is { } s
        ? F($"Source: {Path.GetFileName(s.Path)} · top die body {s.TopBody} · read {s.Stamp:yyyy-MM-dd HH:mm:ss}")
        : "";

    /// <summary>
    /// Imports a 3-body STEP assembly: reads and classifies the bodies (off the UI thread), asks the view which outer
    /// body is the moving top die, then adds and selects the resulting 3D die set. Returns an error message or null.
    /// </summary>
    public Task<string?> ImportStepAsync(string path, Func<ForgingAssembly, CadBody?> chooseTopDie) =>
        ImportCoreAsync(path, chooseTopDie, replace: null, keepSettings: false);

    /// <summary>
    /// Re-reads the STEP file of the selected imported die set. Unchanged file: nothing to do (settings kept).
    /// Changed file: rebuilds the die set with the same top die and keeps every setting. Optionally solves afterwards.
    /// </summary>
    public async Task<string?> ReimportAsync(bool solveAfter)
    {
        var src = CurrentSource;
        if (src is null) return "The selected geometry was not imported from a STEP file.";
        if (!File.Exists(src.Path)) return "The STEP file no longer exists: " + src.Path;
        string hash = await Task.Run(() => FileHash(src.Path));
        if (hash == src.Hash)
        {
            SourceChanged = false;
            _logQueue.Enqueue($"Re-import: {Path.GetFileName(src.Path)} is unchanged since it was read; geometry and settings kept.");
            StatusText = "STEP file unchanged — geometry and settings kept.";
        }
        else
        {
            string? error = await ImportCoreAsync(src.Path, asm => PickSameTop(asm, src), replace: _selectedDie3D, keepSettings: true, userBillet: _selectedDie3D.UserBillet);
            if (error is not null) return error;
            StatusText = "Re-imported " + Path.GetFileName(src.Path) + " — all settings kept.";
        }

        if (solveAfter) await SolveAsync();
        return null;
    }

    /// <summary>The die that was the top die before: same body number if it is still an outer body, else nearest COG.</summary>
    private static CadBody PickSameTop(ForgingAssembly asm, ImportSource src)
    {
        foreach (var d in new[] { asm.DieA, asm.DieB })
        {
            if (d.Index + 1 == src.TopBody) return d;
        }

        return (asm.DieA.CenterOfGravity - src.TopCog).Length <= (asm.DieB.CenterOfGravity - src.TopCog).Length ? asm.DieA : asm.DieB;
    }

    private async Task<string?> ImportCoreAsync(string path, Func<ForgingAssembly, CadBody?> chooseTopDie, DieSet3D? replace, bool keepSettings, UserBillet? userBillet = null)
    {
        ArgumentNullException.ThrowIfNull(chooseTopDie);
        StatusText = "Reading STEP file " + Path.GetFileName(path) + "…";
        ForgingAssembly asm;
        string hash;
        DateTime stamp = DateTime.Now;
        try
        {
            (asm, hash) = await Task.Run(() => (ForgingAssembly.Classify(Path.GetFileName(path), StepImporter.Read(path)), FileHash(path)));
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
        {
            StatusText = "STEP import failed.";
            _logQueue.Enqueue("STEP IMPORT ERROR: " + ex.Message);
            return ex.Message;
        }

        foreach (var b in asm.Bodies)
        {
            _logQueue.Enqueue(F($"STEP body {b.Index + 1}: volume {b.Volume / 1000:F2} cm³, COG ({b.CenterOfGravity.X:F2}, {b.CenterOfGravity.Y:F2}, {b.CenterOfGravity.Z:F2}) mm, {b.Mesh.Triangles.Length / 3:N0} facets"));
        }

        var top = chooseTopDie(asm);
        if (top is null)
        {
            StatusText = "STEP import cancelled.";
            return null;
        }

        DieSet3D die;
        try
        {
            die = await Task.Run(() =>
            {
                var d = asm.BuildDieSet(top, _logQueue.Enqueue, asm.IsDieOnly ? userBillet ?? new UserBillet() : null);
                if (asm.IsDieOnly && userBillet is null) d.SetUserBillet(DefaultWorkpiece(d));
                d.CavityVolume();
                d.Place(0, 0);
                return d;
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            StatusText = "STEP import failed.";
            _logQueue.Enqueue("STEP IMPORT ERROR: " + ex.Message);
            return ex.Message;
        }

        _sources[die] = new ImportSource(Path.GetFullPath(path), top.Index + 1, top.CenterOfGravity, stamp, hash);
        // Insert, select, then remove the replaced entry: replacing the selected item in place makes the ComboBox
        // push a null selection back through the binding.
        int at = replace is null ? -1 : DieSets3D.IndexOf(replace);
        if (at >= 0) DieSets3D.Insert(at, die);
        else DieSets3D.Add(die);

        _keepSettings = keepSettings;
        try
        {
            Analysis3D = true;
            SelectedDie3D = die;
            Is3D = true;
        }
        finally
        {
            _keepSettings = false;
        }

        if (at >= 0)
        {
            _sources.Remove(replace!);
            DieSets3D.Remove(replace!);
        }

        SourceChanged = false;
        OnWorkflowChanged();
        RefreshDerived();
        if (!keepSettings) StatusText = "Imported " + asm.Name + ". Assign the workpiece material and press Solve.";
        return null;
    }

    private static string FileHash(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    /// <summary>Polls the imported file's timestamp (called from the UI timer) to flag edits made in CAD.</summary>
    private void CheckSourceFile()
    {
        if (++_sourceCheckTicks < 15) return; // ~1.5 s
        _sourceCheckTicks = 0;
        if (CurrentSource is not { } s || SourceChanged) return;
        try
        {
            if (File.Exists(s.Path) && File.GetLastWriteTime(s.Path) > s.Stamp) SourceChanged = true;
        }
        catch (IOException)
        {
        }
    }

    // ================================================================== setups (save / open)

    /// <summary>Every current input as a setup document.</summary>
    public RunSettings CaptureSettings()
    {
        var src = CurrentSource;
        return new RunSettings
        {
            Analysis = _analysis3D ? "3D" : "2D",
            Preset = src is null ? (_analysis3D ? _selectedDie3D.Name : _selectedDie.Name) : null,
            StepFile = src?.Path,
            TopDieBody = src?.TopBody,
            Workpiece = src is not null ? _selectedDie3D.UserBillet : null,
            Material = _selectedMaterial.Name,
            Temperature = _temperature,
            Friction = _friction,
            BilletRadius = src is null ? _billetRadius : null,
            BilletHeight = src is null ? _billetHeight : null,
            BilletLength = src is null ? _billetLength : null,
            ElementSize = _elementSize,
            StrokeIncrement = _strokeIncrement,
            RamSpeed = _ramSpeed,
            PressCapacity = _pressCapacity,
            LinearBackend = SelectedBackend.Backend.ToString(),
            AutoStep = _autoStep,
            MaxCutbacks = _maxCutbacks,
            RefinementLevels = _refinementLevels,
            RefineThinFlash = _refineThinFlash,
            RemeshQuality = _remeshQuality,
            MaxNodeCount = _maxNodeCount,
            MaxIterations = _maxIterations,
            Tolerance = _tolerance,
        };
    }

    /// <summary>Applies a setup: selects or imports the geometry, then every value. Returns an error message or null.</summary>
    public async Task<string?> ApplySettingsAsync(RunSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        bool is3D = !string.Equals(s.Analysis, "2D", StringComparison.OrdinalIgnoreCase);
        if (s.StepFile is { Length: > 0 } step)
        {
            if (!File.Exists(step)) return "STEP file not found: " + step;
            var existing = _sources.FirstOrDefault(kv => string.Equals(kv.Value.Path, Path.GetFullPath(step), StringComparison.OrdinalIgnoreCase)).Key;
            if (existing is not null)
            {
                Analysis3D = true;
                SelectedDie3D = existing;
                if (s.Workpiece is { } wp && existing.UserBillet is not null) UpdateWorkpiece(wp);
            }
            else
            {
                string? err = await ImportCoreAsync(step, asm => PickBody(asm, s.TopDieBody), replace: null, keepSettings: false, userBillet: s.Workpiece);
                if (err is not null) return err;
            }
        }
        else if (is3D)
        {
            var die = DieSets3D.FirstOrDefault(d => Matches(d.Name, s.Preset));
            if (die is null) return $"Unknown 3D die preset \"{s.Preset}\".";
            Analysis3D = true;
            SelectedDie3D = die;
        }
        else
        {
            var die = DieSets.FirstOrDefault(d => Matches(d.Name, s.Preset));
            if (die is null) return $"Unknown 2D die preset \"{s.Preset}\".";
            Analysis3D = false;
            SelectedDie = die;
        }

        _suspendDerived = true;
        try
        {
            var mat = Materials.FirstOrDefault(m => Matches(m.Name, s.Material));
            if (mat is not null) SelectedMaterial = mat;
            if (s.Temperature > 0 || mat is null) Temperature = s.Temperature;
            Friction = s.Friction;
            if (s.BilletRadius is { } r) BilletRadius = r;
            if (s.BilletHeight is { } hgt) BilletHeight = hgt;
            if (s.BilletLength is { } len) BilletLength = len;
            if (s.ElementSize > 0) ElementSize = s.ElementSize;
            if (s.StrokeIncrement > 0) StrokeIncrement = s.StrokeIncrement;
            if (s.RamSpeed > 0) RamSpeed = s.RamSpeed;
            if (s.PressCapacity > 0) PressCapacity = s.PressCapacity;
            if (Enum.TryParse<LinearBackend>(s.LinearBackend, true, out var lb)) SelectedBackend = Backends.FirstOrDefault(b => b.Backend == lb) ?? Backends[0];
            AutoStep = s.AutoStep;
            MaxCutbacks = s.MaxCutbacks;
            RefinementLevels = s.RefinementLevels;
            RefineThinFlash = s.RefineThinFlash;
            RemeshQualityThreshold = s.RemeshQuality;
            MaxNodeCount = s.MaxNodeCount;
            MaxIterations = s.MaxIterations;
            Tolerance = s.Tolerance;
        }
        finally
        {
            _suspendDerived = false;
        }

        RefreshDerived();
        return null;
    }

    private static CadBody PickBody(ForgingAssembly asm, int? body) =>
        body is { } b && (asm.DieA.Index + 1 == b || asm.DieB.Index + 1 == b) ? (asm.DieA.Index + 1 == b ? asm.DieA : asm.DieB) : asm.SuggestedTop;

    /// <summary>Case-insensitive name match that also accepts a unique prefix ("AISI 4140" for "AISI 4140 alloy steel").</summary>
    private static bool Matches(string name, string? wanted) =>
        !string.IsNullOrWhiteSpace(wanted) && (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase) || name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase));

    public void SaveSetup(string path)
    {
        var s = CaptureSettings();
        // Store the STEP path relative to the setup when it lives in the same folder tree (portable setups).
        if (s.StepFile is { } step)
        {
            string rel = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "", step);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel)) s = s with { StepFile = rel };
        }

        s.Save(path);
        StatusText = "Setup saved to " + Path.GetFileName(path) + ".";
    }

    public async Task<string?> OpenSetupAsync(string path)
    {
        RunSettings s;
        try
        {
            s = RunSettings.Load(path);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            return "Could not read the setup: " + ex.Message;
        }

        string? error = await ApplySettingsAsync(s);
        if (error is null) StatusText = "Setup " + Path.GetFileName(path) + " loaded.";
        return error;
    }

    // ================================================================== run history (telemetry overlays)

    private static readonly OxyColor[] RunPalette =
    [
        OxyColor.FromRgb(0xD9, 0x5F, 0x02), OxyColor.FromRgb(0x75, 0x70, 0xB3), OxyColor.FromRgb(0xE7, 0x29, 0x8A),
        OxyColor.FromRgb(0x66, 0xA6, 0x1E), OxyColor.FromRgb(0xA6, 0x76, 0x1D), OxyColor.FromRgb(0x1B, 0x9E, 0x77),
        OxyColor.FromRgb(0x66, 0x66, 0x66), OxyColor.FromRgb(0x2C, 0x7F, 0xB8),
    ];

    private int _runCounter;
    private RunRecord? _selectedRun;
    private string? _runLabelOverride;

    /// <summary>Finished runs of this session (newest first); their curves can be overlaid on the chart.</summary>
    public ObservableCollection<RunRecord> Runs { get; } = [];

    /// <summary>Selecting a run in RUNS shows its results again (viewport, slider, read-outs) for comparison.</summary>
    public RunRecord? SelectedRun
    {
        get => _selectedRun;
        set
        {
            if (!Set(ref _selectedRun, value)) return;
            if (value is { CanShow: true } && value != _shown && !IsRunning && !IsSweeping && !IsExporting) ShowRun(value);
            SyncOverlays();
        }
    }

    /// <summary>Number of runs whose full results are kept in memory for re-display.</summary>
    private const int KeepResults = 10;

    /// <summary>The run whose results are in the viewport (null: none / inputs changed since).</summary>
    private RunRecord? _shown;

    /// <summary>Puts a stored run back into the viewport, slider, chart and read-outs. The inputs on the left are not changed.</summary>
    private void ShowRun(RunRecord run)
    {
        if (run.Results is not { } r) return;
        StopPlayback();
        _shown = run;
        CurrentRun = run;
        _snapshots.Clear();
        _snapshots.AddRange(r.Snapshots);
        _totalStroke = r.Snapshots.Count > 0 ? r.Snapshots[^1].Stroke : 0;
        _preview = null;
        if (r.Die3D is not null && !_is3D)
        {
            _is3D = true;
            OnPropertyChanged(nameof(Is3D));
            OnPropertyChanged(nameof(Is2D));
        }

        ViewBounds = r.ViewBounds;
        Bounds3D = r.Bounds3D;
        foreach (var n in new[] { nameof(ActiveDie2D), nameof(ActiveDie3D), nameof(ShownPartLength), nameof(ScrubMaximum), nameof(HasResults), nameof(CanScrub) })
            OnPropertyChanged(n);
        RecomputeRanges();
        RebuildSeries();
        _scrubIndex = -1;
        ScrubIndex = ScrubMaximum;
        State = run.Status switch
        {
            SimulationStatus.Completed => RunState.Completed,
            SimulationStatus.Aborted => RunState.Aborted,
            _ => RunState.Failed,
        };
        StatusText = $"Showing {run.Label} — {run.Summary}. Pick another run to compare; inputs on the left are unchanged.";
    }

    /// <summary>
    /// Double-click on the telemetry chart: finds the curve nearest to <paramref name="at"/> (chart coordinates), loads
    /// that run into the viewport, selects it in RUNS and moves the slider to the increment under the cursor. Returns the
    /// run picked (null: no curve within reach, or busy).
    /// </summary>
    public RunRecord? PickCurve(ScreenPoint at)
    {
        if (IsRunning || IsSweeping || IsExporting) return null;
        if (HitCurve(at) is not { Run: { } run } hit) return null;

        if (!run.CanShow)
        {
            SelectedRun = run;
            StatusText = $"{run.Label}: full results were released to save memory (the {KeepResults} most recent runs keep them); only its telemetry is kept.";
            return run;
        }

        if (run != _shown) ShowRun(run);
        SelectedRun = run;
        ScrubIndex = Math.Clamp(hit.Index, 0, ScrubMaximum);
        if (CurrentSnapshot is { } s1)
            StatusText = $"Showing {run.Label} at step {s1.Step} — {U.Lf(s1.DieGap)} from die closure, {U.LoadfN(s1.LoadNewton)}, fill {s1.FillPercent.ToString("F1", Inv)} %.";
        return run;
    }

    /// <summary>
    /// The curve point nearest to <paramref name="at"/> (chart coordinates, within 14 px): which run, which increment,
    /// its telemetry and, while the run keeps its full results, the increment itself (for a thumbnail).
    /// </summary>
    public CurveHit? HitCurve(ScreenPoint at)
    {
        OxyPlot.Series.Series? best = null;
        TrackerHitResult? hit = null;
        double reach = 14; // px
        foreach (var s in LoadPlot.Series)
        {
            if (!s.IsVisible || s.GetNearestPoint(at, true) is not { } h) continue;
            double d = h.Position.DistanceTo(at);
            if (d >= reach) continue;
            reach = d;
            best = s;
            hit = h;
        }

        if (best is null || hit is null) return null;
        int index = (int)Math.Round(hit.Index);
        bool load = best == _loadSeries || Runs.Any(r => r.LoadSeries == best);
        if (best == _loadSeries || best == _fillSeries)
        {
            if (index < 0 || index >= _snapshots.Count) return null;
            var run = _shown ?? CurrentRun;
            var snap = _snapshots[index];
            return new CurveHit(run, index, TelemetryRow.From(snap), snap, ActiveDie2D, ViewBounds,
                run?.Label ?? "Current run", run?.Color ?? _loadSeries.Color, load);
        }

        var rec = Runs.FirstOrDefault(r => r.LoadSeries == best || r.FillSeries == best);
        if (rec is null || index < 0 || index >= rec.Rows.Count) return null;
        var results = rec.Results;
        var s2 = results is not null && index < results.Snapshots.Count ? results.Snapshots[index] : null;
        return new CurveHit(rec, index, rec.Rows[index], s2, results?.Die2D, results?.ViewBounds ?? ViewBounds, rec.Label, rec.Color, load);
    }

    /// <summary>Out-of-plane length used to draw the shown 2D plane-strain result.</summary>
    public double ShownPartLength => _shown?.Results?.PartLength ?? _billetLength;

    public bool HasRuns => Runs.Count > 0;

    private void AddRunRecord(SimulationResult result, RunSettings settings, TimeSpan elapsed)
    {
        if (result.Snapshots.Count == 0) return;
        int n = ++_runCounter;
        string label = _runLabelOverride ?? DefaultLabel(settings);
        var rec = new RunRecord(n, F($"#{n} {label}"), settings, result.Snapshots.Select(TelemetryRow.From).ToList(), result.Status, elapsed, RunPalette[(n - 1) % RunPalette.Length])
        {
            Results = new RunResults(result.Snapshots.ToList(), _analysis3D ? null : _selectedDie, _analysis3D ? _selectedDie3D : null, ViewBounds, Bounds3D, _billetLength, RunLog),
            Context = new RunContext(GeometryName, DieDescriptionText, MaterialSummary, AnalysisText),
        };
        rec.ShownChanged += (_, _) => SyncOverlays();
        _shown = rec; // already in the viewport
        Runs.Insert(0, rec);
        for (int i = KeepResults; i < Runs.Count; i++) Runs[i].Results = null; // bound memory
        SelectedRun = rec;
        CurrentRun = rec;
        SyncOverlays();
        OnPropertyChanged(nameof(HasRuns));
    }

    private static string DefaultLabel(RunSettings s)
    {
        string geo = s.StepFile is { } f ? Path.GetFileNameWithoutExtension(f) : s.Preset ?? "";
        string mat = s.Material.Split(' ') is { Length: > 1 } parts ? parts[0] + " " + parts[1] : s.Material;
        return $"{geo} · {mat} · {U.Tf(s.Temperature)} · m {s.Friction.ToString("F2", Inv)} · v {U.L(s.RamSpeed).ToString("F1", Inv)}";
    }

    /// <summary>Puts the shown runs' curves on the chart (behind the live run).</summary>
    private void SyncOverlays()
    {
        var keep = new[] { (OxyPlot.Series.Series)_loadSeries, _fillSeries };
        foreach (var s in LoadPlot.Series.Except(keep).ToList()) LoadPlot.Series.Remove(s);
        int i = 0;
        foreach (var r in Runs) r.SetHighlight(r == _selectedRun);
        foreach (var r in Runs.Reverse().Where(r => r.IsShown))
        {
            LoadPlot.Series.Insert(i++, r.LoadSeries);
            LoadPlot.Series.Insert(i++, r.FillSeries);
        }

        LoadPlot.InvalidatePlot(true);
    }

    public ICommand ClearRunsCommand => new RelayCommand(() =>
    {
        Runs.Clear();
        SelectedRun = null;
        SyncOverlays();
        OnPropertyChanged(nameof(HasRuns));
    }, () => !IsRunning && Runs.Count > 0);

    /// <summary>
    /// Removes one run (its telemetry, chart overlay and stored results) from RUNS. When it is the run in the viewport,
    /// the viewport falls back to the next run with stored results, else to the setup preview. Not while solving.
    /// </summary>
    public bool DeleteRun(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (IsRunning || IsSweeping || IsExporting || !Runs.Contains(run)) return false;
        int index = Runs.IndexOf(run);
        bool wasShown = run == _shown || run == CurrentRun;
        Runs.Remove(run);
        if (wasShown)
        {
            _shown = null;
            CurrentRun = null;
            var next = Runs.Skip(index).Concat(Runs.Take(index)).FirstOrDefault(r => r.CanShow);
            if (next is not null)
            {
                SelectedRun = next;
            }
            else
            {
                SelectedRun = null;
                ResetResults();
                State = RunState.Idle;
                BuildPreview();
            }
        }
        else if (run == _selectedRun)
        {
            SelectedRun = null;
        }

        SyncOverlays();
        OnPropertyChanged(nameof(HasRuns));
        StatusText = $"Deleted run {run.Label}.";
        return true;
    }

    public ICommand ShowAllRunsCommand => new RelayCommand(() =>
    {
        bool any = Runs.Any(r => !r.IsShown);
        foreach (var r in Runs) r.IsShown = any;
    }, () => Runs.Count > 0);

    /// <summary>Writes the telemetry of a run (default: the selected one) to CSV, with its setup as comment lines.</summary>
    public void SaveTelemetry(string path, RunRecord? run = null)
    {
        run ??= SelectedRun ?? throw new InvalidOperationException("No run selected.");
        TelemetryCsv.Write(path, run.Rows, RunComments(run));
        // The setup beside it, so the run can be reproduced exactly.
        string setup = Path.ChangeExtension(path, null) + RunSettings.FileExtension;
        run.Settings.Save(setup);
        StatusText = $"Telemetry of run #{run.Number} saved to {Path.GetFileName(path)} (+ setup {Path.GetFileName(setup)}).";
    }

    private static IEnumerable<string> RunComments(RunRecord r)
    {
        yield return $"ImpressionFEA {AppVersion.Display} run #{r.Number}: {r.Label}";
        yield return FormattableString.Invariant($"status {r.StatusText}, peak load {r.PeakLoad:F2} t, final fill {r.FinalFill:F2} %, stroke {r.FinalStroke:F3} mm, volume change {r.VolumeChange:F3} %, solve time {r.Elapsed.TotalSeconds:F1} s");
        yield return "setup " + System.Text.Json.JsonSerializer.Serialize(r.Settings, RunSettings.JsonOptions).Replace(Environment.NewLine, " ").Replace("  ", "");
    }

    /// <summary>Saves every run's telemetry (one CSV each) and a summary table into a folder.</summary>
    public string SaveAllTelemetry(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var r in Runs) SaveTelemetry(Path.Combine(folder, F($"run{r.Number:000}.csv")), r);
        string summary = Path.Combine(folder, "summary.csv");
        WriteRunSummary(summary, Runs.Reverse());
        StatusText = $"{Runs.Count} runs saved to {folder}.";
        return summary;
    }

    /// <summary>One row per run: status, key results and the main inputs (mm, t, °C).</summary>
    internal static void WriteRunSummary(string path, IEnumerable<RunRecord> runs)
    {
        var lines = new List<string> { "run,label,status,peak_load_t,final_fill_pct,final_stroke_mm,volume_change_pct,solve_time_s,material,temperature_C,friction,ram_speed_mm_s,element_size_mm,stroke_increment_mm" };
        foreach (var r in runs)
        {
            var s = r.Settings;
            lines.Add(string.Join(',', r.Number, TelemetryCsv.Field(r.Label), r.StatusText,
                FormattableString.Invariant($"{r.PeakLoad:F3}"), FormattableString.Invariant($"{r.FinalFill:F3}"), FormattableString.Invariant($"{r.FinalStroke:F4}"),
                FormattableString.Invariant($"{r.VolumeChange:F4}"), FormattableString.Invariant($"{r.Elapsed.TotalSeconds:F1}"), TelemetryCsv.Field(s.Material),
                FormattableString.Invariant($"{s.Temperature:F1}"), FormattableString.Invariant($"{s.Friction:F3}"), FormattableString.Invariant($"{s.RamSpeed:F2}"),
                FormattableString.Invariant($"{s.ElementSize:F3}"), FormattableString.Invariant($"{s.StrokeIncrement:F4}")));
        }

        File.WriteAllLines(path, lines);
    }

    // ================================================================== multi-run

    private bool _sweepAbort;
    private bool _isSweeping;
    private string _sweepText = "";

    public bool IsSweeping
    {
        get => _isSweeping;
        private set
        {
            if (!Set(ref _isSweeping, value)) return;
            OnPropertyChanged(nameof(CanExport));
            OnPropertyChanged(nameof(CanEditQueue));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string SweepText { get => _sweepText; private set => Set(ref _sweepText, value); }

    public static IReadOnlyList<SweepParameter> SweepParameters { get; } = Enum.GetValues<SweepParameter>();

    /// <summary>Unit and a sensible default value list for a sweep parameter, around the current setting.</summary>
    public (string Unit, string Suggestion) SweepDefaults(SweepParameter p) => p switch
    {
        SweepParameter.Temperature => (U.Temperature, string.Join(", ", new[] { -100, -50, 0, 50 }.Select(d => U.T(_temperature + d).ToString("F0", Inv)))),
        SweepParameter.Friction => ("–", "0.1, 0.2, 0.3, 0.5"),
        SweepParameter.RamSpeed => (U.Speed, string.Join(", ", new[] { 0.5, 1, 2, 4 }.Select(k => U.L(_ramSpeed * k).ToString(U.LengthFormat, Inv)))),
        SweepParameter.ElementSize => (U.Length, string.Join(", ", new[] { 1.25, 1.0, 0.8 }.Select(k => U.L(_elementSize * k).ToString(U.FineLengthFormat, Inv)))),
        SweepParameter.StrokeIncrement => (U.Length, string.Join(", ", new[] { 1.0, 0.5, 0.25 }.Select(k => U.L(_strokeIncrement * k).ToString(U.FineLengthFormat, Inv)))),
        SweepParameter.WorkpieceOffsetX => (U.Length, string.Join(", ", new[] { 0.0, 5, 10, 15 }.Select(mm => U.L(mm).ToString(U.LengthFormat, Inv)))),
        _ => ("", "AISI 4140, 6061-T6, C11000, Modeling plasticine"),
    };

    /// <summary>
    /// Runs the current setup once per value of <paramref name="parameter"/>, keeping every run in the history and
    /// optionally writing each run's telemetry and a summary CSV to <paramref name="folder"/>. The original value is
    /// restored afterwards; Abort stops the whole batch.
    /// </summary>
    public async Task<string?> RunSweepAsync(SweepParameter parameter, IReadOnlyList<string> values, string? folder)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return "Enter at least one value.";
        var original = CaptureSettings();
        var done = new List<RunRecord>();
        IsSweeping = true;
        _sweepAbort = false;
        try
        {
            for (int i = 0; i < values.Count && !_sweepAbort; i++)
            {
                string? err = ApplySweepValue(parameter, values[i]);
                if (err is not null)
                {
                    _logQueue.Enqueue("MULTI-RUN: " + err);
                    continue;
                }

                SweepText = F($"Multi-run {i + 1}/{values.Count}: {parameter} = {values[i]}");
                _runLabelOverride = F($"{parameter} = {values[i]} · {DefaultLabel(CaptureSettings())}");
                await SolveAsync();
                _runLabelOverride = null;
                if (Runs.FirstOrDefault() is { } rec && !done.Contains(rec))
                {
                    done.Add(rec);
                    if (folder is not null)
                    {
                        Directory.CreateDirectory(folder);
                        SaveTelemetry(Path.Combine(folder, F($"run{rec.Number:000}_{Sanitize(parameter + "_" + values[i])}.csv")), rec);
                    }
                }

                if (State == RunState.Failed && _snapshots.Count == 0) break; // input error: no point continuing
            }
        }
        finally
        {
            _runLabelOverride = null;
            IsSweeping = false;
            SweepText = "";
            await ApplySettingsAsync(original);
        }

        if (folder is not null && done.Count > 0) WriteRunSummary(Path.Combine(folder, "summary.csv"), done);
        string table = string.Join(Environment.NewLine, done.Select(r => F($"  #{r.Number,-3} {r.Label,-70} peak {r.PeakLoad,8:F1} t   fill {r.FinalFill,6:F1} %   {r.StatusText}")));
        _logQueue.Enqueue($"=== Multi-run {(_sweepAbort ? "aborted" : "finished")}: {done.Count} of {values.Count} runs ===" + Environment.NewLine + table);
        StatusText = F($"Multi-run {(_sweepAbort ? "aborted" : "finished")}: {done.Count} runs. Curves are overlaid on the telemetry chart.") + (folder is not null ? " Telemetry saved to " + folder : "");
        return null;
    }

    private static string Sanitize(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));

    private string? ApplySweepValue(SweepParameter p, string value)
    {
        if (p == SweepParameter.Material)
        {
            var m = Materials.FirstOrDefault(x => Matches(x.Name, value.Trim()));
            if (m is null) return $"Unknown material \"{value}\".";
            SelectedMaterial = m; // also sets T to the material's default forging temperature, as a material comparison needs
            return null;
        }

        if (!double.TryParse(value, System.Globalization.NumberStyles.Float, Inv, out double v)) return $"\"{value}\" is not a number.";
        switch (p)
        {
            // Sweep values are entered in display units.
            case SweepParameter.Temperature: TemperatureU = v; break;
            case SweepParameter.Friction: Friction = v; break;
            case SweepParameter.RamSpeed: RamSpeedU = v; break;
            case SweepParameter.ElementSize: ElementSizeU = v; break;
            case SweepParameter.StrokeIncrement: StrokeIncrementU = v; break;
            case SweepParameter.WorkpieceOffsetX:
                if (!IsUserBillet) return "Workpiece position can only be swept for die-only imports.";
                WorkpieceOffsetXU = v;
                break;
        }

        return null;
    }

    // ================================================================== demos

    private DemoDefinition? _selectedDemo;
    private DemoDefinition? _activeDemo;

    public IReadOnlyList<DemoDefinition> Demos { get; private set; } = [];

    public DemoDefinition? SelectedDemo { get => _selectedDemo; set => Set(ref _selectedDemo, value); }

    /// <summary>The demo whose setup is loaded (its guide is shown on the Basic tab).</summary>
    public DemoDefinition? ActiveDemo { get => _activeDemo; private set => Set(ref _activeDemo, value); }

    public string DemoFolder => DemoCatalog.Folder;

    private void LoadDemos()
    {
        Demos = DemoCatalog.Load(out var errors);
        foreach (var e in errors) _logQueue.Enqueue("DEMO: " + e);
        SelectedDemo = Demos.FirstOrDefault();
        OnPropertyChanged(nameof(Demos));
    }

    public ICommand ReloadDemosCommand => new RelayCommand(LoadDemos);

    public async Task<string?> LoadDemoAsync(DemoDefinition demo)
    {
        ArgumentNullException.ThrowIfNull(demo);
        string? err = await ApplySettingsAsync(demo.Setup);
        if (err is not null) return err;
        ActiveDemo = demo;
        _logQueue.Enqueue("=== Demo: " + demo.Title + " ===" + Environment.NewLine + demo.Guide);
        StatusText = "Demo loaded: " + demo.Title + ". Follow the guide, then press Solve.";
        return null;
    }

    public ICommand CloseDemoCommand => new RelayCommand(() => ActiveDemo = null);

    private bool _keepSettings;

    /// <summary>Demo sweeps are written in mm, °C and mm/s; shows them in the current display units.</summary>
    public string DemoSweepText(DemoSweep sweep)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        if (!Enum.TryParse<SweepParameter>(sweep.Parameter, true, out var p)) return string.Join(", ", sweep.Values);
        return string.Join(", ", sweep.Values.Select(v =>
        {
            if (!double.TryParse(v, System.Globalization.NumberStyles.Float, Inv, out double x)) return v;
            return p switch
            {
                SweepParameter.Temperature => U.T(x).ToString("F0", Inv),
                SweepParameter.RamSpeed => U.L(x).ToString(U.LengthFormat, Inv),
                SweepParameter.ElementSize or SweepParameter.StrokeIncrement => U.L(x).ToString(U.FineLengthFormat, Inv),
                SweepParameter.WorkpieceOffsetX => U.L(x).ToString(U.LengthFormat, Inv),
                _ => v,
            };
        }));
    }

    private void OnWorkflowChanged()
    {
        RaiseWorkpiece();
        OnPropertyChanged(nameof(CanReimport));
        OnPropertyChanged(nameof(SourceText));
    }
}
