using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CsvAnalyser.App.ViewModels;
using ScottPlot;
using ScottPlot.Plottables;
using ScottPlot.WPF;

namespace CsvAnalyser.App.Plotting;

/// <summary>Keeps the ScottPlot control in sync with <see cref="MainViewModel"/> and drives the vertical cursor.</summary>
public sealed class PlotController
{
    private const double Margin = 0.04;

    private readonly WpfPlot _view;
    private readonly MainViewModel _vm;
    private readonly List<Series> _series = [];
    private VerticalLine? _cursorLine;
    private double[]? _xs;
    private bool _xSorted;

    // ScottPlot renders in physical pixels: sizes must follow the Windows display scaling.
    private float _scale = 1;

    // Zoom/pan history: the view before a mouse gesture (or a burst of wheel ticks) is recorded when it ends.
    private readonly ViewHistory _history = new();
    private readonly DispatcherTimer _wheelTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private AxisLimits? _gestureStart;

    private sealed record Series(ChannelItemViewModel Item, double[] Ys, Marker Marker);

    public PlotController(WpfPlot view, MainViewModel vm)
    {
        _view = view;
        _vm = vm;
        _vm.PlotChanged += OnPlotChanged;
        _view.MouseMove += OnMouseMove;
        _view.MouseLeave += (_, _) => HideCursor();
        _view.Loaded += (_, _) => UpdateScale();

        _view.PreviewMouseDown += OnPreviewMouseDown;
        _view.PreviewMouseWheel += OnPreviewMouseWheel;
        // handledEventsToo: ScottPlot handles the mouse-up itself (e.g. end of a zoom rectangle).
        _view.AddHandler(UIElement.MouseUpEvent, new MouseButtonEventHandler(OnMouseUpAfterPlot), handledEventsToo: true);
        _wheelTimer.Tick += (_, _) =>
        {
            _wheelTimer.Stop();
            CommitGesture();
        };

        ApplyTheme();
        _view.Refresh();
    }

    private void UpdateScale()
    {
        float scale = (float)VisualTreeHelper.GetDpi(_view).DpiScaleX;
        if (Math.Abs(scale - _scale) < 0.01)
            return;
        _scale = scale;
        ApplyTheme();
        Rebuild(resetView: _series.Count == 0);
    }

    private Plot Plot => _view.Plot;

    private void OnPlotChanged(object? sender, PlotChange change)
    {
        switch (change)
        {
            case PlotChange.Dataset:
            case PlotChange.XAxis:
                // Previous views are meaningless on another X axis.
                _gestureStart = null;
                _history.Clear();
                UpdateHistoryState();
                Rebuild(resetView: true);
                break;
            case PlotChange.Curves:
                Rebuild(resetView: _series.Count == 0);
                break;
            case PlotChange.Theme:
                ApplyTheme();
                Rebuild(resetView: false);
                break;
            case PlotChange.PlotFont:
                ApplyTheme();
                _view.Refresh();
                break;
            case PlotChange.AutoScale:
                var before = Plot.Axes.GetLimits();
                AutoScaleAll();
                Record(before);
                _view.Refresh();
                break;
            case PlotChange.UndoView:
                Undo();
                break;
            case PlotChange.RedoView:
                Redo();
                break;
        }
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        switch (e.ChangedButton)
        {
            case MouseButton.XButton1:
                Undo();
                e.Handled = true;
                return;
            case MouseButton.XButton2:
                Redo();
                e.Handled = true;
                return;
        }

        if (_wheelTimer.IsEnabled)
        {
            _wheelTimer.Stop();
            CommitGesture();
        }
        _gestureStart ??= Plot.Axes.GetLimits();
    }

