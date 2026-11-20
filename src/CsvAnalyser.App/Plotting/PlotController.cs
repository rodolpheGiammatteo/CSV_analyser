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
public sealed class PlotController
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
    private sealed record Series(ChannelItemViewModel Item, double[] Ys, IPlottable Curve, Marker Marker);

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
    }

    public PlotController(WpfPlot view, MainViewModel vm)
    {
        _view = view;
        _vm = vm;
        _paddingMatcher = new PaddingMatcher(this);
        _vm.PlotChanged += OnPlotChanged;
        _vm.CurrentAxisRange = GetCurrentAxisRange;
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
        menu.Add("Réglages des axes…", plot => _vm.EditAxesCommand.Execute(GraphOf(plot)));
        menu.Add("Ajuster", _ => _vm.AutoScaleCommand.Execute(null));
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
                AutoScaleWithHistory();
                break;
            case PlotChange.UndoView:
                Undo();
                break;
            case PlotChange.RedoView:
                Redo();
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
            specs = [new GraphSpec(new GraphViewModel(0, () => { }), [])];
        _layoutSignature = GraphLayout.Signature(specs);
        _axisMembers = specs.SelectMany(g => g.Axes.Select(a => (Key: AxisId(g.Graph, a.Key), Members: string.Join("+", a.Curves.Select(c => c.Channel.SourceName)))))
            .ToDictionary(x => x.Key, x => x.Members);

        _graphs.Clear();
        _series.Clear();
        _xs = _vm.Dataset is { } ds && _vm.SelectedXAxis is { } option ? option.GetValues(ds) : null;
        _xSorted = _vm.SelectedXAxis?.IsSorted ?? true;

        for (int gi = 0; gi < specs.Count; gi++)
        {
            var spec = specs[gi];
            var render = new GraphRender(spec.Graph, new Plot());
            _graphs.Add(render);
            BuildGraph(render, spec, isBottom: gi == specs.Count - 1);
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

        _view.Refresh();
    }

    private void BuildGraph(GraphRender render, GraphSpec spec, bool isBottom)
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
                    created.Add((new Series(item, item.Channel.Values, curve, null!), axis));
                }
            }

            // Markers and cursor are added last so they are drawn on top of the curves.
            foreach (var (pending, axis) in created)
            {
                var marker = plot.Add.Marker(0, 0, MarkerShape.FilledCircle, 8 * _scale, pending.Item.Color);
                marker.Axes.YAxis = axis;
                marker.IsVisible = false;
                marker.LegendText = "";
                var series = pending with { Marker = marker };
                _series.Add(series);
                ApplyAppearance(series);
            }

            var cursor = plot.Add.VerticalLine(0);
            cursor.Color = ScottPlot.Color.FromHex(_vm.SelectedTheme.CursorColor).WithAlpha(0.8);
            cursor.LinePattern = LinePattern.Dashed;
            cursor.LineWidth = _scale;
            cursor.IsVisible = false;
            render.Cursor = cursor;
        }

        // Only the bottom graph shows the X axis; the others share it.
        plot.Axes.Bottom.Label.Text = isBottom ? _vm.XAxisLabel : "";
        plot.Axes.Bottom.TickLabelStyle.IsVisible = isBottom;
        plot.Legend.IsVisible = spec.Axes.Count > 0;
        plot.Legend.Alignment = Alignment.UpperRight;
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

    private void AutoScaleWithHistory()
    {
        var before = Capture();
        SetFullX();
        foreach (var g in _graphs)
            FitGraphY(g);
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

    // ---------------------------------------------------------------- stacked layout

    /// <summary>
    /// Before each render, gives every subplot the same left and right padding (the widest needed),
    /// so the data areas line up and the X axis is the same for all graphs.
    /// </summary>
    private sealed class PaddingMatcher(PlotController owner) : IMultiplotPreRenderAction
    {
        public void Invoke() => owner.MatchPaddings();
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
        }

        if (_wheelTimer.IsEnabled)
        {
            _wheelTimer.Stop();
            CommitGesture();
        }
        _gestureStart ??= Capture();
    }

    private void OnMouseUpAfterPlot(object sender, MouseButtonEventArgs e)
    {
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
        _gestureStart ??= Capture();
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

    // ---------------------------------------------------------------- cursor

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_xs is null || _series.Count == 0)
            return;

        var position = e.GetPosition(_view);
        var pixel = new Pixel((float)(position.X * _view.DisplayScale), (float)(position.Y * _view.DisplayScale));
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
        _view.Refresh();
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
