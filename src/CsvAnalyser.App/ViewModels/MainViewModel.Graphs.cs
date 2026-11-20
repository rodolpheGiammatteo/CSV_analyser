using CommunityToolkit.Mvvm.ComponentModel;

namespace CsvAnalyser.App.ViewModels;

/// <summary>Graph organisation from the "Graphes et axes" window: add, reorder, remove, columns.</summary>
public partial class MainViewModel
{
    /// <summary>Number of columns of graphs (graphs fill the first column top to bottom, then the next one).</summary>
    [ObservableProperty]
    private int _graphColumns = 1;

    public static IReadOnlyList<StyleChoice<int>> GraphColumnChoices { get; } =
    [
        new(1, "1 colonne"),
        new(2, "2 colonnes"),
        new(3, "3 colonnes"),
        new(4, "4 colonnes"),
    ];

    partial void OnGraphColumnsChanged(int value) => RaisePlotChanged(PlotChange.Layout);

    /// <summary>(column, row, rows in that column) of graph number <paramref name="index"/> among <paramref name="count"/>.</summary>
    public static (int Column, int Row, int RowsInColumn) GraphCell(int index, int count, int columns)
    {
        columns = Math.Clamp(columns, 1, Math.Max(1, count));
        int perColumn = (int)Math.Ceiling(count / (double)columns);
        int column = index / perColumn;
        int first = column * perColumn;
        int rows = Math.Min(perColumn, count - first);
        return (column, index - first, rows);
    }

    /// <summary>While the window is open, empty graphs are kept (the user is filling them).</summary>
    public void BeginGraphEditing()
    {
        foreach (var g in Graphs)
            g.KeepWhenEmpty = true;
    }

    public void EndGraphEditing()
    {
        foreach (var g in Graphs)
            g.KeepWhenEmpty = false;
        int before = Graphs.Count;
        RemoveEmptyGraphs();
        if (Graphs.Count != before)
            RaisePlotChanged(PlotChange.Curves);
    }

    /// <summary>Adds an empty graph at the bottom (kept until the window closes).</summary>
    public GraphViewModel AddEmptyGraph()
    {
        var graph = NewGraph();
        graph.KeepWhenEmpty = true;
        Graphs.Add(graph);
        RenumberGraphs();
        RaisePlotChanged(PlotChange.Curves);
        return graph;
    }

    public void MoveGraph(GraphViewModel graph, int delta)
    {
        int from = Graphs.IndexOf(graph);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= Graphs.Count)
            return;
        Graphs.Move(from, to);
        RenumberGraphs();
        RaisePlotChanged(PlotChange.Curves);
    }

    /// <summary>Removes a graph; its curves are hidden. The last graph is only emptied.</summary>
    public void RemoveGraph(GraphViewModel graph)
    {
        _suppressPlotEvents = true;
        try
        {
            foreach (var item in PlottedChannels.Where(c => c.Graph == graph).ToList())
                item.IsPlotted = false;
            if (Graphs.Count > 1)
                Graphs.Remove(graph);
            RenumberGraphs();
        }
        finally
        {
            _suppressPlotEvents = false;
        }
        RaisePlotChanged(PlotChange.Curves);
    }

    /// <summary>
    /// Swaps a curve with the previous (delta = -1) or next (+1) curve of its graph in the plotted order:
    /// the last one is drawn on top of the others.
    /// </summary>
    public void MoveCurveInGraph(ChannelItemViewModel item, int delta)
    {
        var inGraph = PlottedChannels.Where(c => c.Graph == item.Graph).ToList();
        int index = inGraph.IndexOf(item);
        int other = index + delta;
        if (index < 0 || other < 0 || other >= inGraph.Count)
            return;
        int a = PlottedChannels.IndexOf(item), b = PlottedChannels.IndexOf(inGraph[other]);
        PlottedChannels.Move(a, b);
        if (Math.Abs(a - b) > 1)
            PlottedChannels.Move(a < b ? b - 1 : b + 1, a); // put the other one where this one was
        RaisePlotChanged(PlotChange.Curves);
    }

    private void RenumberGraphs()
    {
        for (int i = 0; i < Graphs.Count; i++)
            Graphs[i].Number = i + 1;
    }
}
