namespace CsvAnalyser.App.Plotting;

/// <summary>
/// The ranges of every axis shown, keyed by a name that survives a rebuild of the plots
/// ("X", or "graph id|axis key" for a Y axis). Used to tell whether a gesture changed the view.
/// </summary>
public sealed class ViewSnapshot(Dictionary<string, (double Min, double Max)> ranges)
{
    public IReadOnlyDictionary<string, (double Min, double Max)> Ranges { get; } = ranges;

    public bool SameAs(ViewSnapshot other) =>
        Ranges.Count == other.Ranges.Count
        && Ranges.All(kv => other.Ranges.TryGetValue(kv.Key, out var r) && r == kv.Value);
}
