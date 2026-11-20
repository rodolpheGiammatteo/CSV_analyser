namespace CsvAnalyser.App.Plotting;

/// <summary>
/// The ranges of every axis shown, keyed by a name that survives a rebuild of the plots
/// ("X", or "graph id|axis key" for a Y axis).
/// </summary>
public sealed class ViewSnapshot(Dictionary<string, (double Min, double Max)> ranges)
{
    public IReadOnlyDictionary<string, (double Min, double Max)> Ranges { get; } = ranges;

    public bool SameAs(ViewSnapshot other) =>
        Ranges.Count == other.Ranges.Count
        && Ranges.All(kv => other.Ranges.TryGetValue(kv.Key, out var r) && r == kv.Value);
}

/// <summary>Back/forward history of the plot views, like a web browser.</summary>
public sealed class ViewHistory
{
    private const int MaxDepth = 100;

    private readonly List<ViewSnapshot> _back = [];
    private readonly List<ViewSnapshot> _forward = [];

    public bool CanUndo => _back.Count > 0;

    public bool CanRedo => _forward.Count > 0;

    /// <summary>Records the view that was shown before a change made by the user.</summary>
    public void Push(ViewSnapshot previous)
    {
        _back.Add(previous);
        if (_back.Count > MaxDepth)
            _back.RemoveAt(0);
        _forward.Clear();
    }

    public bool TryUndo(ViewSnapshot current, out ViewSnapshot target) => Move(_back, _forward, current, out target);

    public bool TryRedo(ViewSnapshot current, out ViewSnapshot target) => Move(_forward, _back, current, out target);

    public void Clear()
    {
        _back.Clear();
        _forward.Clear();
    }

    private static bool Move(List<ViewSnapshot> from, List<ViewSnapshot> to, ViewSnapshot current, out ViewSnapshot target)
    {
        if (from.Count == 0)
        {
            target = current;
            return false;
        }
        target = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(current);
        return true;
    }
}
