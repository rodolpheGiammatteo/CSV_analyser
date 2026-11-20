using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CsvAnalyser.App.ViewModels;
using CsvAnalyser.Core.Analysis;
using ScottPlot;
using ScottPlot.Interactivity.UserActionResponses;
using ScottPlot.Plottables;
using ScottPlot.WPF;

namespace CsvAnalyser.App.Plotting;

/// <summary>
/// Keeps the ScottPlot control in sync with <see cref="MainViewModel"/>: one subplot per stacked graph
/// (shared X axis), one Y axis per unit (or per curve that asks for its own), the vertical cursor, the
/// NaN-aware autoscale and the zoom history. This is the only class that touches ScottPlot.
/// </summary>
public sealed class PlotController : IPlotView
{
    private const double Margin = 0.04;
    private const string XKey = "X";

    private readonly WpfPlot _view;
    private readonly MainViewModel _vm;
    private readonly List<GraphRender> _graphs = [];
    private readonly List<Series> _series = [];
    private double[]? _xs;
    private bool _xSorted;
    private string _layoutSignature = "";
    private Dictionary<string, string> _axisMembers = [];

    // ScottPlot renders in physical pixels: sizes must follow the Windows display scaling.
    private float _scale = 1;

    // Zoom/pan history: the view before a mouse gesture (or a burst of wheel ticks) is recorded when it ends.
    private readonly ViewHistory _history = new();
    private readonly DispatcherTimer _wheelTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private ViewSnapshot? _gestureStart;
    private Point _middleDownPosition;

    private readonly PaddingMatcher _paddingMatcher;

    /// <param name="Curve">The SignalXY (sorted X) or Scatter (unsorted X) drawing the data.</param>
    /// <param name="Marker">The dot that follows the vertical cursor.</param>
    /// <param name="Label">The value written next to the marker (option "valeurs dans les graphes").</param>
    private sealed record Series(ChannelItemViewModel Item, double[] Ys, IPlottable Curve, Marker Marker, Text Label, GraphRender Graph, IYAxis Axis);

    private sealed class AxisRender(string key, AxisSpec spec, IYAxis axis)
    {
        public string Key { get; } = key;
        public AxisSpec Spec { get; set; } = spec;
        public IYAxis Axis { get; } = axis;
    }

    private sealed class GraphRender(GraphViewModel graph, Plot plot)
    {
        public GraphViewModel Graph { get; } = graph;
        public Plot Plot { get; } = plot;
        public List<AxisRender> Axes { get; } = [];
        public VerticalLine? Cursor { get; set; }
        public VerticalLine? LineA { get; set; }
        public VerticalLine? LineB { get; set; }
    }

    public PlotController(WpfPlot view, MainViewModel vm)
    {
        _view = view;
        _vm = vm;
        _paddingMatcher = new PaddingMatcher(this);
        _vm.PlotChanged += OnPlotChanged;
        _vm.CurrentAxisRange = GetCurrentAxisRange;
        _vm.PlotView = this;
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

        // Middle click = our NaN-aware autoscale (handled in OnMouseUpAfterPlot); no benchmark overlay.
        _view.UserInputProcessor.UserActionResponses.RemoveAll(r => r is SingleClickAutoscale);
        _view.UserInputProcessor.DoubleLeftClickBenchmark(false);
        SetupContextMenu();

        Rebuild(resetView: true);
    }

    private void SetupContextMenu()
    {
        if (_view.Menu is not WpfPlotMenu menu)
            return;
        menu.Clear();
        menu.Add("Ajuster ce graphe en Y", plot =>
        {
            if (_graphs.FirstOrDefault(g => g.Plot == plot) is { } graph)
            {
                var before = Capture();
                FitGraphY(graph);
                Record(before);
                _view.Refresh();
            }
        });
        menu.Add("Ajuster X", _ => AutoScaleWithHistory(x: true, y: false));
        menu.Add("Tout ajuster (X et Y)", _ => AutoScaleWithHistory(x: true, y: true));
        menu.AddSeparator();
        menu.Add("Placer le curseur A ici", plot => PlaceMeasureCursorAtMenu(plot, 'A'));
        menu.Add("Placer le curseur B ici", plot => PlaceMeasureCursorAtMenu(plot, 'B'));
        menu.AddSeparator();
        menu.Add("Spectre (FFT) de ce graphe…", plot => _vm.ShowSpectrumCommand.Execute(GraphOf(plot)));
        menu.Add("Titre, légende et axes…", plot => _vm.EditAxesCommand.Execute(GraphOf(plot)));
        menu.AddSeparator();
        menu.Add("Copier l'image", WpfPlotMenu.CopyImageToClipboard);
        menu.Add("Enregistrer l'image…", menu.OpenSaveImageDialog);
    }

    private GraphViewModel? GraphOf(Plot plot) => _graphs.FirstOrDefault(g => g.Plot == plot)?.Graph;

