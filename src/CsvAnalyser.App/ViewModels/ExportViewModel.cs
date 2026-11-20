using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CsvAnalyser.Core.Export;

namespace CsvAnalyser.App.ViewModels;

public enum ExportRangeKind
{
    All,
    Visible,
    BetweenCursors,
}

/// <summary>Choices of the CSV export window.</summary>
public sealed partial class ExportViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private ExportRangeKind _range = ExportRangeKind.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private bool _plottedOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private string _delimiter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private string _decimal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private bool _includeDate;

    public ExportViewModel(MainViewModel main)
    {
        _main = main;
        var ranges = new List<StyleChoice<ExportRangeKind>> { new(ExportRangeKind.All, "Toutes les données") };
        if (main.PlotView?.XRange is { } x)
        {
            ranges.Add(new(ExportRangeKind.Visible,
                L.F("Plage visible ({0} → {1})", MainViewModel.FormatValue(x.Min, ""), MainViewModel.FormatValue(x.Max, ""))));
        }
        if (main.IsMeasuring && main.MeasureA is { } a && main.MeasureB is { } b)
        {
            ranges.Add(new(ExportRangeKind.BetweenCursors,
                L.F("Entre les curseurs A et B ({0} → {1})", MainViewModel.FormatValue(Math.Min(a, b), ""), MainViewModel.FormatValue(Math.Max(a, b), ""))));
            _range = ExportRangeKind.BetweenCursors;
        }
        RangeChoices = ranges;

        int plotted = main.PlottedChannels.Count;
        ColumnChoices =
        [
            new(true, L.F("Courbes affichées ({0})", plotted)),
            new(false, L.F("Toutes les colonnes ({0})", main.Channels.Count)),
        ];
        _plottedOnly = plotted > 0;
        _delimiter = DelimiterChoices.Any(c => c.Value == main.ExportDelimiter) ? main.ExportDelimiter : ";";
        _decimal = DecimalChoices.Any(c => c.Value == main.ExportDecimal) ? main.ExportDecimal : ",";
        CanIncludeDate = main.Dataset?.Time?.Origin is not null;
        _includeDate = CanIncludeDate;
    }

    public IReadOnlyList<StyleChoice<ExportRangeKind>> RangeChoices { get; }

    public IReadOnlyList<StyleChoice<bool>> ColumnChoices { get; }

    public static IReadOnlyList<StyleChoice<string>> DelimiterChoices { get; } =
    [
        new(";", "Point-virgule  ;"),
        new(",", "Virgule  ,"),
        new("\t", "Tabulation"),
    ];

    public static IReadOnlyList<StyleChoice<string>> DecimalChoices { get; } =
    [
        new(",", "Virgule  (1,5)"),
        new(".", "Point  (1.5)"),
    ];

    public bool CanIncludeDate { get; }

    public string? Error => Delimiter == Decimal ? L.T("Le séparateur de colonnes et la décimale doivent être différents.") : null;

    public bool CanExport => Error is null;

    public string Summary
    {
        get
        {
            int columns = Columns.Count + (IncludeDate && CanIncludeDate ? 1 : 0);
            return L.F("{0:N0} lignes × {1} colonnes (la colonne X en premier)", MainViewModel.CountRows(Rows), columns);
        }
    }

    public string SuggestedFileName =>
        Path.GetFileNameWithoutExtension(_main.Dataset?.FilePath ?? "export") + " - export.csv";

    public ExportRows Rows => Range switch
    {
        ExportRangeKind.Visible when _main.PlotView?.XRange is { } x => _main.RowsInXRange(x.Min, x.Max),
        ExportRangeKind.BetweenCursors when _main.RowsBetweenCursors is { } rows => rows,
        _ => ExportRows.All(_main.Dataset?.RowCount ?? 0),
    };

    public IReadOnlyList<ExportColumn> Columns => _main.ExportColumnsFor(PlottedOnly);

    public CsvExportOptions Options => new()
    {
        Delimiter = Delimiter[0],
        DecimalSeparator = Decimal[0],
        DateColumn = IncludeDate && _main.Dataset?.Time is { Origin: { } origin } time
            ? new ExportDateColumn(L.T("Date et heure"), origin, time.Channel.Values)
            : null,
    };

    /// <summary>Remembers the separators for the next export.</summary>
    public void RememberChoices()
    {
        _main.ExportDelimiter = Delimiter;
        _main.ExportDecimal = Decimal;
    }
}
