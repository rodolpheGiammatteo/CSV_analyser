namespace CsvAnalyser.App.ViewModels;

/// <summary>One Y axis of a graph and the curves drawn against it.</summary>
public sealed record AxisSpec(string Key, string Label, IReadOnlyList<ChannelItemViewModel> Curves, AxisRangeSettings Settings)
{
    /// <summary>Left for the 1st, 3rd… axis, right for the 2nd, 4th…</summary>
    public bool IsRight { get; init; }

    /// <summary>Axis color: the curve color when the axis has a single curve.</summary>
    public ScottPlot.Color? Color => Curves.Count == 1 ? Curves[0].Color : null;
}

public sealed record GraphSpec(GraphViewModel Graph, IReadOnlyList<AxisSpec> Axes)
{
    public IEnumerable<ChannelItemViewModel> Curves => Axes.SelectMany(a => a.Curves);
}

/// <summary>Groups the plotted curves into graphs and Y axes.</summary>
public static class GraphLayout
{
    /// <summary>
    /// Curves of a graph share an axis when they have the same unit, unless they ask for their own axis
    /// or for the axis of other curves (whatever the units).
    /// </summary>
    public static string AxisKey(ChannelItemViewModel item) => item.AxisChoice switch
    {
        ChannelItemViewModel.AutoAxis => UnitKey(item),
        ChannelItemViewModel.OwnAxisChoice => $"curve:{item.Channel.SourceName}",
        var key => key,
    };

    public static string UnitKey(ChannelItemViewModel item) => $"unit:{item.Unit}";

    /// <summary>The axis choices offered for a curve in its settings: automatic, own axis, or one of the other axes of its graph.</summary>
    public static IReadOnlyList<StyleChoice<string>> AxisChoices(ChannelItemViewModel item, IReadOnlyList<GraphSpec> layout)
    {
        string unit = string.IsNullOrEmpty(item.Unit) ? "sans unité" : item.Unit;
        var choices = new List<StyleChoice<string>>
        {
            new(ChannelItemViewModel.AutoAxis, $"Automatique (axe « {unit} »)"),
            new(ChannelItemViewModel.OwnAxisChoice, "Axe séparé"),
        };
        var graph = layout.FirstOrDefault(g => g.Graph == item.Graph);
        string ownKey = $"curve:{item.Channel.SourceName}";
        foreach (var axis in graph?.Axes ?? [])
        {
            if (axis.Key == UnitKey(item) || axis.Key == ownKey)
                continue;
            var others = axis.Curves.Where(c => c != item).ToList();
            if (others.Count == 0)
                continue;
            choices.Add(new(axis.Key, $"Partager l'axe « {AxisLabel(others)} »"));
        }
        // Keep the current choice selectable even if its axis has no other curve any more.
        if (choices.All(c => c.Value != item.AxisChoice))
            choices.Add(new(item.AxisChoice, $"Axe partagé ({item.AxisChoice.Split(':').Last()})"));
        return choices;
    }

    public static IReadOnlyList<GraphSpec> Build(IEnumerable<GraphViewModel> graphs, IEnumerable<ChannelItemViewModel> plotted)
    {
        var plottedList = plotted.ToList();
        var result = new List<GraphSpec>();
        foreach (var graph in graphs)
        {
            var axes = plottedList
                .Where(c => c.Graph == graph)
                .GroupBy(AxisKey)
                .Select((group, index) =>
                {
                    var curves = group.ToList();
                    return new AxisSpec(group.Key, AxisLabel(curves), curves, graph.GetAxisSettings(group.Key))
                    {
                        IsRight = index % 2 == 1,
                    };
                })
                .ToList();
            result.Add(new GraphSpec(graph, axes));
        }
        return result;
    }

    /// <summary>A string that changes whenever a curve changes graph or axis (used to detect layout changes).</summary>
    public static string Signature(IReadOnlyList<GraphSpec> specs) =>
        string.Join(";", specs.Select(g => $"{g.Graph.Id}:" + string.Join(",", g.Axes.Select(a =>
            a.Key + "=" + string.Join("+", a.Curves.Select(c => c.Channel.SourceName))))));

    /// <summary>The curve name for a single curve, else its unit(s): "rpm", or "rpm · °C" for an axis shared across units.</summary>
    public static string AxisLabel(IReadOnlyList<ChannelItemViewModel> curves)
    {
        if (curves.Count == 1)
            return curves[0].LegendText;
        return string.Join(" · ", curves.Select(c => c.Unit).Where(u => !string.IsNullOrEmpty(u)).Distinct());
    }
}
