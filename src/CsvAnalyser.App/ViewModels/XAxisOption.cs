using CsvAnalyser.Core.Data;

namespace CsvAnalyser.App.ViewModels;

/// <summary>A possible X axis: a channel, or the row number when <see cref="Channel"/> is null.</summary>
public sealed class XAxisOption
{
    private double[]? _rowIndex;

    public XAxisOption(string label, Channel? channel)
    {
        Label = label;
        Channel = channel;
    }

    public string Label { get; }

    public Channel? Channel { get; }

    public bool IsSorted => Channel?.IsSortedAscending ?? true;

    public double[] GetValues(Dataset dataset)
    {
        if (Channel is not null)
            return Channel.Values;

        if (_rowIndex is null || _rowIndex.Length != dataset.RowCount)
        {
            _rowIndex = new double[dataset.RowCount];
            for (int i = 0; i < _rowIndex.Length; i++)
                _rowIndex[i] = i;
        }
        return _rowIndex;
    }

    public override string ToString() => Label;
}
