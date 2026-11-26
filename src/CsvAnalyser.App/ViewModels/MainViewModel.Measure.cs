using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsvAnalyser.Core.Analysis;
using CsvAnalyser.Core.Export;

namespace CsvAnalyser.App.ViewModels;

/// <summary>One line of the measurement table: a curve between the cursors A and B.</summary>
public sealed record MeasureRow(
    string Name, SolidColorBrush Brush,
    string A, string B, string Delta, string Min, string Max, string Mean, string Rms, string PeakToPeak);

/// <summary>Oscilloscope-like measurements between two vertical cursors A and B.</summary>
public partial class MainViewModel
{
    private DispatcherTimer? _measureTimer;

    /// <summary>The two measurement cursors are shown (toolbar "Mesures", Ctrl+M).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMeasurePanel))]
    private bool _isMeasuring;

    [ObservableProperty]
    private string _measureSummary = "";

    /// <summary>X position of cursor A (set by the plot controller; null = not placed yet).</summary>
    public double? MeasureA { get; private set; }

    public double? MeasureB { get; private set; }

    public ObservableCollection<MeasureRow> MeasureRows { get; } = [];

    partial void OnIsMeasuringChanged(bool value)
    {
        RaisePlotChanged(PlotChange.Measure);
        ScheduleMeasurements();
    }

    [RelayCommand]
    private void ToggleMeasure()
    {
        if (HasData)
            IsMeasuring = !IsMeasuring;
    }

    /// <summary>Called by the plot controller when a cursor is placed or dragged.</summary>
    public void SetMeasureCursors(double a, double b)
    {
        MeasureA = a;
        MeasureB = b;
        ScheduleMeasurements();
    }

    /// <summary>Another X axis or file: the cursors will be placed again in the visible range.</summary>
    private void ForgetMeasureCursors()
    {
        MeasureA = null;
        MeasureB = null;
    }

    /// <summary>Computing over millions of samples on every mouse move would lag: measurements follow at most every 60 ms.</summary>
    private void ScheduleMeasurements()
    {
        if (_measureTimer is null)
        {
            _measureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _measureTimer.Tick += (_, _) =>
            {
                _measureTimer.Stop();
                UpdateMeasurements();
            };
        }
        _measureTimer.Stop();
        _measureTimer.Start();
    }

    /// <summary>The rows between the cursors (for the measurements and the export).</summary>
    public ExportRows? RowsBetweenCursors =>
        MeasureA is { } a && MeasureB is { } b && Dataset is not null && SelectedXAxis is not null
            ? RowsInXRange(Math.Min(a, b), Math.Max(a, b))
            : null;

    private void UpdateMeasurements()
    {
        MeasureRows.Clear();
        if (!IsMeasuring || Dataset is not { } ds || SelectedXAxis is not { } option
            || MeasureA is not { } a || MeasureB is not { } b || RowsBetweenCursors is not { } rows)
        {
            MeasureSummary = IsMeasuring ? L.T("Cochez des courbes pour voir les mesures.") : "";
            return;
        }

        int count = CountRows(rows);

        string xUnit = option.Channel?.Unit ?? "";
        double delta = b - a;
        string summary = $"X1 : {Number(a)}   X2 : {Number(b)}   X2 − X1 = {Number(delta)}{(xUnit.Length > 0 ? " " + xUnit : "")}";
        bool isSeconds = option.Channel is not null && option.Channel == ds.Time?.Channel || xUnit == "s";
        if (isSeconds && Math.Abs(delta) > 0)
            summary += $"   (1/Δ = {Number(1 / Math.Abs(delta))} Hz)";
        MeasureSummary = summary + L.F("   —   {0:N0} points", count);

        // Each file on its own samples: rows between the cursors and samples nearest to X1 and X2 (NaN outside its X range).
        var perFile = new Dictionary<DataSource, (ExportRows Rows, int A, int B)>();
        foreach (var item in PlottedChannels)
        {
            var source = item.Source ?? PrimarySource!;
            if (!perFile.TryGetValue(source, out var at))
            {
                var xs = source.XValues(option);
                bool sorted = source.IsXSorted(option);
                at = perFile[source] = (source.IsPrimary ? rows : RowsInXRange(Math.Min(a, b), Math.Max(a, b), source),
                    NearestInRange(xs, sorted, a), NearestInRange(xs, sorted, b));
            }
            var values = item.Channel.Values;
            var stats = RangeStatistics.Compute(values, at.Rows.Start, at.Rows.End, at.Rows.Filter);
            double va = at.A >= 0 ? values[at.A] : double.NaN;
            double vb = at.B >= 0 ? values[at.B] : double.NaN;
            MeasureRows.Add(new MeasureRow(
                item.LegendText, item.Brush,
                Number(va), Number(vb), Number(vb - va),
                Number(stats.Min), Number(stats.Max), Number(stats.Mean), Number(stats.Rms), Number(stats.PeakToPeak)));
        }
    }

    /// <summary>The sample nearest to x, or -1 when x is outside the X range of that file.</summary>
    private static int NearestInRange(double[] xs, bool sorted, double x)
    {
        int i = NearestIndex(xs, sorted, x);
        if (i < 0 || !sorted || xs.Length == 0)
            return i;
        return x < xs[0] - 1e-12 || x > xs[^1] + 1e-12 ? -1 : i;
    }

