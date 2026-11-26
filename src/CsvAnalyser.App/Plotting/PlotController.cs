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
public sealed partial class PlotController : IPlotView
{
    private const double Margin = 0.04;
    private const string XKey = "X";

    private readonly WpfPlot _view;
    private readonly MainViewModel _vm;
    private readonly List<GraphRender> _graphs = [];
    private readonly List<Series> _series = [];
    private double[]? _xs;
    private bool _xSorted;

    /// <summary>Unsorted X split into a few ascending runs (each drawn as a line); null when sorted or unordered.</summary>
    private IReadOnlyList<(int Start, int End)>? _xRuns;
    private string _layoutSignature = "";
    private Dictionary<string, string> _axisMembers = [];

    // ScottPlot renders in physical pixels: sizes must follow the Windows display scaling.
    private float _scale = 1;

    // Zoom/pan history: the view before a mouse gesture (or a burst of wheel ticks) is recorded when it ends.
    private readonly DispatcherTimer _wheelTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private ViewSnapshot? _gestureStart;

    private readonly PaddingMatcher _paddingMatcher;

    /// <param name="Curve">
    /// The SignalXY (sorted X, or the first ascending run of an X that goes back a few times) or Scatter (unordered X)
    /// drawing the data.
    /// </param>
    /// <param name="Marker">The dot that follows the vertical cursor.</param>
    /// <param name="Label">The value written next to the marker (option "valeurs dans les graphes").</param>
    private sealed record Series(ChannelItemViewModel Item, double[] Ys, IPlottable Curve, Marker Marker, Text Label, GraphRender Graph, IYAxis Axis)
    {
        /// <summary>The other ascending runs of an X that goes back a few times: same style, no legend entry.</summary>
        public IReadOnlyList<SignalXY> Pieces { get; init; } = [];

        /// <summary>Spaced markers (sorted X only): placed before each render by <see cref="PlaceSpacedMarkers"/>.</summary>
        public Scatter? Spaced { get; init; }

        public List<double> SpacedXs { get; init; } = [];

        public List<double> SpacedYs { get; init; } = [];
    }

    private sealed class AxisRender(string key, AxisSpec spec, IYAxis axis)
    {
        public string Key { get; } = key;
        public AxisSpec Spec { get; set; } = spec;
        public IYAxis Axis { get; } = axis;
    }

    private sealed class GraphRender(GraphViewModel graph, Plot plot)
    {
        public GraphViewModel Graph { get; } = graph;

        /// <summary>Last graph of its column: always shows the X axis.</summary>
        public bool IsBottom { get; set; }
        public Plot Plot { get; } = plot;
        public List<AxisRender> Axes { get; } = [];
        public VerticalLine? Cursor { get; set; }
        public VerticalLine? LineA { get; set; }
        public VerticalLine? LineB { get; set; }
        public int Column { get; set; }
        public bool IsTop { get; set; }
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

        // No autoscale on a middle click (ScottPlot's), no benchmark overlay.
        _view.UserInputProcessor.UserActionResponses.RemoveAll(r => r is SingleClickAutoscale);
        _view.UserInputProcessor.DoubleLeftClickBenchmark(false);
        ApplyMouseButtons();
        SetupContextMenu();

        Rebuild(resetView: true);
    }

    /// <summary>
    /// The plot menu. Right-clicked on an axis, it starts with that axis back to automatic
    /// (rebuilt at each right-click, see <see cref="OnPreviewMouseDown"/>).
    /// </summary>
    private void SetupContextMenu(GraphRender? axisGraph = null, IAxis? axis = null)
    {
        if (_view.Menu is not WpfPlotMenu menu)
            return;
        menu.Clear();
        if (axisGraph is not null && axis is IYAxis yAxis && axisGraph.Axes.FirstOrDefault(a => a.Axis == yAxis) is { } axisRender)
        {
            string name = string.IsNullOrEmpty(axisRender.Spec.Label) ? L.T("Axe Y") : axisRender.Spec.Label;
            menu.Add(L.F("Axe « {0} » en automatique", MenuText(name)), _ => SetAxisAuto(axisGraph, axisRender));
            menu.AddSeparator();
        }
        else if (axisGraph is not null && axis is not null && axis == axisGraph.Plot.Axes.Bottom)
        {
            menu.Add(L.T("Axe X en automatique (tout afficher)"), _ => AutoScaleWithHistory(x: true, y: false));
            menu.AddSeparator();
        }
        menu.Add(L.T("Ajuster ce graphe en Y"), plot =>
        {
            if (_graphs.FirstOrDefault(g => g.Plot == plot) is { } graph)
            {
                var before = Capture();
                FitGraphY(graph, fitManual: true);
                KeepManualBoundsOnScreen();
                Record(before);
                _view.Refresh();
            }
        });
        menu.Add(L.T("Ajuster X"), _ => AutoScaleWithHistory(x: true, y: false));
        menu.Add(L.T("Tout ajuster (X et Y)"), _ => AutoScaleWithHistory(x: true, y: true));
        menu.AddSeparator();
        if (axisGraph is null) // "here" means a point of the graph, not of an axis
        {
            // The curve nearest to the mouse (vertically, at that X) leaves the graph: unticked, Ctrl+Z brings it back.
            if (NearestSeries(_lastMenuPixel) is { } nearest)
            {
                var item = nearest.Item;
                menu.Add(L.F("Retirer « {0} » du graphe", MenuText(item.Name)), _ => _vm.RemoveFromGraph(item));
                menu.AddSeparator();
            }
            AddAnnotationMenuItems(menu);
            menu.Add(L.T("Placer le curseur X1 ici"), plot => PlaceMeasureCursorAtMenu(plot, 'A'));
            menu.Add(L.T("Placer le curseur X2 ici"), plot => PlaceMeasureCursorAtMenu(plot, 'B'));
            menu.Add(L.T("Placer le curseur Y1 ici"), plot => PlaceMeasureYAtMenu(plot, '1'));
            menu.Add(L.T("Placer le curseur Y2 ici"), plot => PlaceMeasureYAtMenu(plot, '2'));
            menu.AddSeparator();
        }
        menu.Add(L.T("Spectre (FFT) de ce graphe…"), plot => _vm.ShowSpectrumCommand.Execute(GraphOf(plot)));
        menu.Add(L.T("Titre, légende et axes…"), plot => _vm.EditAxesCommand.Execute(GraphOf(plot)));
        menu.AddSeparator();
        // Images at the resolution chosen in Paramètres; with several graphs, this one or all of them.
        if (_graphs.Count > 1)
        {
            menu.Add(L.T("Copier l'image de ce graphe"), plot => PlotTools.CopyToClipboard(RenderGraphImage(plot, SkiaSharp.SKEncodedImageFormat.Png)));
            menu.Add(L.T("Copier l'image de tous les graphes"), _ => PlotTools.CopyToClipboard(RenderAllImage(SkiaSharp.SKEncodedImageFormat.Png)));
            menu.Add(L.T("Enregistrer l'image de ce graphe…"), plot => PlotTools.SaveWithDialog(f => RenderGraphImage(plot, f), ImageName(GraphOf(plot)?.Title)));
            menu.Add(L.T("Enregistrer l'image de tous les graphes…"), _ => PlotTools.SaveWithDialog(RenderAllImage, ImageName(L.T("graphes"))));
        }
        else
        {
            menu.Add(L.T("Copier l'image"), _ => PlotTools.CopyToClipboard(RenderAllImage(SkiaSharp.SKEncodedImageFormat.Png)));
            menu.Add(L.T("Enregistrer l'image…"), _ => PlotTools.SaveWithDialog(RenderAllImage, ImageName(null)));
        }
    }

