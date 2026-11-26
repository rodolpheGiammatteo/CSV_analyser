using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CsvAnalyser.Core.Data;

namespace CsvAnalyser.App.ViewModels;

/// <summary>
/// One loaded CSV file. The first one (<see cref="IsPrimary"/>) is the file that was opened; the others were added to
/// compare with it. Each file keeps its own X (its own time column, its own rows) shifted by <see cref="Offset"/>.
/// With a single file nothing of this shows: no group in the curve list, no file name after the curves.
/// </summary>
public sealed partial class DataSource : ObservableObject
{
    private readonly Action<DataSource> _offsetChanged;
    private (double[] Raw, double Offset, double[] Shifted)? _shifted;
    private double[]? _rows;

    public DataSource(Dataset dataset, int index, Action<DataSource> offsetChanged)
    {
        Dataset = dataset;
        Index = index;
        _offsetChanged = offsetChanged;
    }

    public Dataset Dataset { get; }

    /// <summary>0 for the opened file, then 1, 2… in the order they were added.</summary>
    public int Index { get; set; }

    public bool IsPrimary => Index == 0;

    public string FilePath => Dataset.FilePath;

    public string Name => Path.GetFileNameWithoutExtension(Dataset.FilePath);

    /// <summary>Several files are loaded: the curves show their file name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Suffix))]
    private bool _showName;

    /// <summary>" — essai 2" after the curve names when several files are loaded, else nothing.</summary>
    public string Suffix => ShowName ? " — " + Name : "";

    /// <summary>Added to every X value of this file (in the unit of the X axis), to line it up with the others.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffsetText))]
    private double _offset;

    partial void OnOffsetChanged(double value) => _offsetChanged(this);

    /// <summary>Restores an offset (project, session, undo) without asking for a redraw.</summary>
    public void SetOffsetSilently(double value)
    {
#pragma warning disable MVVMTK0034
        _offset = value;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(Offset));
        OnPropertyChanged(nameof(OffsetText));
    }

    /// <summary>The offset in the box of the file header ("+12,5"); a value that cannot be read is ignored.</summary>
    public string OffsetText
    {
        get => Offset == 0 ? "0" : Offset.ToString("+0.######;-0.######", CultureInfo.CurrentCulture);
        set
        {
            string text = (value ?? "").Trim().Replace(" ", "");
            if (text.Length == 0)
                Offset = 0;
            else if (double.TryParse(text.Replace('.', ',').Replace(',', CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator[0]),
                         NumberStyles.Float, CultureInfo.CurrentCulture, out double v) && double.IsFinite(v))
                Offset = v;
            OnPropertyChanged(); // the box shows the value kept
        }
    }

    /// <summary>
    /// The X values of this file for the chosen X axis, offset included: the opened file uses the option itself;
    /// another file uses its own time column for the time, its own rows for the row number, and its column of the
    /// same name for a column (else its time, else its rows).
    /// </summary>
    public double[] XValues(XAxisOption option)
    {
        double[] raw = RawX(option);
        if (Offset == 0)
            return raw;
        if (_shifted is { } s && ReferenceEquals(s.Raw, raw) && s.Offset == Offset)
            return s.Shifted;
        var shifted = new double[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            shifted[i] = raw[i] + Offset;
        _shifted = (raw, Offset, shifted);
        return shifted;
    }

    /// <summary>True when this file's X for that option is ascending (an offset does not change it).</summary>
    public bool IsXSorted(XAxisOption option) => SourceChannel(option)?.IsSortedAscending ?? true;

    /// <summary>This file has the X column chosen (else its curves are drawn on its time, or its rows).</summary>
    public bool HasXColumn(XAxisOption option) =>
        IsPrimary || option.Channel is null || option.IsTime || FindColumn(option.Channel.SourceName) is not null;

    private double[] RawX(XAxisOption option)
    {
        if (IsPrimary)
            return option.GetValues(Dataset);
        return SourceChannel(option)?.Values ?? Rows();
    }

    private Channel? SourceChannel(XAxisOption option)
    {
        if (IsPrimary)
            return option.Channel;
        if (option.Channel is null)
            return null;
        if (!option.IsTime && !option.IsComputed && FindColumn(option.Channel.SourceName) is { } column)
            return column;
        return Dataset.Time?.Channel;
    }

    public Channel? FindColumn(string sourceName) =>
        Dataset.Channels.FirstOrDefault(c => c.SourceName == sourceName);

    private double[] Rows()
    {
        if (_rows is null || _rows.Length != Dataset.RowCount)
        {
            _rows = new double[Dataset.RowCount];
            for (int i = 0; i < _rows.Length; i++)
                _rows[i] = i;
        }
        return _rows;
    }
}
