using ImpressionFEA.App.Infrastructure;
using ImpressionFEA.Core.Simulation;

namespace ImpressionFEA.App.ViewModels;

public enum QueueItemState
{
    Pending,
    Running,
    Done,
    Failed,
    Aborted,
}

/// <summary>A queued simulation: a complete setup (any input can differ from the others), its name and its outcome.</summary>
public sealed class QueueItem : ObservableObject
{
    private string _name;
    private RunSettings _settings;
    private QueueItemState _state;
    private string _detail = "";
    private string? _message;
    private RunRecord? _result;
    private int _position;

    public QueueItem(string name, RunSettings settings, QueueItemState state = QueueItemState.Pending)
    {
        _name = name;
        _settings = settings;
        _state = state;
    }

    public string Name { get => _name; set => Set(ref _name, value); }

    public RunSettings Settings { get => _settings; set => Set(ref _settings, value); }

    public QueueItemState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsPending));
            OnPropertyChanged(nameof(Tip));
        }
    }

    public bool IsPending => _state == QueueItemState.Pending;

    /// <summary>1-based position in the queue.</summary>
    public int Position { get => _position; set => Set(ref _position, value); }

    /// <summary>What sets this item apart from the others in the queue (the values of the inputs that differ).</summary>
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    /// <summary>Why the item failed, or the run's summary when done.</summary>
    public string? Message
    {
        get => _message;
        set
        {
            if (Set(ref _message, value)) OnPropertyChanged(nameof(Tip));
        }
    }

    /// <summary>The run this item produced (in RUNS).</summary>
    public RunRecord? Result
    {
        get => _result;
        set
        {
            if (Set(ref _result, value)) OnPropertyChanged(nameof(Tip));
        }
    }

    public string StateText => _state switch
    {
        QueueItemState.Pending => "pending",
        QueueItemState.Running => "running",
        QueueItemState.Done => "done",
        QueueItemState.Failed => "failed",
        _ => "aborted",
    };

    public string Tip => _name + Environment.NewLine + StateText + (_result is { } r ? " · " + r.Label + " · " + r.Summary : "")
        + (_message is { Length: > 0 } m ? Environment.NewLine + m : "")
        + Environment.NewLine + (_state == QueueItemState.Pending ? "Double-click to load it into the configuration and edit it." : "Double-click to show its results.");
}
