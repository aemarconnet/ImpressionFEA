using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using ImpressionFEA.App.Export;
using ImpressionFEA.App.Infrastructure;
using ImpressionFEA.Core.Simulation;

namespace ImpressionFEA.App.ViewModels;

/// <summary>
/// Simulation queue: complete setups (any input can differ) added from the configuration panel, from a multi-run or
/// from setup files, then solved one after the other. Each finished item appears in RUNS like any other run. The
/// configuration is locked while the queue runs and is put back afterwards. The queue is saved automatically.
/// </summary>
public sealed partial class MainViewModel
{
    public const string QueueFileExtension = ".ifea-queue.json";

    private sealed record QueueEntry(string Name, QueueItemState State, RunSettings Settings);

    private static readonly string QueueAutosave = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImpressionFEA", "queue" + QueueFileExtension);

    private QueueItem? _selectedQueueItem;
    private QueueItem? _editingItem;
    private bool _isQueueRunning;
    private bool _queueStopAfter;
    private bool _queueSkip;
    private bool _queueLoading;

    public ObservableCollection<QueueItem> Queue { get; } = [];

    public QueueItem? SelectedQueueItem { get => _selectedQueueItem; set => Set(ref _selectedQueueItem, value); }

    /// <summary>The pending item loaded into the configuration panel for editing (Update writes the panel back to it).</summary>
    public QueueItem? EditingItem
    {
        get => _editingItem;
        private set
        {
            if (Set(ref _editingItem, value)) OnPropertyChanged(nameof(EditingText));
        }
    }

    public string EditingText => _editingItem is { } q ? F($"Editing queue item {q.Position}. Change any input, then Update to save it to the item.") : "";

    public bool IsQueueRunning
    {
        get => _isQueueRunning;
        private set
        {
            if (!Set(ref _isQueueRunning, value)) return;
            OnPropertyChanged(nameof(QueueNeedsAttention));
            OnPropertyChanged(nameof(CanEditQueue));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>The queue can be changed (nothing is solving or exporting).</summary>
    public bool CanEditQueue => !IsRunning && !IsSweeping && !IsExporting && !IsCheckingMesh;

    public int PendingCount => Queue.Count(q => q.State == QueueItemState.Pending);

    /// <summary>Items wait to be solved (or the queue is running): the Queue tab pulses until it is opened.</summary>
    public bool QueueNeedsAttention => PendingCount > 0 || IsQueueRunning;

    public string QueueHeader => PendingCount > 0 ? F($"Queue ({PendingCount})") : "Queue";

    public string QueueSummary => Queue.Count == 0
        ? ""
        : F($"{Queue.Count} item{(Queue.Count == 1 ? "" : "s")} · {PendingCount} pending · {Queue.Count(q => q.State == QueueItemState.Done)} done")
          + (Queue.Count(q => q.State is QueueItemState.Failed or QueueItemState.Aborted) is int bad and > 0 ? F($" · {bad} failed / aborted") : "");

    private void InitQueue()
    {
        Queue.CollectionChanged += OnQueueChanged;
        LoadQueueFile(QueueAutosave, replace: true, autosave: true);
    }

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (QueueItem q in e.NewItems) q.PropertyChanged += OnQueueItemChanged;
        }

        if (e.OldItems is not null)
        {
            foreach (QueueItem q in e.OldItems)
            {
                q.PropertyChanged -= OnQueueItemChanged;
                if (q == _editingItem) EditingItem = null;
            }
        }

        QueueChanged();
    }

