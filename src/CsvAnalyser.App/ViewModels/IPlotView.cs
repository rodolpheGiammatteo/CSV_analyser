namespace CsvAnalyser.App.ViewModels;

/// <summary>What the view models may read from or set on the plot (implemented by the plot controller).</summary>
public interface IPlotView
{
    /// <summary>The visible X range, or null when nothing is plotted.</summary>
    (double Min, double Max)? XRange { get; }

    /// <summary>The visible range of a Y axis, or null when the graph has no such axis.</summary>
    (double Min, double Max)? AxisRange(GraphViewModel graph, string axisKey);

    /// <summary>Shows a saved view: X range (null = unchanged) and Y ranges of some axes.</summary>
    void SetView((double Min, double Max)? x, IReadOnlyList<(GraphViewModel Graph, string AxisKey, double Min, double Max)> y);
}
