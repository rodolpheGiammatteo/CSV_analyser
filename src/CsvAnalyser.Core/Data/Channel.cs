namespace CsvAnalyser.Core.Data;

public sealed class Channel
{
    private bool? _isSortedAscending;

    public Channel(string sourceName, double[] values)
    {
        SourceName = sourceName;
        DisplayName = sourceName;
        Values = values;
    }

    /// <summary>Header text as found in the file; the stable key used to map configurations.</summary>
    public string SourceName { get; }

    public string DisplayName { get; set; }

    public string Unit { get; set; } = "";

    public double[] Values { get; }

    public int Length => Values.Length;

    /// <summary>True when every value is a number and the sequence never decreases (usable as an X axis).</summary>
    public bool IsSortedAscending => _isSortedAscending ??= ComputeSortedAscending(Values);

    internal void InvalidateCache() => _isSortedAscending = null;

    private static bool ComputeSortedAscending(double[] values)
    {
        if (values.Length == 0)
            return false;

        double previous = values[0];
        if (double.IsNaN(previous))
            return false;

        for (int i = 1; i < values.Length; i++)
        {
            double v = values[i];
            if (double.IsNaN(v) || v < previous)
                return false;
            previous = v;
        }
        return true;
    }

    public override string ToString() => DisplayName;
}