    private void UpdateScale()
    {
        float scale = (float)VisualTreeHelper.GetDpi(_view).DpiScaleX;
        if (Math.Abs(scale - _scale) < 0.01)
            return;
        _scale = scale;
        Rebuild(resetView: _series.Count == 0);
    }

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
            case PlotChange.PlotFont:
                Rebuild(resetView: false);
                break;
            case PlotChange.Appearance:
                UpdateAppearance();
                break;
            case PlotChange.AxisSettings:
                var beforeSettings = Capture();
                foreach (var g in _graphs)
                    FitGraphY(g);
                Record(beforeSettings);
                _view.Refresh();
                break;
            case PlotChange.AutoScale:
                AutoScaleWithHistory(x: true, y: true);
                break;
            case PlotChange.AutoScaleX:
                AutoScaleWithHistory(x: true, y: false);
                break;
            case PlotChange.AutoScaleY:
                AutoScaleWithHistory(x: false, y: true);
                break;
            case PlotChange.GraphAppearance:
                foreach (var g in _graphs)
                    ApplyGraphAppearance(g);
                _view.Refresh();
                break;
            case PlotChange.UndoView:
                Undo();
                break;
            case PlotChange.RedoView:
                Redo();
                break;
            case PlotChange.Measure:
                UpdateMeasureLines();
                _view.Refresh();
                break;
        }
    }

    // ---------------------------------------------------------------- building

    /// <summary>Recreates every subplot, axis and plottable. Keeps X and the Y ranges of unchanged axes unless asked.</summary>
    private void Rebuild(bool resetView)
    {
        var previous = _graphs.Count > 0 ? Capture() : null;
        var previousMembers = _axisMembers;

        var specs = _vm.BuildLayout();
        if (specs.Count == 0)
            specs = [new GraphSpec(new GraphViewModel(0, () => { }, () => { }), [])];
        _layoutSignature = GraphLayout.Signature(specs);
        // Members = curves and their data arrays: an axis whose computed curve was recomputed is fitted again.
        _axisMembers = specs.SelectMany(g => g.Axes.Select(a => (Key: AxisId(g.Graph, a.Key), Members: string.Join("+", a.Curves.Select(c =>
                c.Channel.SourceName + "#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c.Channel.Values))))))
            .ToDictionary(x => x.Key, x => x.Members);

        _graphs.Clear();
        _series.Clear();
        _lastYRanges.Clear();
        _interactionAxis = null;
        _xs =_vm.Dataset is { } ds && _vm.SelectedXAxis is { } option ? option.GetValues(ds) : null;
        _xSorted = _vm.SelectedXAxis?.IsSorted ?? true;

        for (int gi = 0; gi < specs.Count; gi++)
        {
            var spec = specs[gi];
            var render = new GraphRender(spec.Graph, new Plot());
            _graphs.Add(render);
            BuildGraph(render, spec, isBottom: gi == specs.Count - 1, isTop: gi == 0);
        }

        var plots = _graphs.Select(g => g.Plot).ToArray();
        _view.Reset(plots[0]);
        _view.Multiplot.Reset(plots[0]);
        for (int i = 1; i < plots.Length; i++)
            _view.Multiplot.AddPlot(plots[i]);
        _view.Multiplot.Layout = new ScottPlot.MultiplotLayouts.Rows();
        if (plots.Length > 1)
            _view.Multiplot.SharedAxes.ShareX(plots);
        if (!_view.Multiplot.PreRenderActions.Contains(_paddingMatcher))
            _view.Multiplot.PreRenderActions.Add(_paddingMatcher);

        // X: keep the previous range unless asked to reset.
        if (!resetView && previous is not null && previous.Ranges.TryGetValue(XKey, out var x) && _series.Count > 0)
            SetX(x.Min, x.Max);
        else
            SetFullX();

        // Y: keep the range of axes whose curves did not change, fit the graphs where something changed.
        foreach (var g in _graphs)
        {
            bool fit = resetView || previous is null;
            foreach (var a in g.Axes)
            {
                string id = AxisId(g.Graph, a.Key);
                if (!fit && previous!.Ranges.TryGetValue(id, out var range)
                    && previousMembers.TryGetValue(id, out var members) && members == _axisMembers[id])
                    g.Plot.Axes.SetLimitsY(range.Min, range.Max, a.Axis);
                else
                    fit = true;
            }
            if (fit)
                FitGraphY(g);
        }

        UpdateMeasureLines();
        _view.Refresh();
    }

    private void BuildGraph(GraphRender render, GraphSpec spec, bool isBottom, bool isTop)
    {
        var plot = render.Plot;
        ApplyTheme(plot);

        if (_xs is not null)
        {
            var created = new List<(Series Series, IYAxis Axis)>();
            for (int ai = 0; ai < spec.Axes.Count; ai++)
            {
                var axisSpec = spec.Axes[ai];
                IYAxis axis = ai switch
                {
                    0 => plot.Axes.Left,
                    1 => plot.Axes.Right,
                    _ => axisSpec.IsRight ? plot.Axes.AddRightAxis() : plot.Axes.AddLeftAxis(),
                };
                StyleAxis(axis);
                var axisRender = new AxisRender(axisSpec.Key, axisSpec, axis);
                render.Axes.Add(axisRender);
                ApplyAxisLabel(plot, axisRender);

                foreach (var item in axisSpec.Curves)
                {
                    IPlottable curve = _xSorted
                        ? plot.Add.SignalXY(_xs, item.Channel.Values, item.Color)
                        : plot.Add.ScatterPoints(_xs, item.Channel.Values, item.Color);
                    curve.Axes.YAxis = axis;
                    created.Add((new Series(item, item.Channel.Values, curve, null!, null!, render, axis), axis));
                }
            }

            // Markers, value labels and cursor are added last so they are drawn on top of the curves.
            foreach (var (pending, axis) in created)
            {
                var marker = plot.Add.Marker(0, 0, MarkerShape.FilledCircle, 8 * _scale, pending.Item.Color);
                marker.Axes.YAxis = axis;
                marker.IsVisible = false;
                marker.LegendText = "";

                var label = plot.Add.Text("", 0, 0);
                label.Axes.YAxis = axis;
                label.IsVisible = false;
                label.LabelStyle.FontSize = 11 * FontScale;
                label.LabelStyle.ForeColor = ScottPlot.Color.FromHex(_vm.SelectedTheme.AxisColor);
                label.LabelStyle.BackgroundColor = ScottPlot.Color.FromHex(_vm.SelectedTheme.DataBackground).WithAlpha(0.92);
                label.LabelStyle.BorderWidth = 1.5f * _scale;
                label.LabelStyle.BorderRadius = 4 * _scale;
                label.LabelStyle.Padding = 3 * _scale;

                var series = pending with { Marker = marker, Label = label };
                _series.Add(series);
                ApplyAppearance(series);
            }

            var cursor = plot.Add.VerticalLine(0);
            cursor.Color = ScottPlot.Color.FromHex(_vm.SelectedTheme.CursorColor).WithAlpha(0.8);
            cursor.LinePattern = LinePattern.Dashed;
            cursor.LineWidth = _scale;
            cursor.IsVisible = false;
            render.Cursor = cursor;
            render.LineA = AddMeasureLine(plot, "A", MeasureColorA, labelled: isTop);
            render.LineB = AddMeasureLine(plot, "B", MeasureColorB, labelled: isTop);
        }

        // Only the bottom graph shows the X axis; the others share it.
        plot.Axes.Bottom.Label.Text = isBottom ? _vm.XAxisLabel : "";
        plot.Axes.Bottom.TickLabelStyle.IsVisible = isBottom;
        ApplyGraphAppearance(render);
    }

    /// <summary>Title and legend placement of a graph.</summary>
    private void ApplyGraphAppearance(GraphRender g)
    {
        var plot = g.Plot;
        string title = g.Graph.Name.Trim();
        plot.Axes.Title.Label.Text = title;
        plot.Axes.Title.IsVisible = title.Length > 0;
        plot.Axes.Title.Label.FontSize = 15 * FontScale;
        plot.Axes.Title.Label.ForeColor = ScottPlot.Color.FromHex(_vm.SelectedTheme.AxisColor);

        var corner = g.Graph.LegendCorner;
        plot.Legend.IsVisible = g.Axes.Count > 0 && corner != LegendCorner.Hidden;
        plot.Legend.Alignment = corner switch
        {
            LegendCorner.UpperLeft => Alignment.UpperLeft,
            LegendCorner.LowerRight => Alignment.LowerRight,
            LegendCorner.LowerLeft => Alignment.LowerLeft,
            _ => Alignment.UpperRight,
        };
        if (g.Graph.LegendOffset is { } offset)
        {
            float dx = (float)(offset.X * _view.DisplayScale), dy = (float)(offset.Y * _view.DisplayScale);
            bool right = plot.Legend.Alignment is Alignment.UpperRight or Alignment.LowerRight;
            bool upper = plot.Legend.Alignment is Alignment.UpperRight or Alignment.UpperLeft;
            plot.Legend.Margin = new PixelPadding(right ? 0 : dx, right ? dx : 0, upper ? 0 : dy, upper ? dy : 0);
        }
        else
        {
            plot.Legend.Margin = new PixelPadding(DefaultLegendMargin * _scale);
        }
    }

    private const float DefaultLegendMargin = 8;

    // ---------------------------------------------------------------- legend dragging

    private (GraphRender Graph, Pixel Start, PixelRect Rect)? _legendDrag;

    /// <summary>Where the legend of a graph was drawn (pixels), assuming ScottPlot places it in the data area inset by its margin.</summary>
    private static PixelRect? LegendRect(GraphRender g)
    {
        var legend = g.Plot.Legend;
        var size = legend.LastRenderSize;
        if (!legend.IsVisible || size.Width <= 0 || size.Height <= 0)
            return null;
        var data = g.Plot.LastRender.DataRect;
        var m = legend.Margin;
        bool right = legend.Alignment is Alignment.UpperRight or Alignment.LowerRight;
        bool upper = legend.Alignment is Alignment.UpperRight or Alignment.UpperLeft;
        float left = right ? data.Right - m.Right - size.Width : data.Left + m.Left;
        float top = upper ? data.Top + m.Top : data.Bottom - m.Bottom - size.Height;
        return new PixelRect(left, left + size.Width, top + size.Height, top);
    }

    private GraphRender? LegendAt(Pixel pixel) => _graphs.FirstOrDefault(g =>
        LegendRect(g) is { } r && pixel.X >= r.Left && pixel.X <= r.Right && pixel.Y >= r.Top && pixel.Y <= r.Bottom);

    private Pixel ToPixel(MouseEventArgs e)
    {
        var p = e.GetPosition(_view);
        return new Pixel((float)(p.X * _view.DisplayScale), (float)(p.Y * _view.DisplayScale));
    }

    private bool TryStartLegendDrag(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return false;
        var pixel = ToPixel(e);
        if (LegendAt(pixel) is not { } g || LegendRect(g) is not { } rect)
            return false;
        _legendDrag = (g, pixel, rect);
        _view.CaptureMouse();
        e.Handled = true; // No pan while moving the legend.
        return true;
    }

    /// <summary>Moves the legend with the mouse; it is anchored to the nearest corner of the data area.</summary>
    private void DragLegend(MouseEventArgs e)
    {
        var (g, start, rect) = _legendDrag!.Value;
        var pixel = ToPixel(e);
        var data = g.Plot.LastRender.DataRect;
        float width = rect.Width, height = rect.Height;
        float left = Math.Clamp(rect.Left + pixel.X - start.X, data.Left, Math.Max(data.Left, data.Right - width));
        float top = Math.Clamp(rect.Top + pixel.Y - start.Y, data.Top, Math.Max(data.Top, data.Bottom - height));

        bool right = left + width / 2 > (data.Left + data.Right) / 2;
        bool upper = top + height / 2 < (data.Top + data.Bottom) / 2;
        float dx = right ? data.Right - (left + width) : left - data.Left;
        float dy = upper ? top - data.Top : data.Bottom - (top + height);

        // Stored in device-independent units so it survives a rebuild or a DPI change.
        g.Graph.LegendCorner = (upper, right) switch
        {
            (true, true) => LegendCorner.UpperRight,
            (true, false) => LegendCorner.UpperLeft,
            (false, true) => LegendCorner.LowerRight,
            _ => LegendCorner.LowerLeft,
        };
        g.Graph.LegendOffset = (dx / _view.DisplayScale, dy / _view.DisplayScale);
        ApplyGraphAppearance(g);
        _view.Refresh();
    }

    private void EndLegendDrag()
    {
        _legendDrag = null;
        _view.ReleaseMouseCapture();
    }

    private static string AxisId(GraphViewModel graph, string axisKey) => $"{graph.Id}|{axisKey}";

    /// <summary>Name/unit/color/style changed: in place when the graph/axis layout is the same, else a rebuild.</summary>
    private void UpdateAppearance()
    {
        var specs = _vm.BuildLayout();
        if (GraphLayout.Signature(specs) != _layoutSignature || specs.Count != _graphs.Count)
        {
            Rebuild(resetView: false);
            return;
        }

        foreach (var s in _series)
            ApplyAppearance(s);
        for (int gi = 0; gi < _graphs.Count; gi++)
        {
            var g = _graphs[gi];
            foreach (var a in g.Axes)
            {
                if (specs[gi].Axes.FirstOrDefault(x => x.Key == a.Key) is { } spec)
                    a.Spec = spec;
                ApplyAxisLabel(g.Plot, a);
            }
            if (gi == _graphs.Count - 1)
                g.Plot.Axes.Bottom.Label.Text = _vm.XAxisLabel;
        }
        _view.Refresh();
    }

    // ---------------------------------------------------------------- styling

    private float FontScale => _scale * (float)_vm.PlotFontScale;

    private void ApplyTheme(Plot plot)
    {
        var t = _vm.SelectedTheme;
        var figure = ScottPlot.Color.FromHex(t.FigureBackground);
        var axis = ScottPlot.Color.FromHex(t.AxisColor);

        plot.FigureBackground.Color = figure;
        plot.DataBackground.Color = ScottPlot.Color.FromHex(t.DataBackground);
        plot.Axes.Color(axis);
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex(t.MajorGrid);
        plot.Grid.MinorLineColor = ScottPlot.Color.FromHex(t.MinorGrid);
        plot.Grid.MajorLineWidth = _scale;
        plot.Grid.MinorLineWidth = _scale;
        plot.Legend.BackgroundColor = figure.WithAlpha(0.85);
        plot.Legend.FontColor = axis;
        plot.Legend.OutlineColor = ScottPlot.Color.FromHex(t.MajorGrid);
        plot.Legend.FontSize = 12 * FontScale;

        foreach (var a in plot.Axes.GetAxes())
            StyleAxis(a);
    }

    private void StyleAxis(IAxis a)
    {
        a.TickLabelStyle.FontSize = 12 * FontScale;
        a.Label.FontSize = 14 * FontScale;
        a.MajorTickStyle.Length = 4 * _scale;
        a.MinorTickStyle.Length = 2 * _scale;
        a.FrameLineStyle.Width = _scale;
    }

    /// <summary>Axis title, and the curve color for an axis carrying a single curve (so it is clear which axis is whose).</summary>
    private void ApplyAxisLabel(Plot plot, AxisRender a)
    {
        a.Axis.Label.Text = a.Spec.Label;
        var color = a.Spec.Color ?? ScottPlot.Color.FromHex(_vm.SelectedTheme.AxisColor);
        plot.Axes.Color(a.Axis, color);
    }

    /// <summary>Copies the curve settings (color, line, markers, legend) onto its plottables.</summary>
    private void ApplyAppearance(Series s)
    {
        var item = s.Item;
        s.Marker.Color = item.Color;
        s.Label.LabelStyle.BorderColor = item.Color;

        switch (s.Curve)
        {
            case SignalXY signal:
                signal.Color = item.Color;
                signal.LegendText = item.LegendText;
                signal.LineWidth = item.IsLineVisible ? (float)item.LineWidth * _scale : 0;
                signal.LinePattern = ToPattern(item.Line);
                signal.MarkerShape = ToShape(item.Marker);
                signal.MarkerSize = item.HasMarkers ? (float)item.MarkerSize * _scale : 0;
                break;

            case Scatter scatter:
                // Unsorted X: points only, a line would zigzag across the plot.
                scatter.Color = item.Color;
                scatter.LegendText = item.LegendText;
                scatter.LineWidth = 0;
                scatter.MarkerShape = item.HasMarkers ? ToShape(item.Marker) : MarkerShape.FilledCircle;
                scatter.MarkerSize = (float)(item.HasMarkers ? item.MarkerSize : 3) * _scale;
                break;
        }
    }

    private static LinePattern ToPattern(CurveLine line) => line switch
    {
        CurveLine.Dashed => LinePattern.Dashed,
        CurveLine.DenselyDashed => LinePattern.DenselyDashed,
        CurveLine.Dotted => LinePattern.Dotted,
        _ => LinePattern.Solid,
    };

    private static MarkerShape ToShape(CurveMarker marker) => marker switch
    {
        CurveMarker.Circle => MarkerShape.FilledCircle,
        CurveMarker.Square => MarkerShape.FilledSquare,
        CurveMarker.Triangle => MarkerShape.FilledTriangleUp,
        CurveMarker.Diamond => MarkerShape.FilledDiamond,
        CurveMarker.Cross => MarkerShape.Cross,
        _ => MarkerShape.None,
    };

    // ---------------------------------------------------------------- scaling

    /// <summary>Fits X (whole data), Y (all graphs, in the visible X range) or both.</summary>
    private void AutoScaleWithHistory(bool x, bool y)
    {
        var before = Capture();
        if (x)
            SetFullX();
        if (y)
        {
            foreach (var g in _graphs)
                FitGraphY(g);
        }
        Record(before);
        _view.Refresh();
    }

    private void SetFullX()
    {
        if (_xs is null || _series.Count == 0)
        {
            SetX(0, 1);
            return;
        }
        var (xMin, xMax) = _xSorted ? FirstLastFinite(_xs) : MinMax(_xs, 0, _xs.Length);
        SetX(xMin, xMax);
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
        foreach (var g in _graphs)
            g.Plot.Axes.SetLimitsX(min, max);
    }

    /// <summary>
    /// Y range of every axis of a graph: manual axes take their bounds, auto axes fit the data visible in the
    /// current X range (NaN ignored), then zeros are aligned if the graph asks for it.
    /// </summary>
    private void FitGraphY(GraphRender g)
    {
        if (g.Axes.Count == 0)
        {
            g.Plot.Axes.SetLimitsY(0, 1);
            return;
        }

        var xRange = g.Plot.Axes.Bottom.Range;
        var ranges = new (double Min, double Max)[g.Axes.Count];
        var isFixed = new bool[g.Axes.Count];
        for (int i = 0; i < g.Axes.Count; i++)
        {
            var a = g.Axes[i];
            if (a.Spec.Settings.ManualRange is { } manual)
            {
                ranges[i] = manual;
                isFixed[i] = true;
            }
            else
            {
                ranges[i] = DataRangeY(a, xRange.Min, xRange.Max);
            }
        }

        if (g.Graph.AlignZeros && g.Axes.Count > 1)
            ranges = ZeroAlignment.Align(ranges, isFixed);

        for (int i = 0; i < g.Axes.Count; i++)
            g.Plot.Axes.SetLimitsY(ranges[i].Min, ranges[i].Max, g.Axes[i].Axis);
    }

    /// <summary>Range of the curves of one axis inside [left, right], with margins.</summary>
    private (double Min, double Max) DataRangeY(AxisRender a, double left, double right)
    {
        double yMin = double.PositiveInfinity, yMax = double.NegativeInfinity;
        if (_xs is not null)
        {
            foreach (var item in a.Spec.Curves)
            {
                var ys = item.Channel.Values;
                double min, max;
                if (_xSorted)
                {
                    int i0 = LowerBound(_xs, left);
                    int i1 = LowerBound(_xs, right) + 1;
                    (min, max) = MinMax(ys, i0, Math.Min(i1, ys.Length));
                }
                else
                {
                    (min, max) = MinMaxInXRange(_xs, ys, left, right);
                }
                yMin = Math.Min(yMin, min);
                yMax = Math.Max(yMax, max);
            }
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
        return (yMin - margin, yMax + margin);
    }

    private (double Min, double Max)? GetCurrentAxisRange(GraphViewModel graph, string axisKey)
    {
        var g = _graphs.FirstOrDefault(x => x.Graph == graph);
        var a = g?.Axes.FirstOrDefault(x => x.Key == axisKey);
        return a is null ? null : (a.Axis.Range.Min, a.Axis.Range.Max);
    }

    // ---------------------------------------------------------------- IPlotView

    public (double Min, double Max)? XRange
    {
        get
        {
            if (_series.Count == 0 || _graphs.Count == 0)
                return null;
            var x = _graphs[0].Plot.Axes.Bottom.Range;
            return (x.Min, x.Max);
        }
    }

    public (double Min, double Max)? AxisRange(GraphViewModel graph, string axisKey) => GetCurrentAxisRange(graph, axisKey);

    public void SetView((double Min, double Max)? x, IReadOnlyList<(GraphViewModel Graph, string AxisKey, double Min, double Max)> y)
    {
        if (x is { } xr)
        {
            SetX(xr.Min, xr.Max);
            // Auto axes without a saved range follow the restored X range.
            foreach (var g in _graphs)
                FitGraphY(g);
        }
        foreach (var (graph, key, min, max) in y)
        {
            var g = _graphs.FirstOrDefault(r => r.Graph == graph);
            if (g?.Axes.FirstOrDefault(a => a.Key == key) is { } a)
                g.Plot.Axes.SetLimitsY(min, max, a.Axis);
        }
        _lastYRanges.Clear();
        _view.Refresh();
    }

    // ---------------------------------------------------------------- stacked layout

    /// <summary>
    /// Before each render, gives every subplot the same left and right padding (the widest needed),
    /// so the data areas line up and the X axis is the same for all graphs.
    /// </summary>
    private sealed class PaddingMatcher(PlotController owner) : IMultiplotPreRenderAction
    {
        public void Invoke()
        {
            owner.KeepZerosAligned();
            owner.MatchPaddings();
        }
    }

    // Y ranges seen at the previous render, to find which axis changed when no axis was grabbed.
    private readonly Dictionary<IYAxis, (double Min, double Max)> _lastYRanges = [];

    // The Y axis under the mouse when a drag or a wheel burst started (null = the data area).
    private IYAxis? _interactionAxis;

    private void RememberInteractionAxis(MouseEventArgs e)
    {
        var pixel = ToPixel(e);
        _interactionAxis = _graphs.Select(g => g.Plot.GetAxis(pixel)).OfType<IYAxis>().FirstOrDefault();
    }

    /// <summary>
    /// "Align zeros" must hold while the user drags or zooms one Y axis with the mouse (ScottPlot moves only that
    /// axis): the axis that changed leads, the others shift so their zero is at the same height, keeping their scale.
    /// </summary>
    private void KeepZerosAligned()
    {
        foreach (var g in _graphs)
        {
            if (!g.Graph.AlignZeros || g.Axes.Count < 2)
                continue;

            var axes = g.Axes.Select(a => a.Axis).ToList();
            static double ZeroFraction(IYAxis axis) => -axis.Range.Min / (axis.Range.Max - axis.Range.Min);

            double reference = ZeroFraction(axes[0]);
            bool aligned = axes.All(a => Math.Abs(ZeroFraction(a) - reference) < 1e-6);
            if (!aligned)
            {
                // The axis grabbed with the mouse leads. (ScottPlot puts the other axes back to their state at
                // mouse-down on every move, so "the axis that changed" would pick the wrong one.)
                var leader = _interactionAxis is { } grabbed && axes.Contains(grabbed)
                    ? grabbed
                    : axes.FirstOrDefault(a => !_lastYRanges.TryGetValue(a, out var last)
                        || last.Min != a.Range.Min || last.Max != a.Range.Max) ?? axes[0];
                double f = ZeroFraction(leader);
                if (double.IsFinite(f))
                {
                    foreach (var axis in axes.Where(a => a != leader))
                    {
                        double span = axis.Range.Max - axis.Range.Min;
                        g.Plot.Axes.SetLimitsY(-f * span, (1 - f) * span, axis);
                    }
                }
            }
            foreach (var axis in axes)
                _lastYRanges[axis] = (axis.Range.Min, axis.Range.Max);
        }
    }

    private void MatchPaddings()
    {
        var plots = _view.Multiplot.GetPlots();
        if (plots.Length < 2)
        {
            foreach (var p in plots)
                p.Layout.Default();
            return;
        }

        float width = (float)(_view.ActualWidth * _view.DisplayScale);
        float height = (float)(_view.ActualHeight * _view.DisplayScale);
        if (width < 10 || height < 10)
            return;

        var rects = _view.Multiplot.Layout.GetSubplotRectangles(_view.Multiplot.Subplots, new PixelRect(0, width, height, 0));
        var engine = new ScottPlot.LayoutEngines.Automatic();
        using var paint = Paint.NewDisposablePaint();
        var layouts = new Layout[plots.Length];
        float left = 0, right = 0;
        for (int i = 0; i < plots.Length; i++)
        {
            layouts[i] = engine.GetLayout(rects[i], plots[i], paint);
            left = Math.Max(left, layouts[i].DataRect.Left - rects[i].Left);
            right = Math.Max(right, rects[i].Right - layouts[i].DataRect.Right);
        }
        for (int i = 0; i < plots.Length; i++)
        {
            float bottom = rects[i].Bottom - layouts[i].DataRect.Bottom;
            float top = layouts[i].DataRect.Top - rects[i].Top;
            plots[i].Layout.Fixed(new PixelPadding(left, right, bottom, top));
        }
    }

    /// <summary>Index of the graph under a point of the control (device-independent units), or null.</summary>
    public int? GraphIndexAt(Point position)
    {
        for (int i = 0; i < _graphs.Count; i++)
        {
            if (GraphBounds(i) is { } r && r.Contains(position))
                return i;
        }
        return null;
    }

    /// <summary>Area of a graph in the control, in device-independent units.</summary>
    public Rect? GraphBounds(int index)
    {
        if (index < 0 || index >= _graphs.Count)
            return null;
        if (_view.Multiplot.LastRender.GetLastRenderRectangle(_graphs[index].Plot) is not { } r)
            return null;
        double s = _view.DisplayScale;
        return new Rect(r.Left / s, r.Top / s, r.Width / s, r.Height / s);
    }

    // ---------------------------------------------------------------- history

    private ViewSnapshot Capture()
    {
        var ranges = new Dictionary<string, (double, double)>();
        if (_graphs.Count > 0)
        {
            var x = _graphs[0].Plot.Axes.Bottom.Range;
            ranges[XKey] = (x.Min, x.Max);
        }
        foreach (var g in _graphs)
        {
            foreach (var a in g.Axes)
                ranges[AxisId(g.Graph, a.Key)] = (a.Axis.Range.Min, a.Axis.Range.Max);
        }
        return new ViewSnapshot(ranges);
    }

    private void Restore(ViewSnapshot snapshot)
    {
        if (snapshot.Ranges.TryGetValue(XKey, out var x))
            SetX(x.Min, x.Max);
        foreach (var g in _graphs)
        {
            foreach (var a in g.Axes)
            {
                if (snapshot.Ranges.TryGetValue(AxisId(g.Graph, a.Key), out var r))
                    g.Plot.Axes.SetLimitsY(r.Min, r.Max, a.Axis);
            }
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
            case MouseButton.Middle:
                _middleDownPosition = e.GetPosition(_view);
                break;
            case MouseButton.Right:
                _lastMenuPixel = ToPixel(e);
                break;
        }

        if (TryStartLegendDrag(e) || TryStartMeasureDrag(e))
            return;

        RememberInteractionAxis(e);
        if (_wheelTimer.IsEnabled)
        {
            _wheelTimer.Stop();
            CommitGesture();
        }
        _gestureStart ??= Capture();
    }

    private void OnMouseUpAfterPlot(object sender, MouseButtonEventArgs e)
    {
        if (_legendDrag is not null)
        {
            EndLegendDrag();
            return;
        }
        if (_measureDrag is not null)
        {
            EndMeasureDrag();
            return;
        }
        if (e.ChangedButton == MouseButton.Middle && (e.GetPosition(_view) - _middleDownPosition).Length < 4)
        {
            // Middle click without dragging: fit everything (a middle drag is ScottPlot's zoom rectangle).
            _view.Dispatcher.BeginInvoke(() =>
            {
                var start = _gestureStart;
                _gestureStart = null;
                SetFullX();
                foreach (var g in _graphs)
                    FitGraphY(g);
                if (start is not null)
                    Record(start);
                _view.Refresh();
            }, DispatcherPriority.Background);
            return;
        }
        _view.Dispatcher.BeginInvoke(CommitGesture, DispatcherPriority.Background);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_wheelTimer.IsEnabled)
            RememberInteractionAxis(e);
        _gestureStart ??= Capture();
        _wheelTimer.Stop();
        _wheelTimer.Start();
    }

    private void CommitGesture()
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed
            || Mouse.MiddleButton == MouseButtonState.Pressed)
            return;
        // Runs after the last render of the gesture: the grabbed axis no longer leads the zero alignment.
        _interactionAxis = null;
        if (_gestureStart is not { } start)
            return;
        _gestureStart = null;
        Record(start);
    }

    private void Record(ViewSnapshot previous)
    {
        if (previous.SameAs(Capture()))
            return;
        _history.Push(previous);
        UpdateHistoryState();
    }

    private void Undo()
    {
        _wheelTimer.Stop();
        CommitGesture();
        if (_history.TryUndo(Capture(), out var target))
            ShowView(target);
    }

    private void Redo()
    {
        _wheelTimer.Stop();
        CommitGesture();
        if (_history.TryRedo(Capture(), out var target))
            ShowView(target);
    }

    private void ShowView(ViewSnapshot snapshot)
    {
        Restore(snapshot);
        UpdateHistoryState();
        _view.Refresh();
    }

    private void UpdateHistoryState()
    {
        _vm.CanUndoView = _history.CanUndo;
        _vm.CanRedoView = _history.CanRedo;
    }

    // ---------------------------------------------------------------- measurement cursors A and B

    private static readonly ScottPlot.Color MeasureColorA = ScottPlot.Color.FromHex("#D6336C");
    private static readonly ScottPlot.Color MeasureColorB = ScottPlot.Color.FromHex("#0C8599");

    private char? _measureDrag;
    private Pixel _lastMenuPixel;

    private VerticalLine AddMeasureLine(Plot plot, string name, ScottPlot.Color color, bool labelled)
    {
        var line = plot.Add.VerticalLine(0);
        line.Color = color;
        line.LineWidth = 1.5f * _scale;
        line.IsVisible = false;
        if (labelled)
        {
            // The name at the top of the upper graph, so it is clear which line is which.
            line.LabelText = name;
            line.LabelOppositeAxis = true;
            line.LabelFontSize = 12 * FontScale;
            line.LabelBackgroundColor = color;
            line.LabelFontColor = ScottPlot.Colors.White;
        }
        return line;
    }

    /// <summary>Shows or hides the lines A and B; places them in the visible range when they have no position yet.</summary>
    private void UpdateMeasureLines()
    {
        bool on = _vm.IsMeasuring && _xs is not null && _series.Count > 0;
        if (on && (_vm.MeasureA is null || _vm.MeasureB is null))
        {
            var x = _graphs[0].Plot.Axes.Bottom.Range;
            double a = Snap(x.Min + (x.Max - x.Min) / 3), b = Snap(x.Min + 2 * (x.Max - x.Min) / 3);
            _vm.SetMeasureCursors(a, b);
        }
        // Room above the upper graph for the "A" and "B" labels.
        if (_graphs.Count > 0)
            _graphs[0].Plot.Axes.Top.MinimumSize = on ? 22 * FontScale : 0;
        foreach (var g in _graphs)
        {
            if (g.LineA is { } la && g.LineB is { } lb)
            {
                la.IsVisible = lb.IsVisible = on;
                if (on)
                {
                    la.X = _vm.MeasureA!.Value;
                    lb.X = _vm.MeasureB!.Value;
                }
            }
        }
    }

    /// <summary>The X of the sample nearest to <paramref name="x"/>.</summary>
    private double Snap(double x)
    {
        if (_xs is null || _xs.Length == 0)
            return x;
        int i = _xSorted ? NearestSorted(_xs, x) : NearestUnsorted(_xs, x);
        return i >= 0 ? _xs[i] : x;
    }

    /// <summary>"A" or "B" when the pixel is on (or within a few pixels of) that line in a graph.</summary>
    private char? MeasureLineAt(Pixel pixel)
    {
        if (!_vm.IsMeasuring || _vm.MeasureA is not { } a || _vm.MeasureB is not { } b)
            return null;
        float tolerance = 6 * _scale;
        foreach (var g in _graphs)
        {
            var r = g.Plot.LastRender.DataRect;
            // The labels of the upper graph are just above its data area.
            if (pixel.Y < r.Top - 24 * _scale || pixel.Y > r.Bottom)
                continue;
            float pa = g.Plot.GetPixel(new Coordinates(a, 0), g.Plot.Axes.Bottom, g.Plot.Axes.Left).X;
            float pb = g.Plot.GetPixel(new Coordinates(b, 0), g.Plot.Axes.Bottom, g.Plot.Axes.Left).X;
            float da = Math.Abs(pixel.X - pa), db = Math.Abs(pixel.X - pb);
            if (Math.Min(da, db) <= tolerance)
                return da <= db ? 'A' : 'B';
        }
        return null;
    }

    private bool TryStartMeasureDrag(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || MeasureLineAt(ToPixel(e)) is not { } which)
            return false;
        _measureDrag = which;
        _view.CaptureMouse();
        e.Handled = true; // no pan while moving a cursor
        return true;
    }

    private void DragMeasure(MouseEventArgs e)
    {
        var plot = _graphs[0].Plot;
        double x = Snap(plot.GetCoordinates(ToPixel(e), plot.Axes.Bottom, plot.Axes.Left).X);
        if (_measureDrag == 'A')
            _vm.SetMeasureCursors(x, _vm.MeasureB ?? x);
        else
            _vm.SetMeasureCursors(_vm.MeasureA ?? x, x);
        UpdateMeasureLines();
        _view.Refresh();
    }

    private void EndMeasureDrag()
    {
        _measureDrag = null;
        _view.ReleaseMouseCapture();
    }

    /// <summary>Context menu: puts cursor A or B where the menu was opened (and turns the measurements on).</summary>
    private void PlaceMeasureCursorAtMenu(Plot plot, char which)
    {
        double x = Snap(plot.GetCoordinates(_lastMenuPixel, plot.Axes.Bottom, plot.Axes.Left).X);
        bool wasOn = _vm.IsMeasuring;
        if (!wasOn)
            _vm.IsMeasuring = true; // places both cursors in the visible range
        if (which == 'A')
            _vm.SetMeasureCursors(x, _vm.MeasureB ?? x);
        else
            _vm.SetMeasureCursors(_vm.MeasureA ?? x, x);
        UpdateMeasureLines();
        _view.Refresh();
    }

    // ---------------------------------------------------------------- cursor

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_legendDrag is not null)
        {
            DragLegend(e);
            return;
        }
        if (_xs is null || _series.Count == 0)
            return;
        if (_measureDrag is not null)
            DragMeasure(e); // the hover cursor below follows the dragged line

        var pixel = ToPixel(e);
        _view.Cursor = _measureDrag is not null || MeasureLineAt(pixel) is not null ? Cursors.SizeWE
            : LegendAt(pixel) is not null ? Cursors.SizeAll
            : null;
        var plot = _graphs.Select(g => g.Plot).FirstOrDefault(p =>
        {
            var r = p.LastRender.DataRect;
            return pixel.X >= r.Left && pixel.X <= r.Right && pixel.Y >= r.Top && pixel.Y <= r.Bottom;
        });
        if (plot is null)
        {
            HideCursor();
            return;
        }

        double x = plot.GetCoordinates(pixel, plot.Axes.Bottom, plot.Axes.Left).X;
        int index = _xSorted ? NearestSorted(_xs, x) : NearestUnsorted(_xs, x);
        if (index < 0)
            return;

        double snappedX = _xs[index];
        foreach (var g in _graphs)
        {
            if (g.Cursor is { } cursor)
            {
                cursor.X = snappedX;
                cursor.IsVisible = true;
            }
        }
        _vm.CursorXText = _vm.FormatX(snappedX);

        foreach (var s in _series)
        {
            double y = s.Ys[index];
            s.Marker.Location = new Coordinates(snappedX, y);
            s.Marker.IsVisible = !double.IsNaN(y);
            s.Item.CursorText = MainViewModel.FormatValue(y, s.Item.Unit);
        }
        PlaceValueLabels(snappedX, index);
        _view.Refresh();
    }

    /// <summary>
    /// Writes each value next to its point, on the right of the cursor (left near the right edge),
    /// spreading the labels of a graph vertically so they never overlap.
    /// </summary>
    private void PlaceValueLabels(double x, int index)
    {
        foreach (var g in _graphs)
        {
            var inGraph = _series.Where(s => s.Graph == g).ToList();
            if (!_vm.ShowCursorLabels)
            {
                foreach (var s in inGraph)
                    s.Label.IsVisible = false;
                continue;
            }

            var data = g.Plot.LastRender.DataRect;
            float cursorPixel = g.Plot.GetPixel(new Coordinates(x, 0), g.Plot.Axes.Bottom, g.Plot.Axes.Left).X;
            bool toLeft = cursorPixel > data.Right - 170 * _scale;
            float gap = 12 * _scale;
            float height = 11 * FontScale * 1.35f + 8 * _scale; // text + padding + border
            float spacing = height + 2 * _scale;

            var visible = new List<(Series S, float Pixel)>();
            foreach (var s in inGraph)
            {
                double y = s.Ys[index];
                bool show = !double.IsNaN(y);
                s.Label.IsVisible = show;
                if (!show)
                    continue;
                s.Label.Location = new Coordinates(x, y);
                s.Label.LabelText = s.Item.CursorText;
                s.Label.Alignment = toLeft ? Alignment.MiddleRight : Alignment.MiddleLeft;
                s.Label.OffsetX = toLeft ? -gap : gap;
                visible.Add((s, g.Plot.GetPixel(new Coordinates(x, y), g.Plot.Axes.Bottom, s.Axis).Y));
            }
            if (visible.Count == 0)
                continue;

            // Top to bottom: push each label below the previous one, then pull the stack back inside the data area.
            visible.Sort((a, b) => a.Pixel.CompareTo(b.Pixel));
            var placed = new float[visible.Count];
            for (int i = 0; i < visible.Count; i++)
            {
                float wanted = Math.Clamp(visible[i].Pixel, data.Top + height / 2, data.Bottom - height / 2);
                placed[i] = i == 0 ? wanted : Math.Max(wanted, placed[i - 1] + spacing);
            }
            float overflow = placed[^1] - (data.Bottom - height / 2);
            if (overflow > 0)
            {
                for (int i = 0; i < placed.Length; i++)
                    placed[i] -= overflow;
            }
            for (int i = 0; i < visible.Count; i++)
                visible[i].S.Label.OffsetY = placed[i] - visible[i].Pixel;
        }
    }

    private void HideCursor()
    {
        if (!_graphs.Any(g => g.Cursor?.IsVisible == true))
            return;

        foreach (var g in _graphs)
        {
            if (g.Cursor is { } cursor)
                cursor.IsVisible = false;
        }
        foreach (var s in _series)
        {
            s.Marker.IsVisible = false;
            s.Label.IsVisible = false;
            s.Item.CursorText = "";
        }
        _vm.CursorXText = "Survolez le graphe";
        _view.Refresh();
    }

    // ---------------------------------------------------------------- array helpers

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
