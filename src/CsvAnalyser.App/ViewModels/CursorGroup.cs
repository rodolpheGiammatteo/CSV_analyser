namespace CsvAnalyser.App.ViewModels;

public enum CursorPanelPlacement
{
    Left,
    Right,
    Bottom,
}

/// <summary>The curves of one graph in the cursor card (the title is shown only when there are several graphs).</summary>
public sealed record CursorGroup(GraphViewModel Graph, IReadOnlyList<ChannelItemViewModel> Curves, bool ShowTitle);