    private string ImageName(string? what)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(_vm.Dataset?.FilePath ?? "graphe");
        return (what is { Length: > 0 } ? $"{name} - {what}" : name) + ".png";
    }

    /// <summary>One graph as an image (the hover cursor is hidden first).</summary>
    private byte[] RenderGraphImage(Plot plot, SkiaSharp.SKEncodedImageFormat format)
    {
        HideCursor();
        var rect = _view.Multiplot.LastRender.GetLastRenderRectangle(plot);
        var size = rect is { } r ? new PixelSize(r.Width, r.Height)
            : new PixelSize((float)(_view.ActualWidth * _view.DisplayScale), (float)(_view.ActualHeight * _view.DisplayScale));
        var bytes = PlotTools.RenderPlot(plot, size, _vm.ImageScale, format);
        _view.Refresh(); // the export changed the last-render layout used by the mouse
        return bytes;
    }

    /// <summary>Every graph, as laid out on screen, as one image.</summary>
    private byte[] RenderAllImage(SkiaSharp.SKEncodedImageFormat format)
    {
        HideCursor();
        var size = new PixelSize((float)(_view.ActualWidth * _view.DisplayScale), (float)(_view.ActualHeight * _view.DisplayScale));
        var bytes = PlotTools.RenderMultiplot(_view.Multiplot, size, _vm.ImageScale, format);
        _view.Refresh();
        return bytes;
    }

    /// <summary>A Y axis back to automatic (manual bounds dropped) and fitted to the visible data.</summary>
    private void SetAxisAuto(GraphRender g, AxisRender a)
    {
        var before = Capture();
        a.Spec.Settings.IsAuto = true; // no change event when it already was: the fit below covers that case
        FitGraphY(g);
        Record(before);
        _view.Refresh();
    }

    /// <summary>Left drag zooms on a rectangle and wheel-button drag pans, or the reverse (Paramètres).</summary>
    /// <param name="leftPans">For one gesture: a left drag started on an axis moves that axis, whatever the setting.</param>
    private void ApplyMouseButtons(bool leftPans = false)
    {
        PlotTools.ApplyMouseButtons(_view, _vm.LeftDragZooms && !leftPans);
    }

    private bool _axisPanGesture;

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
            case PlotChange.Grid:
                foreach (var g in _graphs)
                    PlotTools.ApplyGrid(g.Plot, _vm.SelectedTheme.DataBackground, _vm.SelectedTheme.AxisColor, _vm.Grid, _scale);
                _view.Refresh();
                break;
            case PlotChange.MouseButtons:
                ApplyMouseButtons();
                break;
            case PlotChange.Layout:
                Rebuild(resetView: false);
                break;
            case PlotChange.Measure:
                UpdateMeasureLines();
                _view.Refresh();
                break;
            case PlotChange.Annotations:
                UpdateAnnotations();
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
        _xRuns = _xSorted || _xs is null ? null : AscendingRuns.Find(_xs);

        for (int gi = 0; gi < specs.Count; gi++)
        {
            var spec = specs[gi];
            var render = new GraphRender(spec.Graph, new Plot());
            _graphs.Add(render);
            var cell = MainViewModel.GraphCell(gi, specs.Count, _vm.GraphColumns);
            render.Column = cell.Column;
            render.IsTop = cell.Row == 0;
            BuildGraph(render, spec, isBottom: cell.Row == cell.RowsInColumn - 1, isTop: cell.Row == 0);
        }

        var plots = _graphs.Select(g => g.Plot).ToArray();
        _view.Reset(plots[0]);
        _view.Multiplot.Reset(plots[0]);
        for (int i = 1; i < plots.Length; i++)
            _view.Multiplot.AddPlot(plots[i]);
        _view.Multiplot.Layout = new ColumnsLayout(this);
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
        UpdateAnnotations();
        _view.Refresh();
    }

    private void BuildGraph(GraphRender render, GraphSpec spec, bool isBottom, bool isTop)
    {
        var plot = render.Plot;
        ApplyTheme(plot);

        if (_xs is not null)
        {
            var created = new List<(Series Series, IYAxis Axis)>();
            var axisOf = new Dictionary<ChannelItemViewModel, IYAxis>();
            bool leftUsed = false, rightUsed = false;
            foreach (var axisSpec in spec.Axes)
            {
                // The first axis of each side is the plot's own; the others are added beside it.
                IYAxis axis = axisSpec.IsRight
                    ? (rightUsed ? plot.Axes.AddRightAxis() : plot.Axes.Right)
                    : (leftUsed ? plot.Axes.AddLeftAxis() : plot.Axes.Left);
                if (axisSpec.IsRight)
                    rightUsed = true;
                else
                    leftUsed = true;
                StyleAxis(axis);
                var axisRender = new AxisRender(axisSpec.Key, axisSpec, axis);
                render.Axes.Add(axisRender);
                ApplyAxisLabel(plot, axisRender);
                foreach (var item in axisSpec.Curves)
                    axisOf[item] = axis;
            }

            // Every axis on the right: the plot's own left axis would stay as an empty 0–1 scale.
            if (rightUsed && !leftUsed)
            {
                var left = plot.Axes.Left;
                left.TickLabelStyle.IsVisible = false;
                left.MajorTickStyle.Length = 0;
                left.MinorTickStyle.Length = 0;
                left.Label.Text = "";
            }
            // The horizontal grid lines follow the first axis (by default they follow the left one, maybe empty).
            if (render.Axes.Count > 0)
                plot.Grid.YAxis = render.Axes[0].Axis;

            // Drawing order = order of the plotted list: the last curve is drawn on top (and listed last in the legend).
            foreach (var item in spec.Curves.OrderBy(c => _vm.PlottedChannels.IndexOf(c)))
            {
                var axis = axisOf[item];
                IPlottable curve;
                var pieces = new List<SignalXY>();
                if (_xSorted)
                    curve = plot.Add.SignalXY(_xs, item.Channel.Values, item.Color);
                else if (_xRuns is { } runs)
                {
                    // X goes back a few times (clock resync): one line per ascending run, over the same arrays.
                    foreach (var (start, end) in runs)
                    {
                        var piece = plot.Add.SignalXY(_xs, item.Channel.Values, item.Color);
                        piece.Data.MinimumIndex = start;
                        piece.Data.MaximumIndex = end - 1;
                        piece.Axes.YAxis = axis;
                        pieces.Add(piece);
                    }
                    curve = pieces[0];
                    pieces.RemoveAt(0);
                }
                else
                    curve = plot.Add.ScatterPoints(_xs, item.Channel.Values, item.Color);
                curve.Axes.YAxis = axis;
                created.Add((new Series(item, item.Channel.Values, curve, null!, null!, render, axis) { Pieces = pieces }, axis));
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

                Scatter? spaced = null;
                List<double> spacedXs = [], spacedYs = [];
                if (pending.Curve is SignalXY && _xSorted)
                {
                    // The scatter reads these lists, filled before each render; drawn over the line.
                    spaced = plot.Add.ScatterPoints(spacedXs, spacedYs, pending.Item.Color);
                    spaced.Axes.YAxis = axis;
                    spaced.LineWidth = 0;
                    spaced.LegendText = "";
                    spaced.IsVisible = false;
                }
                var series = pending with { Marker = marker, Label = label, Spaced = spaced, SpacedXs = spacedXs, SpacedYs = spacedYs };
                _series.Add(series);
                ApplyAppearance(series);
            }

            var cursor = plot.Add.VerticalLine(0);
            cursor.Color = ScottPlot.Color.FromHex(_vm.SelectedTheme.CursorColor).WithAlpha(0.8);
            cursor.LinePattern = LinePattern.Dashed;
            cursor.LineWidth = _scale;
            cursor.IsVisible = false;
            render.Cursor = cursor;
            render.LineA = AddMeasureLine(plot, "X1", MeasureColorA, labelled: isTop);
            render.LineB = AddMeasureLine(plot, "X2", MeasureColorB, labelled: isTop);
        }

        render.IsBottom = isBottom;
        ApplyXAxis(render);
        ApplyGraphAppearance(render);
    }

    /// <summary>
    /// The graphs share X: by default only the bottom graph of each column shows its ticks and title; Paramètres can
    /// show the ticks under every graph, with or without the title (<see cref="XAxisPlacement"/>).
    /// </summary>
    private void ApplyXAxis(GraphRender g)
    {
        var placement = _vm.XAxisPlacement;
        bool ticks = g.IsBottom || placement != XAxisPlacement.Bottom;
        bool title = g.IsBottom || placement == XAxisPlacement.AllGraphsWithTitle;
        g.Plot.Axes.Bottom.Label.Text = title ? _vm.XAxisLabel : "";
        g.Plot.Axes.Bottom.TickLabelStyle.IsVisible = ticks;
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
            ApplyXAxis(g);
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
        PlotTools.ApplyGrid(plot, t.DataBackground, t.AxisColor, _vm.Grid, _scale);
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
                bool spaced = item.HasMarkers && item.MarkerSpacing > 0 && s.Spaced is not null;
                foreach (var piece in s.Pieces.Prepend(signal))
                {
                    piece.Color = item.Color.WithAlpha(item.Opacity);
                    piece.LegendText = piece == signal ? item.LegendText : "";
                    piece.LineWidth = item.IsLineVisible ? (float)item.LineWidth * _scale : 0;
                    piece.LinePattern = ToPattern(item.Line);
                    piece.MarkerShape = ToShape(item.Marker);
                    piece.MarkerSize = item.HasMarkers && !spaced ? (float)item.MarkerSize * _scale : 0;
                }
                if (s.Spaced is { } markers)
                {
                    markers.IsVisible = spaced;
                    markers.Color = item.Color.WithAlpha(item.Opacity);
                    markers.MarkerShape = ToShape(item.Marker);
                    markers.MarkerSize = (float)item.MarkerSize * _scale;
                }
                break;

            case Scatter scatter:
                // Unordered X (not a time): points only, a line would zigzag across the plot.
                scatter.Color = item.Color.WithAlpha(item.Opacity);
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
    /// <summary>
    /// The fit buttons (and Ajuster in the menus): manual axes are fitted to the data too, and keep
    /// the bounds now shown as their manual bounds (as after a zoom with the mouse).
    /// </summary>
    private void AutoScaleWithHistory(bool x, bool y)
    {
        var before = Capture();
        if (x)
            SetFullX();
        if (y)
        {
            foreach (var g in _graphs)
                FitGraphY(g, fitManual: true);
            KeepManualBoundsOnScreen();
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
    /// <summary>Y of a graph fitted to the data in the visible X range; a manual axis keeps its bounds unless <paramref name="fitManual"/>.</summary>
    private void FitGraphY(GraphRender g, bool fitManual = false)
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
            if (!fitManual && a.Spec.Settings.ManualRange is { } manual)
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
            ranges = ZeroAlignment.Align(ranges, isFixed, g.Axes.Select(a => a.Spec.Settings.AlignValue).ToArray());

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
            owner.PlaceMeasureMarks();
            owner.PlaceAnnotationLabels();
            owner.PlaceSpacedMarkers();
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
            // Each axis is aligned on its own reference value (0 unless set in the graphs window).
            var references = g.Axes.ToDictionary(a => a.Axis, a => a.Spec.Settings.AlignValue);
            double ZeroFraction(IYAxis axis) => (references[axis] - axis.Range.Min) / (axis.Range.Max - axis.Range.Min);

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
                        double r = references[axis];
                        g.Plot.Axes.SetLimitsY(r - f * span, r + (1 - f) * span, axis);
                    }
                }
            }
            foreach (var axis in axes)
                _lastYRanges[axis] = (axis.Range.Min, axis.Range.Max);
        }
    }

    /// <summary>
    /// Graphs in <see cref="MainViewModel.GraphColumns"/> columns: the first column top to bottom, then the next one;
    /// each column shares its height equally between its graphs.
    /// </summary>
    private sealed class ColumnsLayout(PlotController owner) : IMultiplotLayout
    {
        public PixelRect[] GetSubplotRectangles(SubplotCollection subplots, PixelRect figureRect)
        {
            int count = subplots.Count;
            var rects = new PixelRect[count];
            int columns = Math.Clamp(owner._vm.GraphColumns, 1, Math.Max(1, count));
            float width = figureRect.Width / columns;
            // Height of a graph = its share of the column, weighted by its height factor.
            var weights = Enumerable.Range(0, count)
                .Select(i => i < owner._graphs.Count ? Math.Clamp(owner._graphs[i].Graph.HeightFactor, 0.1, 10) : 1)
                .ToArray();
            for (int i = 0; i < count; i++)
            {
                var (column, row, _) = MainViewModel.GraphCell(i, count, columns);
                int first = i - row;
                int last = first;
                while (last + 1 < count && MainViewModel.GraphCell(last + 1, count, columns).Column == column)
                    last++;
                double total = 0, above = 0;
                for (int k = first; k <= last; k++)
                {
                    total += weights[k];
                    if (k < i)
                        above += weights[k];
                }
                float top = figureRect.Top + (float)(figureRect.Height * above / total);
                float height = (float)(figureRect.Height * weights[i] / total);
                float left = figureRect.Left + column * width;
                rects[i] = new PixelRect(left, left + width, top + height, top);
            }
            return rects;
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
        if (plots.Length != _graphs.Count)
            return;

        float width = (float)(_view.ActualWidth * _view.DisplayScale);
        float height = (float)(_view.ActualHeight * _view.DisplayScale);
        if (width < 10 || height < 10)
            return;

        var rects = _view.Multiplot.Layout.GetSubplotRectangles(_view.Multiplot.Subplots, new PixelRect(0, width, height, 0));
        var engine = new ScottPlot.LayoutEngines.Automatic();
        using var paint = Paint.NewDisposablePaint();
        var layouts = new Layout[plots.Length];
        // Same left and right padding inside each column, so the data areas of a column line up.
        var left = new Dictionary<int, float>();
        var right = new Dictionary<int, float>();
        for (int i = 0; i < plots.Length; i++)
        {
            layouts[i] = engine.GetLayout(rects[i], plots[i], paint);
            int column = _graphs[i].Column;
            left[column] = Math.Max(left.GetValueOrDefault(column), layouts[i].DataRect.Left - rects[i].Left);
            right[column] = Math.Max(right.GetValueOrDefault(column), rects[i].Right - layouts[i].DataRect.Right);
        }
        for (int i = 0; i < plots.Length; i++)
        {
            float bottom = rects[i].Bottom - layouts[i].DataRect.Bottom;
            float top = layouts[i].DataRect.Top - rects[i].Top;
            int column = _graphs[i].Column;
            plots[i].Layout.Fixed(new PixelPadding(left[column], right[column], bottom, top));
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
            case MouseButton.Right:
                _lastMenuPixel = ToPixel(e);
                var (axisGraph, axisUnder) = AxisAt(_lastMenuPixel);
                SetupContextMenu(axisGraph, axisUnder);
                break;
        }

        if (TryStartLegendDrag(e) || TryStartMeasureDrag(e) || TryStartAnnotationDrag(e))
            return;
        RememberAxisClick(e);
        if (_axisClick is not null && _vm.LeftDragZooms)
        {
            ApplyMouseButtons(leftPans: true);
            _axisPanGesture = true;
        }

        RememberInteractionAxis(e);
        if (_wheelTimer.IsEnabled)
        {
            _wheelTimer.Stop();
            CommitGesture();
        }
        _gestureStart ??= Capture();
    }

    /// <summary>Cursor positions when the last cursor drag started (a double-click puts them back).</summary>
    private (double? A, double? B, double? Y1, double? Y2)? _measureDragStart;

    /// <summary>A measurement cursor double-clicked: its editor opens when the button is released.</summary>
    private (char Which, System.Windows.Point Position)? _pendingMeasureEdit;

    private void OnMouseUpAfterPlot(object sender, MouseButtonEventArgs e)
    {
        if (_pendingMeasureEdit is { } edit)
        {
            _pendingMeasureEdit = null;
            _view.Dispatcher.BeginInvoke(() => OpenMeasureEditor(edit.Which, edit.Position), DispatcherPriority.Input);
            return;
        }
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
        if (_annotationDrag is not null)
        {
            EndAnnotationDrag();
            return;
        }
        if (_axisPanGesture)
        {
            _axisPanGesture = false;
            _view.Dispatcher.BeginInvoke(() => ApplyMouseButtons(), DispatcherPriority.Input);
        }
        if (_axisClick is not null && e.ChangedButton == MouseButton.Left)
            _view.Dispatcher.BeginInvoke(() => TryOpenAxisEditor(e), DispatcherPriority.Input);
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
        KeepManualBoundsOnScreen();
        Record(start);
    }

    /// <summary>
    /// After a zoom or pan with the mouse, a manual axis takes the bounds now on screen
    /// (otherwise the next "Ajuster" would jump back to the old typed bounds).
    /// </summary>
    private void KeepManualBoundsOnScreen()
    {
        foreach (var g in _graphs)
        {
            foreach (var a in g.Axes)
            {
                if (a.Spec.Settings.IsManual)
                    a.Spec.Settings.SetBoundsSilently(a.Axis.Range.Min, a.Axis.Range.Max);
            }
        }
    }

    /// <summary>The view changed (zoom, pan, fit): one step of the application-wide undo history.</summary>
    private void Record(ViewSnapshot previous)
    {
        if (previous.SameAs(Capture()))
            return;
        _vm.CommitNow();
    }

    private void Undo()
    {
        _wheelTimer.Stop();
        CommitGesture();
        _vm.Undo();
    }

    private void Redo()
    {
        _wheelTimer.Stop();
        CommitGesture();
        _vm.Redo();
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
        // Room above the upper graph of each column for the "A" and "B" labels.
        foreach (var g in _graphs.Where(g => g.IsTop))
            g.Plot.Axes.Top.MinimumSize = on ? 22 * FontScale : 0;
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
        UpdateMeasureYLines();
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
        if (MeasureYLineAt(pixel) is { } y)
            return y;
        if (!_vm.IsMeasuring || _vm.MeasureA is not { } a || _vm.MeasureB is not { } b)
            return null;
        float tolerance = 6 * _scale;
        foreach (var g in _graphs)
        {
            var r = g.Plot.LastRender.DataRect;
            // The labels of the upper graph are just above its data area.
            if (pixel.Y < r.Top - 24 * _scale || pixel.Y > r.Bottom)
                continue;
            // Where the lines are drawn: at the edge when the cursor is outside the visible range (see PlaceMeasureMarks).
            float pa = g.Plot.GetPixel(new Coordinates(g.LineA?.X ?? a, 0), g.Plot.Axes.Bottom, g.Plot.Axes.Left).X;
            float pb = g.Plot.GetPixel(new Coordinates(g.LineB?.X ?? b, 0), g.Plot.Axes.Bottom, g.Plot.Axes.Left).X;
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
        if (e.ClickCount == 2)
        {
            // Double-click: type the position, as for the bounds of an axis (opened at the release, see OnMouseUpAfterPlot).
            // The first click may have moved the line by a pixel or two: it goes back where it was.
            EndMeasureDrag();
            if (_measureDragStart is { } start)
            {
                if (start.A is { } a0 && start.B is { } b0)
                    _vm.SetMeasureCursors(a0, b0);
                if (_yCursors is { } yc && start.Y1 is { } y10 && start.Y2 is { } y20)
                    _vm.SetMeasureY(yc.Graph.Graph, yc.Axis.Key, y10, y20);
                UpdateMeasureLines();
                _view.Refresh();
            }
            _pendingMeasureEdit = (which, e.GetPosition(_view));
            e.Handled = true;
            return true;
        }
        _measureDrag = which;
        _measureDragStart = (_vm.MeasureA, _vm.MeasureB, _vm.MeasureY1, _vm.MeasureY2);
        _view.CaptureMouse();
        e.Handled = true; // no pan while moving a cursor
        return true;
    }

    /// <summary>
    /// Small editor to type the position of a measurement cursor (double-click on its line): X1/X2 are snapped to the
    /// nearest sample, Y1/Y2 take the value as typed (in the unit of their axis).
    /// </summary>
    private void OpenMeasureEditor(char which, System.Windows.Point position)
    {
        if (which is '1' or '2')
        {
            if (_yCursors is not { } y || _vm.MeasureY1 is not { } y1 || _vm.MeasureY2 is not { } y2)
                return;
            string axis = string.IsNullOrEmpty(y.Axis.Spec.Label) ? L.T("Axe Y") : y.Axis.Spec.Label;
            string title = $"{L.T("Curseur")} Y{which} — {axis}";
            PlotTools.ShowBoundEditor(_view, position, title, which == '1' ? y1 : y2, value =>
            {
                _vm.SetMeasureY(y.Graph.Graph, y.Axis.Key, which == '1' ? value : y1, which == '2' ? value : y2);
                _view.Refresh();
                return null;
            });
            return;
        }

        if (_vm.MeasureA is not { } a || _vm.MeasureB is not { } b)
            return;
        string name = which == 'A' ? "X1" : "X2";
        PlotTools.ShowBoundEditor(_view, position, $"{L.T("Curseur")} {name} — {_vm.XAxisLabel}", which == 'A' ? a : b, value =>
        {
            double x = Snap(value);
            if (which == 'A')
                _vm.SetMeasureCursors(x, b);
            else
                _vm.SetMeasureCursors(a, x);
            UpdateMeasureLines();
            _view.Refresh();
            return null;
        });
    }

    private void DragMeasure(MouseEventArgs e)
    {
        if (_measureDrag is '1' or '2')
        {
            DragMeasureY(e);
            return;
        }
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

    // ---------------------------------------------------------------- typing a bound by clicking an axis

    private (GraphRender Graph, IAxis Axis, Pixel Pixel)? _axisClick;

    /// <summary>Mouse-down on an axis (outside the data area): remembered, the editor opens if the mouse is released without moving.</summary>
    private void RememberAxisClick(MouseButtonEventArgs e)
    {
        _axisClick = null;
        if (e.ChangedButton != MouseButton.Left)
            return;
        var pixel = ToPixel(e);
        if (AxisAt(pixel) is ({ } g, { } axis))
            _axisClick = (g, axis, pixel);
    }

    /// <summary>The Y axis, or the X axis, under a pixel outside the data areas (else nulls).</summary>
    private (GraphRender? Graph, IAxis? Axis) AxisAt(Pixel pixel)
    {
        foreach (var g in _graphs)
        {
            var r = g.Plot.LastRender.DataRect;
            if (pixel.X >= r.Left && pixel.X <= r.Right && pixel.Y >= r.Top && pixel.Y <= r.Bottom)
                return (null, null);
            if (g.Plot.GetAxis(pixel) is { } axis && (axis is IYAxis || axis == g.Plot.Axes.Bottom))
                return (g, axis);
        }
        return (null, null);
    }

    /// <summary>Click on the upper (or right) half of an axis: edit its maximum; lower (or left) half: its minimum.</summary>
    private bool TryOpenAxisEditor(MouseButtonEventArgs e)
    {
        if (_axisClick is not { } click || e.ChangedButton != MouseButton.Left)
            return false;
        _axisClick = null;
        var pixel = ToPixel(e);
        if (Math.Abs(pixel.X - click.Pixel.X) > 4 * _scale || Math.Abs(pixel.Y - click.Pixel.Y) > 4 * _scale)
            return false;

        var g = click.Graph;
        var data = g.Plot.LastRender.DataRect;
        var position = e.GetPosition(_view);
        if (click.Axis is IYAxis yAxis && g.Axes.FirstOrDefault(a => a.Axis == yAxis) is { } a)
        {
            bool max = pixel.Y < (data.Top + data.Bottom) / 2;
            var range = a.Axis.Range;
            string name = string.IsNullOrEmpty(a.Spec.Label) ? L.T("Axe Y") : a.Spec.Label;
            PlotTools.ShowBoundEditor(_view, position, $"{name} — {(max ? L.T("maximum") : L.T("minimum"))}", max ? range.Max : range.Min, value =>
            {
                double min = max ? range.Min : value, top = max ? value : range.Max;
                if (!(min < top))
                    return L.T("Le minimum doit rester inférieur au maximum.");
                a.Spec.Settings.Apply(manual: true, min, top); // a setting: kept by "Ajuster"
                return null;
            });
            return true;
        }
        if (click.Axis == g.Plot.Axes.Bottom)
        {
            bool max = pixel.X > (data.Left + data.Right) / 2;
            var range = g.Plot.Axes.Bottom.Range;
            PlotTools.ShowBoundEditor(_view, position, $"{L.T("Axe X")} — {(max ? L.T("maximum") : L.T("minimum"))}", max ? range.Max : range.Min, value =>
            {
                double min = max ? range.Min : value, top = max ? value : range.Max;
                if (!(min < top))
                    return L.T("Le minimum doit rester inférieur au maximum.");
                var before = Capture();
                SetX(min, top);
                Record(before);
                _view.Refresh();
                return null;
            });
            return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- measurement cursors Y1 and Y2

    private static readonly ScottPlot.Color MeasureColorY1 = ScottPlot.Color.FromHex("#E8590C");
    private static readonly ScottPlot.Color MeasureColorY2 = ScottPlot.Color.FromHex("#6741D9");

    private sealed record YCursors(GraphRender Graph, AxisRender Axis, HorizontalLine Line1, HorizontalLine Line2, Text Label1, Text Label2);

    private YCursors? _yCursors;

    /// <summary>
    /// Draws Y1 and Y2 on the chosen axis (or the first axis of the first graph); places them at a third and two thirds of
    /// the axis range when they have no position yet.
    /// </summary>
    private void UpdateMeasureYLines()
    {
        if (_yCursors is { } old)
        {
            foreach (var p in new IPlottable[] { old.Line1, old.Line2, old.Label1, old.Label2 })
                old.Graph.Plot.Remove(p);
            _yCursors = null;
        }
        if (!_vm.IsMeasuringY || _xs is null || _series.Count == 0)
            return;

        var graph = _graphs.FirstOrDefault(g => g.Graph == _vm.MeasureYGraph && g.Axes.Any(a => a.Key == _vm.MeasureYAxisKey));
        var axis = graph?.Axes.First(a => a.Key == _vm.MeasureYAxisKey);
        bool place = graph is null || _vm.MeasureY1 is null || _vm.MeasureY2 is null;
        if (graph is null || axis is null)
        {
            graph = _graphs.FirstOrDefault(g => g.Axes.Count > 0);
            if (graph is null)
                return;
            axis = graph.Axes[0];
        }
        double y1, y2;
        if (place)
        {
            var r = axis.Axis.Range;
            y1 = r.Min + (r.Max - r.Min) / 3;
            y2 = r.Min + 2 * (r.Max - r.Min) / 3;
        }
        else
        {
            y1 = _vm.MeasureY1!.Value;
            y2 = _vm.MeasureY2!.Value;
        }
        _vm.SetMeasureY(graph.Graph, axis.Key, y1, y2);

        _yCursors = new YCursors(graph, axis,
            AddMeasureYLine(graph.Plot, axis.Axis, y1, MeasureColorY1),
            AddMeasureYLine(graph.Plot, axis.Axis, y2, MeasureColorY2),
            AddMeasureYLabel(graph.Plot, axis.Axis, "Y1", MeasureColorY1),
            AddMeasureYLabel(graph.Plot, axis.Axis, "Y2", MeasureColorY2));
        PlaceMeasureMarks();
    }

    private HorizontalLine AddMeasureYLine(Plot plot, IYAxis axis, double y, ScottPlot.Color color)
    {
        var line = plot.Add.HorizontalLine(y);
        line.Axes.YAxis = axis;
        line.Color = color;
        line.LineWidth = 1.5f * _scale;
        return line;
    }

    private Text AddMeasureYLabel(Plot plot, IYAxis axis, string name, ScottPlot.Color color)
    {
        var text = plot.Add.Text(name, 0, 0);
        text.Axes.YAxis = axis;
        text.Alignment = Alignment.LowerRight;
        text.LabelStyle.FontSize = 11 * FontScale;
        text.LabelStyle.Bold = true;
        text.LabelStyle.ForeColor = ScottPlot.Colors.White;
        text.LabelStyle.BackgroundColor = color;
        text.LabelStyle.Padding = 2 * _scale;
        text.LabelStyle.BorderRadius = 3 * _scale;
        return text;
    }

    /// <summary>Before each render: the names Y1/Y2 sit at the right end of their line, whatever the X zoom.</summary>
    /// <summary>
    /// Before each render: a measurement cursor outside the visible range is drawn dashed against the nearest
    /// edge, its name with an arrow towards it, so it can be seen and dragged back. The names Y1/Y2 sit at the
    /// right end of their line.
    /// </summary>
    private void PlaceMeasureMarks()
    {
        if (_vm.IsMeasuring && _vm.MeasureA is { } a && _vm.MeasureB is { } b)
        {
            foreach (var g in _graphs)
            {
                var r = g.Plot.Axes.Bottom.Range;
                if (g.LineA is { } la)
                    PlaceVertical(la, a, r.Min, r.Max, "X1", g.IsTop);
                if (g.LineB is { } lb)
                    PlaceVertical(lb, b, r.Min, r.Max, "X2", g.IsTop);
            }
        }

        if (_yCursors is { } y && _vm.MeasureY1 is { } y1 && _vm.MeasureY2 is { } y2)
        {
            var yr = y.Axis.Axis.Range;
            PlaceHorizontal(y.Line1, y.Label1, y1, yr.Min, yr.Max, "Y1");
            PlaceHorizontal(y.Line2, y.Label2, y2, yr.Min, yr.Max, "Y2");
            double right = y.Graph.Plot.Axes.Bottom.Range.Max;
            y.Label1.Location = new Coordinates(right, y.Line1.Y);
            y.Label2.Location = new Coordinates(right, y.Line2.Y);
            y.Label1.OffsetX = y.Label2.OffsetX = -4 * _scale;
            // Lines close together (or both against the same edge): Y2's name goes left of Y1's, not on top of it.
            var plot = y.Graph.Plot;
            float p1 = plot.GetPixel(new Coordinates(right, y.Line1.Y), plot.Axes.Bottom, y.Axis.Axis).Y;
            float p2 = plot.GetPixel(new Coordinates(right, y.Line2.Y), plot.Axes.Bottom, y.Axis.Axis).Y;
            if (Math.Abs(p1 - p2) < 20 * FontScale)
                y.Label2.OffsetX = -(8 * _scale + 46 * FontScale);
        }
    }

    /// <summary>The value, or the edge it is beyond (side -1 below/left, +1 above/right, 0 visible).</summary>
    private static (double Position, int Side) ClampToView(double value, double min, double max)
    {
        double inset = (max - min) * 0.003; // just inside the frame
        if (value < min)
            return (min + inset, -1);
        if (value > max)
            return (max - inset, 1);
        return (value, 0);
    }

    private static void PlaceVertical(VerticalLine line, double value, double min, double max, string name, bool labelled)
    {
        var (x, side) = ClampToView(value, min, max);
        line.X = x;
        line.LinePattern = side == 0 ? LinePattern.Solid : LinePattern.Dashed;
        if (labelled)
        {
            line.LabelText = side switch { < 0 => "← " + name, > 0 => name + " →", _ => name };
            line.LabelFontName = ScottPlot.Fonts.Detect(line.LabelText); // a font that has the arrows
        }
    }

    private static void PlaceHorizontal(HorizontalLine line, Text label, double value, double min, double max, string name)
    {
        var (y, side) = ClampToView(value, min, max);
        line.Y = y;
        line.LinePattern = side == 0 ? LinePattern.Solid : LinePattern.Dashed;
        label.LabelText = side switch { < 0 => "↓ " + name, > 0 => "↑ " + name, _ => name };
        label.LabelStyle.FontName = ScottPlot.Fonts.Detect(label.LabelText);
        // Off the bottom edge, the name goes above the line (else it would be cut).
        label.Alignment = side < 0 ? Alignment.LowerRight : side > 0 ? Alignment.UpperRight : Alignment.LowerRight;
    }

    /// <summary>'1' or '2' when the pixel is on Y1 or Y2 (within a few pixels).</summary>
    private char? MeasureYLineAt(Pixel pixel)
    {
        if (_yCursors is not { } y)
            return null;
        var plot = y.Graph.Plot;
        var r = plot.LastRender.DataRect;
        if (pixel.X < r.Left || pixel.X > r.Right)
            return null;
        float p1 = plot.GetPixel(new Coordinates(0, y.Line1.Y), plot.Axes.Bottom, y.Axis.Axis).Y;
        float p2 = plot.GetPixel(new Coordinates(0, y.Line2.Y), plot.Axes.Bottom, y.Axis.Axis).Y;
        float d1 = Math.Abs(pixel.Y - p1), d2 = Math.Abs(pixel.Y - p2);
        if (Math.Min(d1, d2) > 6 * _scale)
            return null;
        return d1 <= d2 ? '1' : '2';
    }

    private void DragMeasureY(MouseEventArgs e)
    {
        if (_yCursors is not { } y)
            return;
        var plot = y.Graph.Plot;
        double value = plot.GetCoordinates(ToPixel(e), plot.Axes.Bottom, y.Axis.Axis).Y;
        double y1 = _measureDrag == '1' ? value : _vm.MeasureY1 ?? value;
        double y2 = _measureDrag == '2' ? value : _vm.MeasureY2 ?? value;
        _vm.SetMeasureY(y.Graph.Graph, y.Axis.Key, y1, y2); // the lines follow before the render
        _view.Refresh();
    }

    /// <summary>Context menu: puts Y1 or Y2 where the menu was opened, on that graph (its measured axis, else its first axis).</summary>
    private void PlaceMeasureYAtMenu(Plot plot, char which)
    {
        var g = _graphs.FirstOrDefault(x => x.Plot == plot);
        if (g is null || g.Axes.Count == 0)
            return;
        var axis = g.Graph == _vm.MeasureYGraph && g.Axes.FirstOrDefault(a => a.Key == _vm.MeasureYAxisKey) is { } current
            ? current
            : g.Axes[0];
        double value = plot.GetCoordinates(_lastMenuPixel, plot.Axes.Bottom, axis.Axis).Y;
        bool sameAxis = g.Graph == _vm.MeasureYGraph && axis.Key == _vm.MeasureYAxisKey && _vm.MeasureY1 is not null;
        var r = axis.Axis.Range;
        double other1 = sameAxis ? _vm.MeasureY1!.Value : r.Min + (r.Max - r.Min) / 3;
        double other2 = sameAxis ? _vm.MeasureY2!.Value : r.Min + 2 * (r.Max - r.Min) / 3;
        _vm.SetMeasureY(g.Graph, axis.Key, which == '1' ? value : other1, which == '2' ? value : other2);
        if (!_vm.IsMeasuringY)
            _vm.IsMeasuringY = true; // draws the lines at the positions just set
        else
            UpdateMeasureYLines();
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
        else if (_annotationDrag is not null)
            DragAnnotation(e);

        var pixel = ToPixel(e);
        var lineUnder = _measureDrag ?? MeasureLineAt(pixel);
        var annotationUnder = lineUnder is null ? _annotationDrag ?? AnnotationAt(pixel) : null;
        _view.Cursor = lineUnder is '1' or '2' ? Cursors.SizeNS
            : lineUnder is not null ? Cursors.SizeWE
            : annotationUnder is { Part: AnnotationPart.NoteLabel } ? Cursors.SizeAll
            : annotationUnder is { } hit && IsDraggable(hit.Part) ? Cursors.SizeWE
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
        _vm.CursorXText = L.T("Survolez le graphe");
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
