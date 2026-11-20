using CommunityToolkit.Mvvm.ComponentModel;
using CsvAnalyser.Core.Data;

namespace CsvAnalyser.App.ViewModels;

/// <summary>A possible X axis: a channel, or the row number when <see cref="Channel"/> is null.</summary>
public sealed class XAxisOption : ObservableObject
{
    private readonly bool _isTime;
    private double[]? _rowIndex;

    public XAxisOption(Channel? channel, bool isTime = false)
    {
        Channel = channel;
        _isTime = isTime;
    }

    /// <summary>Follows the channel's display name (call <see cref="RefreshLabel"/> after a rename).</summary>
    public string Label => Channel is null ? "N° de ligne"
        : _isTime ? $"Temps — {Channel.DisplayName}"
        : Channel.DisplayName;

    public Channel? Channel { get; }

    public bool IsSorted => Channel?.IsSortedAscending ?? true;

    public void RefreshLabel() => OnPropertyChanged(nameof(Label));

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