    private void OnMouseUpAfterPlot(object sender, MouseButtonEventArgs e) =>
        _view.Dispatcher.BeginInvoke(CommitGesture, DispatcherPriority.Background);

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _gestureStart ??= Plot.Axes.GetLimits();
        _wheelTimer.Stop();
        _wheelTimer.Start();
    }

    private void CommitGesture()
    {
        if (_gestureStart is not { } start || Mouse.LeftButton == MouseButtonState.Pressed
            || Mouse.RightButton == MouseButtonState.Pressed || Mouse.MiddleButton == MouseButtonState.Pressed)
            return;
        _gestureStart = null;
        Record(start);
    }

    private void Record(AxisLimits previous)
    {
        if (ViewHistory.AreSame(previous, Plot.Axes.GetLimits()))
            return;
        _history.Push(previous);
        UpdateHistoryState();
    }

    private void Undo()
    {
        _wheelTimer.Stop();
        CommitGesture();
        if (_history.TryUndo(Plot.Axes.GetLimits(), out var target))
            ShowView(target);
    }

    private void Redo()
    {
        _wheelTimer.Stop();
        CommitGesture();
        if (_history.TryRedo(Plot.Axes.GetLimits(), out var target))
            ShowView(target);
    }

    private void ShowView(AxisLimits limits)
    {
        Plot.Axes.SetLimits(limits);
        UpdateHistoryState();
        _view.Refresh();
    }

    private void UpdateHistoryState()
    {
        _vm.CanUndoView = _history.CanUndo;
        _vm.CanRedoView = _history.CanRedo;
    }

    private void Rebuild(bool resetView)
    {
        var previous = Plot.Axes.GetLimits();
        Plot.Clear();
        _series.Clear();
        _cursorLine = null;

        _xs = _vm.Dataset is { } ds && _vm.SelectedXAxis is { } option ? option.GetValues(ds) : null;
        _xSorted = _vm.SelectedXAxis?.IsSorted ?? true;

        if (_xs is not null)
        {
            foreach (var item in _vm.PlottedChannels)
            {
                var ys = item.Channel.Values;
                if (_xSorted)
                {
                    var signal = Plot.Add.SignalXY(_xs, ys, item.Color);
                    signal.LineWidth = 1.5f * _scale;
                    signal.LegendText = item.LegendText;
                }
                else
                {
                    var scatter = Plot.Add.ScatterPoints(_xs, ys, item.Color);
                    scatter.MarkerSize = 3 * _scale;
                    scatter.LegendText = item.LegendText;
                }
            }

            // Markers and cursor are added last so they are drawn on top of the curves.
            foreach (var item in _vm.PlottedChannels)
            {
                var marker = Plot.Add.Marker(0, 0, MarkerShape.FilledCircle, 8 * _scale, item.Color);
                marker.IsVisible = false;
                marker.LegendText = "";
                _series.Add(new Series(item, item.Channel.Values, marker));
            }

            _cursorLine = Plot.Add.VerticalLine(0);
            _cursorLine.Color = ScottPlot.Color.FromHex(_vm.SelectedTheme.CursorColor).WithAlpha(0.8);
            _cursorLine.LinePattern = LinePattern.Dashed;
            _cursorLine.LineWidth = _scale;
            _cursorLine.IsVisible = false;
        }

        Plot.XLabel(_vm.XAxisLabel);
        var units = _vm.PlottedChannels.Select(c => c.Unit).Distinct().ToList();
        Plot.YLabel(units.Count == 1 ? units[0] : "");
        Plot.Legend.IsVisible = _series.Count > 0;
        Plot.Legend.Alignment = Alignment.UpperRight;

        if (resetView || _series.Count == 0)
        {
            AutoScaleAll();
        }
        else
        {
            Plot.Axes.SetLimitsX(previous.Left, previous.Right);
            AutoScaleYVisible();
        }

        _view.Refresh();
    }

    private void ApplyTheme()
    {
        var t = _vm.SelectedTheme;
        var figure = ScottPlot.Color.FromHex(t.FigureBackground);
        var axis = ScottPlot.Color.FromHex(t.AxisColor);

        Plot.FigureBackground.Color = figure;
        Plot.DataBackground.Color = ScottPlot.Color.FromHex(t.DataBackground);
        Plot.Axes.Color(axis);
        Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex(t.MajorGrid);
        Plot.Grid.MinorLineColor = ScottPlot.Color.FromHex(t.MinorGrid);
        Plot.Grid.MajorLineWidth = _scale;
        Plot.Grid.MinorLineWidth = _scale;
        Plot.Legend.BackgroundColor = figure.WithAlpha(0.85);
        Plot.Legend.FontColor = axis;
        Plot.Legend.OutlineColor = ScottPlot.Color.FromHex(t.MajorGrid);
        float font = _scale * (float)_vm.PlotFontScale;
        Plot.Legend.FontSize = 12 * font;

        foreach (var a in Plot.Axes.GetAxes())
        {
            a.TickLabelStyle.FontSize = 12 * font;
            a.Label.FontSize = 14 * font;
            a.MajorTickStyle.Length = 4 * _scale;
            a.MinorTickStyle.Length = 2 * _scale;
            a.FrameLineStyle.Width = _scale;
        }
    }

    private void AutoScaleAll()
    {
        if (_xs is null || _series.Count == 0)
        {
            Plot.Axes.SetLimits(0, 1, 0, 1);
            return;
        }

        var (xMin, xMax) = _xSorted ? FirstLastFinite(_xs) : MinMax(_xs, 0, _xs.Length);
        SetX(xMin, xMax);
        AutoScaleYVisible();
    }

    /// <summary>Fits Y on the data visible in the current X range, ignoring NaN.</summary>
    private void AutoScaleYVisible()
    {
        if (_xs is null || _series.Count == 0)
            return;

        var limits = Plot.Axes.GetLimits();
        double yMin = double.PositiveInfinity, yMax = double.NegativeInfinity;
        foreach (var s in _series)
        {
            double min, max;
            if (_xSorted)
            {
                int i0 = LowerBound(_xs, limits.Left);
                int i1 = LowerBound(_xs, limits.Right) + 1;
                (min, max) = MinMax(s.Ys, i0, Math.Min(i1, s.Ys.Length));
            }
            else
            {
                (min, max) = MinMaxInXRange(_xs, s.Ys, limits.Left, limits.Right);
            }
            yMin = Math.Min(yMin, min);
            yMax = Math.Max(yMax, max);
        }

        if (!double.IsFinite(yMin) || !double.IsFinite(yMax))
        {
            yMin = 0;
            yMax = 1;
        }
        if (yMax - yMin < 1e-12)
        {
            double pad = Math.Abs(yMin) * 0.1 + 1;
            yMin -= pad;
            yMax += pad;
        }
        double margin = (yMax - yMin) * Margin;
        Plot.Axes.SetLimitsY(yMin - margin, yMax + margin);
    }

    private void SetX(double min, double max)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            min = 0;
            max = 1;
        }
        if (max - min < 1e-12)
        {
            min -= 0.5;
            max += 0.5;
        }
        Plot.Axes.SetLimitsX(min, max);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_xs is null || _series.Count == 0 || _cursorLine is null)
            return;

        var position = e.GetPosition(_view);
        var pixel = new Pixel((float)(position.X * _view.DisplayScale), (float)(position.Y * _view.DisplayScale));
        var dataRect = Plot.RenderManager.LastRender.DataRect;
        if (pixel.X < dataRect.Left || pixel.X > dataRect.Right || pixel.Y < dataRect.Top || pixel.Y > dataRect.Bottom)
        {
            HideCursor();
            return;
        }

        double x = Plot.GetCoordinates(pixel).X;
        int index = _xSorted ? NearestSorted(_xs, x) : NearestUnsorted(_xs, x);
        if (index < 0)
            return;

        double snappedX = _xs[index];
        _cursorLine.X = snappedX;
        _cursorLine.IsVisible = true;
        _vm.CursorXText = _vm.FormatX(snappedX);

        foreach (var s in _series)
        {
            double y = s.Ys[index];
            s.Marker.Location = new Coordinates(snappedX, y);
            s.Marker.IsVisible = !double.IsNaN(y);
            s.Item.CursorText = MainViewModel.FormatValue(y, s.Item.Unit);
        }
        _view.Refresh();
    }

    private void HideCursor()
    {
        if (_cursorLine is null || !_cursorLine.IsVisible)
            return;

        _cursorLine.IsVisible = false;
        foreach (var s in _series)
        {
            s.Marker.IsVisible = false;
            s.Item.CursorText = "";
        }
        _vm.CursorXText = "Survolez le graphe";
        _view.Refresh();
    }

    private static int LowerBound(double[] sorted, double value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (sorted[mid] < value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static int NearestSorted(double[] sorted, double value)
    {
        if (sorted.Length == 0)
            return -1;
        int i = LowerBound(sorted, value);
        if (i >= sorted.Length)
            return sorted.Length - 1;
        if (i > 0 && value - sorted[i - 1] < sorted[i] - value)
            return i - 1;
        return i;
    }

    private static int NearestUnsorted(double[] xs, double value)
    {
        int best = -1;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < xs.Length; i++)
        {
            double d = Math.Abs(xs[i] - value);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        return best;
    }

    private static (double Min, double Max) FirstLastFinite(double[] sorted)
    {
        double first = double.NaN, last = double.NaN;
        foreach (var v in sorted)
        {
            if (double.IsFinite(v)) { first = v; break; }
        }
        for (int i = sorted.Length - 1; i >= 0; i--)
        {
            if (double.IsFinite(sorted[i])) { last = sorted[i]; break; }
        }
        return (first, last);
    }

    private static (double Min, double Max) MinMax(double[] values, int start, int end)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (int i = Math.Max(0, start); i < end; i++)
        {
            double v = values[i];
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return (min, max);
    }

    private static (double Min, double Max) MinMaxInXRange(double[] xs, double[] ys, double left, double right)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (int i = 0; i < xs.Length; i++)
        {
            if (xs[i] < left || xs[i] > right)
                continue;
            double v = ys[i];
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return (min, max);
    }
}