    private void OnQueueItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QueueItem.Settings) or nameof(QueueItem.State) or nameof(QueueItem.Name)) QueueChanged();
    }

    /// <summary>Positions, "what differs" details, counts and the autosave.</summary>
    private void QueueChanged()
    {
        var varied = RunComparison.Split(Queue.Select(q => q.Settings)).Varied;
        for (int i = 0; i < Queue.Count; i++)
        {
            var q = Queue[i];
            q.Position = i + 1;
            q.Detail = Queue.Count > 1 && varied.Count is > 0 and <= 4
                ? string.Join(" · ", varied.Select(p => (p.Name.StartsWith("Friction", StringComparison.Ordinal) ? "m " : "") + p.Show(q.Settings)))
                : Queue.Count > 1 && varied.Count == 0 ? "same inputs as the other items" : "";
        }

        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(QueueNeedsAttention));
        OnPropertyChanged(nameof(QueueHeader));
        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(EditingText));
        if (!_queueLoading) SaveQueueFile(QueueAutosave);
    }

    // ------------------------------------------------------------------ building the queue

    /// <summary>Adds the current configuration as a new pending item.</summary>
    public ICommand AddToQueueCommand => new RelayCommand(() =>
    {
        var s = CaptureSettings();
        var item = new QueueItem(DefaultLabel(s), s);
        Queue.Add(item);
        SelectedQueueItem = item;
        StatusText = F($"Added to the queue as item {item.Position}. Change any input and add again, then Run queue.");
    }, () => CanEditQueue);

    /// <summary>Writes the configuration panel back to the item being edited.</summary>
    public ICommand UpdateQueueItemCommand => new RelayCommand(() =>
    {
        if (_editingItem is not { } q) return;
        var s = CaptureSettings();
        q.Settings = s;
        q.Name = DefaultLabel(s);
        q.State = QueueItemState.Pending;
        q.Message = null;
        StatusText = F($"Queue item {q.Position} updated.");
    }, () => _editingItem is not null && CanEditQueue);

    public ICommand FinishEditingCommand => new RelayCommand(() => EditingItem = null);

    /// <summary>Loads an item into the configuration panel; pending items can then be edited and updated.</summary>
    public async Task<string?> LoadQueueItemAsync(QueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!CanEditQueue) return "The queue can't be edited while a simulation, queue, multi-run or export is running.";
        string? err = await ApplySettingsAsync(item.Settings);
        if (err is not null) return err;
        EditingItem = item.IsPending ? item : null;
        SelectedQueueItem = item;
        StatusText = item.IsPending
            ? F($"Queue item {item.Position} loaded into the configuration: change any input, then Update.")
            : F($"Queue item {item.Position} loaded into the configuration ({item.StateText}). Add it to the queue again to rerun it with changes.");
        return null;
    }

    public void DuplicateQueueItem(QueueItem item)
    {
        if (!CanEditQueue) return;
        var copy = new QueueItem(item.Name, item.Settings);
        Queue.Insert(Queue.IndexOf(item) + 1, copy);
        SelectedQueueItem = copy;
    }

    public void RemoveQueueItem(QueueItem item)
    {
        if (CanEditQueue) Queue.Remove(item);
    }

    public void MoveQueueItem(QueueItem item, int delta)
    {
        int i = Queue.IndexOf(item), j = i + delta;
        if (!CanEditQueue || i < 0 || j < 0 || j >= Queue.Count) return;
        Queue.Move(i, j);
    }

    /// <summary>Puts a finished, failed or aborted item back in the queue.</summary>
    public void RequeueItem(QueueItem item)
    {
        if (!CanEditQueue) return;
        item.State = QueueItemState.Pending;
        item.Message = null;
        item.Result = null;
    }

    /// <summary>Shows the run a finished item produced.</summary>
    public void ShowQueueResult(QueueItem item)
    {
        if (item.Result is { } r && Runs.Contains(r)) SelectedRun = r;
        else StatusText = F($"Queue item {item.Position} has no run in RUNS (not run yet, deleted, or from a previous session).");
    }

    public ICommand ClearFinishedCommand => new RelayCommand(() =>
    {
        foreach (var q in Queue.Where(q => q.State != QueueItemState.Pending).ToList()) Queue.Remove(q);
    }, () => CanEditQueue && Queue.Any(q => q.State != QueueItemState.Pending));

    public ICommand ClearQueueCommand => new RelayCommand(() =>
    {
        Queue.Clear();
        EditingItem = null;
    }, () => CanEditQueue && Queue.Count > 0);

    /// <summary>Adds setup files as pending items; returns a message for the files that could not be read.</summary>
    public string? AddSetupsToQueue(IEnumerable<string> paths)
    {
        var problems = new List<string>();
        int added = 0;
        foreach (string p in paths)
        {
            try
            {
                var s = RunSettings.Load(p);
                Queue.Add(new QueueItem(DefaultLabel(s), s));
                added++;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                problems.Add(Path.GetFileName(p) + ": " + ex.Message);
            }
        }

        StatusText = F($"{added} setup{(added == 1 ? "" : "s")} added to the queue.");
        return problems.Count > 0 ? "Some setups could not be read:" + Environment.NewLine + string.Join(Environment.NewLine, problems) : null;
    }

    /// <summary>A multi-run as queue items: one per value, from the current configuration (which is put back).</summary>
    public async Task<string?> AddSweepToQueueAsync(SweepParameter parameter, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return "Enter at least one value.";
        if (!CanEditQueue) return "The queue can't be changed while a simulation, queue, multi-run or export is running.";
        var original = CaptureSettings();
        var errors = new List<string>();
        int added = 0;
        try
        {
            foreach (string v in values)
            {
                if (ApplySweepValue(parameter, v) is { } err)
                {
                    errors.Add(err);
                    continue;
                }

                var s = CaptureSettings();
                Queue.Add(new QueueItem(F($"{parameter} = {v} · {DefaultLabel(s)}"), s));
                added++;
            }
        }
        finally
        {
            await ApplySettingsAsync(original);
        }

        StatusText = F($"{added} multi-run item{(added == 1 ? "" : "s")} added to the queue. Edit any of them, then Run queue.");
        return errors.Count > 0 ? string.Join(Environment.NewLine, errors) : null;
    }

    // ------------------------------------------------------------------ running

    public ICommand RunQueueCommand => new RelayCommand(() => _ = RunQueueAsync(), () => CanEditQueue && PendingCount > 0);

    /// <summary>Finishes the item that is solving, then stops.</summary>
    public ICommand StopQueueAfterCurrentCommand => new RelayCommand(() =>
    {
        _queueStopAfter = true;
        StatusText = "The queue stops after the current item.";
    }, () => IsQueueRunning && !_queueStopAfter);

    /// <summary>Aborts the item that is solving and carries on with the next one.</summary>
    public ICommand SkipQueueItemCommand => new RelayCommand(() =>
    {
        _queueSkip = true;
        _cts?.Cancel();
    }, () => IsQueueRunning && IsRunning);

    /// <summary>Solve times of the items completed in the running queue (for the queue ETA).</summary>
    private readonly List<double> _queueRunSeconds = [];

    /// <summary>Solves every pending item in order. Abort (or Esc) stops the queue; failed items are marked and skipped.</summary>
    public async Task RunQueueAsync()
    {
        if (!CanEditQueue || PendingCount == 0) return;
        var original = CaptureSettings();
        EditingItem = null;
        _sweepAbort = false;
        _queueStopAfter = false;
        IsSweeping = true;
        IsQueueRunning = true;
        var finished = new List<QueueItem>();
        int total = PendingCount;
        _queueRunSeconds.Clear();
        try
        {
            while (!_sweepAbort && !_queueStopAfter && Queue.FirstOrDefault(q => q.State == QueueItemState.Pending) is { } item)
            {
                item.State = QueueItemState.Running;
                item.Message = null;
                item.Result = null;
                SelectedQueueItem = item;
                SweepText = F($"Queue {finished.Count + 1} / {total}: item {item.Position} · {item.Name}");
                finished.Add(item);

                string? err = await ApplySettingsAsync(item.Settings);
                if (err is not null)
                {
                    item.State = QueueItemState.Failed;
                    item.Message = err;
                    _logQueue.Enqueue(F($"QUEUE item {item.Position}: {err}"));
                    continue;
                }

                _queueSkip = false;
                _runLabelOverride = F($"Q{item.Position} · {item.Name}");
                var before = Runs.FirstOrDefault();
                await SolveAsync();
                _runLabelOverride = null;
                var rec = Runs.FirstOrDefault();
                if (rec is not null && rec != before)
                {
                    item.Result = rec;
                    item.State = rec.Status switch
                    {
                        SimulationStatus.Completed => QueueItemState.Done,
                        SimulationStatus.Aborted => QueueItemState.Aborted,
                        _ => QueueItemState.Failed,
                    };
                    item.Message = rec.Status == SimulationStatus.Completed ? null : StatusText;
                    if (rec.Status == SimulationStatus.Completed) _queueRunSeconds.Add(rec.Elapsed.TotalSeconds);
                }
                else
                {
                    item.State = _queueSkip || _sweepAbort ? QueueItemState.Aborted : QueueItemState.Failed;
                    item.Message = StatusText;
                }

                if (_queueSkip) item.Message = "Skipped by the operator.";
            }
        }
        finally
        {
            foreach (var q in Queue.Where(q => q.State == QueueItemState.Running)) q.State = QueueItemState.Pending;
            _runLabelOverride = null;
            IsSweeping = false;
            IsQueueRunning = false;
            SweepText = "";
            await ApplySettingsAsync(original);
        }

        int ok = finished.Count(q => q.State == QueueItemState.Done);
        string table = string.Join(Environment.NewLine, finished.Select(q => F($"  Q{q.Position,-3} {q.StateText,-8} {q.Name}{(q.Result is { } r ? "   peak " + r.PeakLoad.ToString("F1", Inv) + " t   fill " + r.FinalFill.ToString("F1", Inv) + " %" : "")}")));
        bool stopped = _sweepAbort || _queueStopAfter;
        _logQueue.Enqueue($"=== Queue {(stopped ? "stopped" : "finished")}: {ok} of {finished.Count} items done ===" + Environment.NewLine + table);
        StatusText = F($"Queue {(stopped ? "stopped" : "finished")}: {ok} of {finished.Count} items done, {PendingCount} pending. Results are in RUNS; Export ▸ Comparison report compares them.");
    }

    // ------------------------------------------------------------------ files

    /// <summary>Saves the queue (names, states and complete setups) to a file.</summary>
    public void SaveQueueFile(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var entries = Queue.Select(q => new QueueEntry(q.Name, q.State == QueueItemState.Running ? QueueItemState.Pending : q.State, q.Settings)).ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(entries, RunSettings.JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (path != QueueAutosave) throw;
        }
    }

    /// <summary>Reads a queue file, replacing the queue or appending to it. Returns an error message or null.</summary>
    public string? LoadQueueFile(string path, bool replace, bool autosave = false)
    {
        if (!File.Exists(path)) return autosave ? null : "File not found: " + path;
        List<QueueEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<QueueEntry>>(File.ReadAllText(path), RunSettings.JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return "Could not read the queue: " + ex.Message;
        }

        string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        _queueLoading = true;
        try
        {
            if (replace) Queue.Clear();
            foreach (var e in entries ?? [])
            {
                if (e.Settings is null) continue;
                var state = e.State == QueueItemState.Running ? QueueItemState.Pending : e.State;
                Queue.Add(new QueueItem(e.Name, e.Settings.ResolvedAgainst(folder), state)
                {
                    Message = autosave && state != QueueItemState.Pending ? "From a previous session." : null,
                });
            }
        }
        finally
        {
            _queueLoading = false;
        }

        QueueChanged();
        if (!autosave) StatusText = F($"Queue {Path.GetFileName(path)} loaded: {Queue.Count} items, {PendingCount} pending.");
        return null;
    }
}
