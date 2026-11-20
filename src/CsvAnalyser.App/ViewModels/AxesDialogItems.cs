using System.Windows.Media;

namespace CsvAnalyser.App.ViewModels;

/// <summary>A Y axis as shown in the graphs window.</summary>
public sealed class AxisRowViewModel(AxisSpec spec)
{
    public string Label => string.IsNullOrEmpty(spec.Label) ? L.T("(sans unité)") : spec.Label;

    /// <summary>The title the axis gets when none is typed.</summary>
    public string AutoLabel => GraphLayout.AxisLabel(spec.Curves) is { Length: > 0 } label ? label : L.T("(sans unité)");

    public string Curves => string.Join(", ", spec.Curves.Select(c => c.Name));

    public string Side => spec.IsRight ? L.T("droite") : L.T("gauche");

    /// <summary>"à gauche — courbe A, courbe B".</summary>
    public string Description => L.F("à {0} — {1}", Side, Curves);

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

/// <summary>
/// A curve of a graph, as shown in the graphs window: order, color, axis, graph (bound straight to the curve),
/// plus the full settings behind the gear button.
/// </summary>
public sealed class GraphCurveRow(
    ChannelItemViewModel item,
    IReadOnlyList<StyleChoice<string>> axisChoices,
    IReadOnlyList<GraphChoice> choices,
    Action<ChannelItemViewModel, GraphViewModel?> move,
    bool canMoveUp,
    bool canMoveDown)
{
    public ChannelItemViewModel Item { get; } = item;

    /// <summary>Automatic, own axis, or an axis shared with other curves of the graph.</summary>
    public IReadOnlyList<StyleChoice<string>> AxisChoices { get; } = axisChoices;

    public IReadOnlyList<GraphChoice> Choices { get; } = choices;

    public bool CanMoveUp { get; } = canMoveUp;

    public bool CanMoveDown { get; } = canMoveDown;

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

    /// <summary>The per-axis alignment value is shown only when the graph aligns its axes.</summary>
    public bool ShowAlignValues => Graph.AlignZeros && Axes.Count > 1;
}
