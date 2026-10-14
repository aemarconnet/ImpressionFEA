using System.Windows.Input;
using ImpressionFEA.App.Infrastructure;

namespace ImpressionFEA.App.ViewModels;

/// <summary>
/// Input problems, shown as an overlay at the top left of the viewport: errors (the run cannot start and the
/// workpiece preview is not drawn) and warnings (the run is possible but less reliable).
/// </summary>
public sealed partial class MainViewModel
{
    private IReadOnlyList<string> _inputErrors = [];
    private IReadOnlyList<string> _inputWarnings = [];
    private string? _previewError;
    private bool _issuesExpanded = true;

    public bool HasInputErrors => _inputErrors.Count > 0 || _previewError is not null;

    public bool HasInputWarnings => _inputWarnings.Count > 0;

    /// <summary>The overlay is shown: there are issues and no results are on screen.</summary>
    public bool ShowIssues => (HasInputErrors || HasInputWarnings) && !HasResults && !IsRunning;

    public string ErrorHeading => _preview is null
        ? (_analysis3D ? "Cannot solve: the workpiece is not shown" : "Cannot solve: the billet is not shown")
        : "Cannot solve";

    public string ErrorText => string.Join(Environment.NewLine, _previewError is null ? _inputErrors : _inputErrors.Append(_previewError));

    public string WarningText => string.Join(Environment.NewLine, _inputWarnings);

    public string IssueSummary
    {
        get
        {
            int e = _inputErrors.Count + (_previewError is null ? 0 : 1), w = _inputWarnings.Count;
            var parts = new List<string>();
            if (e > 0) parts.Add(e == 1 ? "1 error" : $"{e} errors");
            if (w > 0) parts.Add(w == 1 ? "1 warning" : $"{w} warnings");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The overlay shows every message (true) or only its one-line summary.</summary>
    public bool IssuesExpanded
    {
        get => _issuesExpanded;
        set => Set(ref _issuesExpanded, value);
    }

    public ICommand ToggleIssuesCommand => new RelayCommand(() => IssuesExpanded = !IssuesExpanded);

    private void SetIssues(IReadOnlyList<string> errors, IReadOnlyList<string> warnings)
    {
        bool grew = errors.Count > _inputErrors.Count || warnings.Count > _inputWarnings.Count;
        _inputErrors = errors;
        _inputWarnings = warnings;
        if (grew) IssuesExpanded = true; // a new problem is always shown in full
        RaiseIssues();
    }

    private void SetPreviewError(string? message)
    {
        if (_previewError == message) return;
        _previewError = message;
        if (message is not null) IssuesExpanded = true;
        RaiseIssues();
    }

    private void RaiseIssues()
    {
        foreach (var n in new[] { nameof(HasInputErrors), nameof(HasInputWarnings), nameof(ShowIssues), nameof(ErrorHeading), nameof(ErrorText), nameof(WarningText), nameof(IssueSummary) })
            OnPropertyChanged(n);
    }
}
