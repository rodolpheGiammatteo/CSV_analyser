using System.IO;
using System.Windows.Threading;
using CsvAnalyser.Core.Configuration;

namespace CsvAnalyser.App.ViewModels;

/// <summary>
/// Undo/redo of every user action (Ctrl+Z, Ctrl+Y, mouse back/forward buttons) and the "unsaved changes" state.
/// A state is the whole configuration (graphs, curves, axes, measurements, zoom); it is committed shortly after
/// each change (typing is grouped), and undoing applies the previous one. When only the zoom differs, only the
/// zoom is restored (fast).
/// </summary>
public partial class MainViewModel
{
    private const int MaxUndo = 100;

    private readonly List<ViewConfiguration> _undoStates = [];
    private readonly List<ViewConfiguration> _redoStates = [];
    private ViewConfiguration? _state;
    private string? _stateJson;
    private string? _savedSignature;
    private DispatcherTimer? _commitTimer;
    private int _applyingState;

    /// <summary>Something changed since the project was opened or saved.</summary>
    public bool IsDirty => Dataset is not null && _savedSignature is not null && Signature(CaptureConfiguration()) != _savedSignature;

    /// <summary>A change happened: the new state is committed once things settle (typing is grouped).</summary>
    private void ScheduleCommit()
    {
        if (_applyingState > 0 || Dataset is null || _state is null)
            return;
        if (_commitTimer is null)
        {
            _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _commitTimer.Tick += (_, _) => CommitNow();
        }
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    /// <summary>Records the current state as a new step (called by the plot after a zoom, and before undo/redo).</summary>
    public void CommitNow()
    {
        _commitTimer?.Stop();
        if (_applyingState > 0 || Dataset is null)
            return;
        var now = CaptureConfiguration();
        string json = now.ToJson();
        if (_state is null)
        {
            SetState(now, json);
            return;
        }
        if (json == _stateJson)
            return;
        _undoStates.Add(_state);
        if (_undoStates.Count > MaxUndo)
            _undoStates.RemoveAt(0);
        _redoStates.Clear();
        SetState(now, json);
        UpdateUndoState();
    }

    public void Undo() => Step(_undoStates, _redoStates);

    public void Redo() => Step(_redoStates, _undoStates);

    private void Step(List<ViewConfiguration> from, List<ViewConfiguration> to)
    {
        CommitNow();
        if (from.Count == 0 || _state is null)
            return;
        var target = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(_state);
        ApplyState(target);
        UpdateUndoState();
    }

    private void ApplyState(ViewConfiguration target)
    {
        _applyingState++;
        try
        {
            if (_state is not null && WithoutView(target) == WithoutView(_state))
                RestoreViewOnly(target);
            else
                ApplyConfiguration(target, restoreView: true);
        }
        finally
        {
            _applyingState--;
        }
        _commitTimer?.Stop();
        // The state actually shown (a restored view may differ by rounding from the saved one).
        var now = CaptureConfiguration();
        SetState(now, now.ToJson());
    }

    private void RestoreViewOnly(ViewConfiguration target)
    {
        if (PlotView is not { } view)
            return;
        var y = new List<(GraphViewModel, string, double, double)>();
        for (int i = 0; i < target.Graphs.Count && i < Graphs.Count; i++)
        {
            foreach (var axis in target.Graphs[i].Axes)
            {
                if (axis.View is { IsValid: true } r)
                    y.Add((Graphs[i], axis.Key, r.Min, r.Max));
            }
        }
        view.SetView(target.XRange is { IsValid: true } x ? (x.Min, x.Max) : null, y);
    }

    private void SetState(ViewConfiguration state, string json)
    {
        _state = state;
        _stateJson = json;
        UpdateTitle();
    }

    /// <summary>A new file or project: no history, and nothing to save yet.</summary>
    private void ResetHistory()
    {
        _commitTimer?.Stop();
        _undoStates.Clear();
        _redoStates.Clear();
        var now = CaptureConfiguration();
        _savedSignature = Signature(now);
        SetState(now, now.ToJson());
        UpdateUndoState();
    }

    private void MarkSaved()
    {
        _savedSignature = Signature(CaptureConfiguration());
        UpdateTitle();
    }

    private void UpdateUndoState()
    {
        CanUndoView = _undoStates.Count > 0;
        CanRedoView = _redoStates.Count > 0;
    }

    /// <summary>"projet — fichier.csv • — CSV Analyser" (the dot when there are unsaved changes).</summary>
    private void UpdateTitle()
    {
        if (Dataset is not { } ds)
            return;
        string name = Path.GetFileName(ds.FilePath);
        if (ProjectPath is { } project)
            name = $"{Path.GetFileNameWithoutExtension(project)} — {name}";
        bool dirty = _savedSignature is not null && _state is not null && Signature(_state) != _savedSignature;
        Title = $"{name}{(dirty ? " •" : "")} — {AppInfo.FullTitle}";
    }

    /// <summary>The state without the zoom: what "unsaved changes" and the fast view-only undo compare.</summary>
    private static string WithoutView(ViewConfiguration config)
    {
        var copy = ViewConfiguration.FromJson(config.ToJson());
        copy.XRange = null;
        foreach (var g in copy.Graphs)
        {
            foreach (var a in g.Axes)
                a.View = null;
        }
        return copy.ToJson();
    }

    /// <summary>For "unsaved changes": the state without zoom nor cursor positions.</summary>
    private static string Signature(ViewConfiguration config)
    {
        var copy = ViewConfiguration.FromJson(WithoutView(config));
        if (copy.Measure is { } m)
        {
            m.A = m.B = m.Y1 = m.Y2 = null;
        }
        return copy.ToJson();
    }
}
