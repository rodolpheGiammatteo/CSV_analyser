using ScottPlot;

namespace CsvAnalyser.App.Plotting;

/// <summary>Back/forward history of the plot axis limits, like a web browser.</summary>
public sealed class ViewHistory
{
    private const int MaxDepth = 100;

    private readonly List<AxisLimits> _back = [];
    private readonly List<AxisLimits> _forward = [];

    public bool CanUndo => _back.Count > 0;

    public bool CanRedo => _forward.Count > 0;

    /// <summary>Records the view that was shown before a change made by the user.</summary>
    public void Push(AxisLimits previous)
    {
        _back.Add(previous);
        if (_back.Count > MaxDepth)
            _back.RemoveAt(0);
        _forward.Clear();
    }

    public bool TryUndo(AxisLimits current, out AxisLimits target) => Move(_back, _forward, current, out target);

    public bool TryRedo(AxisLimits current, out AxisLimits target) => Move(_forward, _back, current, out target);

    public void Clear()
    {
        _back.Clear();
        _forward.Clear();
    }

    public static bool AreSame(AxisLimits a, AxisLimits b) =>
        a.Left == b.Left && a.Right == b.Right && a.Bottom == b.Bottom && a.Top == b.Top;

    private static bool Move(List<AxisLimits> from, List<AxisLimits> to, AxisLimits current, out AxisLimits target)
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
