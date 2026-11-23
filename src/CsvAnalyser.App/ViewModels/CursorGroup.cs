namespace CsvAnalyser.App.ViewModels;

public enum CursorPanelPlacement
{
    Left,
    Right,
    Bottom,
}

/// <summary>Where the X axis (ticks, and its title) is drawn when graphs are stacked.</summary>
public enum XAxisPlacement
{
    /// <summary>Under the bottom graph of each column only.</summary>
    Bottom,
    /// <summary>Ticks under every graph, the title under the bottom one only.</summary>
    AllGraphs,
    /// <summary>Ticks and title under every graph.</summary>
    AllGraphsWithTitle,
}

/// <summary>The curves of one graph in the cursor card (the title is shown only when there are several graphs).</summary>
public sealed record CursorGroup(GraphViewModel Graph, IReadOnlyList<ChannelItemViewModel> Curves, bool ShowTitle);
