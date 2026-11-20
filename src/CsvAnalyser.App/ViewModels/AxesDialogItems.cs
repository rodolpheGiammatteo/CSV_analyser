using System.Windows.Media;

namespace CsvAnalyser.App.ViewModels;

/// <summary>A Y axis as shown in the graphs window.</summary>
public sealed class AxisRowViewModel(AxisSpec spec)
{
    public string Label => string.IsNullOrEmpty(spec.Label) ? "(sans unité)" : spec.Label;

    public string Curves => string.Join(", ", spec.Curves.Select(c => c.Name));

    public string Side => spec.IsRight ? "droite" : "gauche";

    /// <summary>"à gauche — courbe A, courbe B".</summary>
    public string Description => $"à {Side} — {Curves}";

    public Brush Brush { get; } = MakeBrush(spec.Color);

    public AxisRangeSettings Settings => spec.Settings;

    private static Brush MakeBrush(ScottPlot.Color? color)
    {
        if (color is not { } c)
            return Brushes.Transparent;
        var brush = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }
}

/// <summary>A destination in the "Graphe" list of a curve: an existing graph, or a new one (Graph = null).</summary>
public sealed record GraphChoice(GraphViewModel? Graph, string Label);

/// <summary>A curve of a graph, as shown in the graphs window: its axis, and a list to send it to another graph.</summary>
public sealed class GraphCurveRow(ChannelItemViewModel item, string axis, IReadOnlyList<GraphChoice> choices, Action<ChannelItemViewModel, GraphViewModel?> move)
{
    public ChannelItemViewModel Item { get; } = item;

    public string Axis { get; } = axis;

    public IReadOnlyList<GraphChoice> Choices { get; } = choices;

    public GraphChoice? Selected
    {
        get => Choices.FirstOrDefault(c => c.Graph == Item.Graph);
        set
        {
            if (value is not null && value.Graph != Item.Graph)
                move(Item, value.Graph);
        }
    }
}

/// <summary>A graph, its curves and its Y axes, as shown in the graphs window.</summary>
public sealed class GraphAxesViewModel(
    GraphViewModel graph,
    IReadOnlyList<GraphCurveRow> curves,
    IReadOnlyList<AxisRowViewModel> axes,
    IEnumerable<ChannelItemViewModel> allCurves,
    int index,
    int count)
{
    public GraphViewModel Graph { get; } = graph;

    public IReadOnlyList<GraphCurveRow> Curves { get; } = curves;

    public IReadOnlyList<AxisRowViewModel> Axes { get; } = axes;

    /// <summary>Every curve of the file, for "Ajouter une courbe".</summary>
    public IEnumerable<ChannelItemViewModel> AllCurves { get; } = allCurves;

    public bool CanAlignZeros => Axes.Count > 1;

    public bool IsEmpty => Curves.Count == 0;

    public bool HasAxes => Axes.Count > 0;

    public bool CanMoveUp { get; } = index > 0;

    public bool CanMoveDown { get; } = index < count - 1;
}
