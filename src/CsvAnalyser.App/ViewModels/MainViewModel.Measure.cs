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
            MeasureSummary = IsMeasuring ? "Cochez des courbes pour voir les mesures." : "";
            return;
        }

        var xs = option.GetValues(ds);
        int ia = NearestIndex(xs, option.IsSorted, a);
        int ib = NearestIndex(xs, option.IsSorted, b);
        int count = CountRows(rows);

        string xUnit = option.Channel?.Unit ?? "";
        double delta = b - a;
        string summary = $"A : {Number(a)}   B : {Number(b)}   B − A = {Number(delta)}{(xUnit.Length > 0 ? " " + xUnit : "")}";
        bool isSeconds = option.Channel is not null && option.Channel == ds.Time?.Channel || xUnit == "s";
        if (isSeconds && Math.Abs(delta) > 0)
            summary += $"   (1/Δ = {Number(1 / Math.Abs(delta))} Hz)";
        MeasureSummary = summary + $"   —   {count:N0} points";

        foreach (var item in PlottedChannels)
        {
            var values = item.Channel.Values;
            var stats = RangeStatistics.Compute(values, rows.Start, rows.End, rows.Filter);
            double va = ia >= 0 ? values[ia] : double.NaN;
            double vb = ib >= 0 ? values[ib] : double.NaN;
            MeasureRows.Add(new MeasureRow(
                item.LegendText, item.Brush,
                Number(va), Number(vb), Number(vb - va),
                Number(stats.Min), Number(stats.Max), Number(stats.Mean), Number(stats.Rms), Number(stats.PeakToPeak)));
        }
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
}