    private static int NearestIndex(double[] xs, bool sorted, double x)
    {
        if (sorted)
            return SortedSearch.Nearest(xs, x);
        int best = -1;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < xs.Length; i++)
        {
            double d = Math.Abs(xs[i] - x);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        return best;
    }

    private static string Number(double v) => double.IsFinite(v) ? v.ToString("G6", CultureInfo.CurrentCulture) : "—";

    // ---------------------------------------------------------------- Y1 and Y2 (horizontal cursors)

    private bool _settingMeasureYAxis;

    /// <summary>Two horizontal cursors Y1 and Y2 on one Y axis of one graph (toolbar "Mesures Y", Ctrl+Shift+M).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMeasurePanel))]
    private bool _isMeasuringY;

    [ObservableProperty]
    private string _measureYSummary = "";

    /// <summary>The axis measured by Y1 and Y2, as "graphId|axisKey" (see <see cref="MeasureYAxisChoices"/>).</summary>
    [ObservableProperty]
    private string? _measureYAxisId;

    public bool ShowMeasurePanel => IsMeasuring || IsMeasuringY;

    public GraphViewModel? MeasureYGraph { get; private set; }

    public string? MeasureYAxisKey { get; private set; }

    public double? MeasureY1 { get; private set; }

    public double? MeasureY2 { get; private set; }

    /// <summary>Every Y axis of every graph: "Graphe 1 — A".</summary>
    public ObservableCollection<StyleChoice<string>> MeasureYAxisChoices { get; } = [];

    partial void OnIsMeasuringYChanged(bool value)
    {
        RefreshMeasureYChoices();
        RaisePlotChanged(PlotChange.Measure);
        UpdateMeasureYSummary();
    }

    [RelayCommand]
    private void ToggleMeasureY()
    {
        if (HasData)
            IsMeasuringY = !IsMeasuringY;
    }

    partial void OnMeasureYAxisIdChanged(string? value)
    {
        if (_settingMeasureYAxis || value is null)
            return;
        var parts = value.Split('|', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out int id) || Graphs.FirstOrDefault(g => g.Id == id) is not { } graph)
            return;
        // Another axis: the cursors are placed again in its visible range.
        MeasureYGraph = graph;
        MeasureYAxisKey = parts[1];
        MeasureY1 = MeasureY2 = null;
        RaisePlotChanged(PlotChange.Measure);
    }

    /// <summary>Called by the plot controller when Y1/Y2 are placed or dragged.</summary>
    public void SetMeasureY(GraphViewModel graph, string axisKey, double y1, double y2)
    {
        MeasureYGraph = graph;
        MeasureYAxisKey = axisKey;
        MeasureY1 = y1;
        MeasureY2 = y2;
        SelectMeasureYAxis();
        UpdateMeasureYSummary();
    }

    private void ForgetMeasureY()
    {
        MeasureYGraph = null;
        MeasureYAxisKey = null;
        MeasureY1 = MeasureY2 = null;
    }

    private void RefreshMeasureYChoices()
    {
        _settingMeasureYAxis = true;
        try
        {
            MeasureYAxisChoices.Clear();
            foreach (var spec in BuildLayout())
            {
                foreach (var axis in spec.Axes)
                {
                    string label = string.IsNullOrEmpty(axis.Label) ? L.T("(sans unité)") : axis.Label;
                    MeasureYAxisChoices.Add(new($"{spec.Graph.Id}|{axis.Key}", Graphs.Count > 1 ? $"{spec.Graph.Title} — {label}" : label));
                }
            }
        }
        finally
        {
            _settingMeasureYAxis = false;
        }
        SelectMeasureYAxis();
    }

    private void SelectMeasureYAxis()
    {
        _settingMeasureYAxis = true;
        try
        {
            MeasureYAxisId = MeasureYGraph is { } g && MeasureYAxisKey is { } k ? $"{g.Id}|{k}" : null;
            OnPropertyChanged(nameof(MeasureYAxisId)); // the combo may have lost it while its list was rebuilt
        }
        finally
        {
            _settingMeasureYAxis = false;
        }
    }

    private void UpdateMeasureYSummary()
    {
        if (!IsMeasuringY || MeasureY1 is not { } y1 || MeasureY2 is not { } y2)
        {
            MeasureYSummary = IsMeasuringY ? L.T("Cochez des courbes pour placer Y1 et Y2.") : "";
            return;
        }
        var axis = BuildLayout().FirstOrDefault(g => g.Graph == MeasureYGraph)?.Axes.FirstOrDefault(a => a.Key == MeasureYAxisKey);
        var units = axis?.Curves.Select(c => c.Unit).Where(u => u.Length > 0).Distinct().ToList() ?? [];
        string unit = units.Count == 1 ? " " + units[0] : "";
        MeasureYSummary = $"Y1 = {Number(y1)}{unit}    Y2 = {Number(y2)}{unit}    Y2 − Y1 = {Number(y2 - y1)}{unit}";
    }
}
