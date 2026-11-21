using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CsvAnalyser.App.ViewModels;
using ScottPlot;
using ScottPlot.Plottables;
using ScottPlot.WPF;

namespace CsvAnalyser.App.Plotting;

/// <summary>
/// Annotations on the plot: markers (vertical lines), zones (translucent bands) and notes (a circle on a curve and a
/// bubble). Marker and zone names sit at the top of the upper graph of each column, stacked on several rows when
/// they would overlap. Lines, zone edges and notes are dragged with the left button; the plot menu adds and edits them.
/// </summary>
public sealed partial class PlotController
{
    private enum AnnotationPart
    {
        Line,
        Start,
        End,
        Inside,
        NotePoint,
        NoteLabel,
    }

    private sealed record AnnotationLabel(AnnotationViewModel Annotation, GraphRender Graph, Text Text);

    private sealed record NoteRender(AnnotationViewModel Annotation, Series Series, Marker Marker, Text Text, LinePlot Leader);

    private readonly List<(Plot Plot, IPlottable Plottable)> _annotationPlottables = [];
    private readonly List<AnnotationLabel> _annotationLabels = [];
    private readonly List<NoteRender> _notes = [];
    private (AnnotationViewModel Annotation, AnnotationPart Part)? _annotationDrag;

    // Dragging a note: the bubble's lower-left corner minus the mouse (bubble drag), or where the bubble stays (point drag).
    private Pixel _noteGrab;

    // Each bubble's anchor at the last render, and each note's bubble rectangle relative to its anchor (see PlaceNoteArrows).
    private readonly Dictionary<Text, Pixel> _noteAnchors = [];
    private readonly Dictionary<AnnotationViewModel, PixelRect> _noteBoxes = [];

    private const int MaxLabelLength = 40;

    /// <summary>Recreates the annotation plottables (after each change; cheap, there are few).</summary>
    private void UpdateAnnotations()
    {
        foreach (var (plot, plottable) in _annotationPlottables)
            plot.Remove(plottable);
        _annotationPlottables.Clear();
        _annotationLabels.Clear();
        _notes.Clear();
        _noteAnchors.Clear(); // those bubbles are gone (their sizes stay known in _noteBoxes)
        if (!_vm.ShowAnnotations || _xs is null || _graphs.Count == 0)
            return;

        foreach (var a in _vm.Annotations.Where(a => a.IsVisible))
        {
            if (a.Row < 0 || a.Row >= _xs.Length)
                continue;
            var color = ScottPlot.Color.FromHex(a.Color);
            if (a.Kind == AnnotationKind.Note)
            {
                AddNote(a, color);
                continue;
            }

            // A marker or zone is drawn in every graph, or in its own graph only (every graph when that one is gone).
            var only = a.Graph is { } chosen ? _graphs.FirstOrDefault(g => g.Graph == chosen) : null;
            IEnumerable<GraphRender> targets = only is not null ? [only] : _graphs;
            double x = _xs[a.Row];
            double x2 = a.Kind == AnnotationKind.Zone && a.EndRow >= 0 && a.EndRow < _xs.Length ? _xs[a.EndRow] : x;
            foreach (var g in targets)
            {
                IPlottable shape;
                if (a.Kind == AnnotationKind.Zone)
                {
                    var span = g.Plot.Add.HorizontalSpan(Math.Min(x, x2), Math.Max(x, x2));
                    span.FillColor = color.WithAlpha(a.Opacity);
                    span.LineColor = color.WithAlpha(0.8);
                    span.LineWidth = (float)a.LineWidth * _scale;
                    span.LinePattern = Pattern(a.Line);
                    span.EnableAutoscale = false;
                    span.LegendText = "";
                    shape = span;
                }
                else
                {
                    var line = g.Plot.Add.VerticalLine(x);
                    line.Color = color;
                    line.LineWidth = (float)a.LineWidth * _scale;
                    line.LinePattern = Pattern(a.Line);
                    line.EnableAutoscale = false;
                    line.ExcludeFromLegend = true;
                    shape = line;
                }
                // In front of the curves: translucent, so a zone stays visible even over a dense signal.
                _annotationPlottables.Add((g.Plot, shape));
            }

            // The name: at the top of the upper graph of each column (or of its own graph).
            IEnumerable<GraphRender> labelled = only is not null ? [only] : _graphs.Where(g => g.IsTop);
            foreach (var g in labelled)
            {
                var text = g.Plot.Add.Text(Shorten(a.ListText), x, 0);
                text.Axes.YAxis = g.Plot.Axes.Left;
                text.Alignment = Alignment.UpperCenter;
                text.LabelStyle.FontSize = (float)a.FontSize * FontScale;
                text.LabelStyle.BackgroundColor = color;
                text.LabelStyle.ForeColor = ReadableOn(color);
                text.LabelStyle.Padding = 3 * _scale;
                text.LabelStyle.BorderRadius = 3 * _scale;
                _annotationPlottables.Add((g.Plot, text));
                _annotationLabels.Add(new AnnotationLabel(a, g, text));
            }
        }
        PlaceAnnotationLabels();
        // The line from a bubble to its point needs the bubble's real size, known only after it is drawn once.
        if (_notes.Count > 0)
            _view.Dispatcher.BeginInvoke(_view.Refresh, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void AddNote(AnnotationViewModel a, ScottPlot.Color color)
    {
        if (_series.FirstOrDefault(s => s.Item == a.Curve) is not { } series || a.Row >= series.Ys.Length)
            return; // its curve is not plotted
        double x = _xs![a.Row], y = series.Ys[a.Row];
        if (!double.IsFinite(y))
            return;
        var plot = series.Graph.Plot;
        var marker = plot.Add.Marker(x, y, MarkerShape.OpenCircle, (float)(a.FontSize * 0.8) * _scale, color);
        marker.Axes.YAxis = series.Axis;
        marker.LineWidth = (float)a.LineWidth * _scale;
        marker.LegendText = "";

        var text = plot.Add.Text(a.Text.Length > 0 ? a.Text : a.KindLabel, x, y);
        text.Axes.YAxis = series.Axis;
        text.Alignment = Alignment.LowerLeft;
        text.OffsetX = (float)a.LabelOffsetX * _scale;
        text.OffsetY = (float)a.LabelOffsetY * _scale;
        text.LabelStyle.FontSize = (float)a.FontSize * FontScale;
        text.LabelStyle.ForeColor = ScottPlot.Color.FromHex(_vm.SelectedTheme.AxisColor);
        text.LabelStyle.BackgroundColor = ScottPlot.Color.FromHex(_vm.SelectedTheme.DataBackground).WithAlpha(0.92);
        text.LabelStyle.BorderColor = color;
        text.LabelStyle.BorderWidth = (float)Math.Max(1, a.LineWidth) * _scale;
        text.LabelStyle.BorderRadius = 4 * _scale;
        text.LabelStyle.Padding = 4 * _scale;
        // A thin line from the bubble's edge to the circle when the bubble was dragged away (placed before each render).
        var leader = plot.Add.Line(x, y, x, y);
        leader.Axes.YAxis = series.Axis;
        leader.LineColor = color;
        leader.LineWidth = (float)Math.Max(1, a.LineWidth) * _scale;
        leader.MarkerSize = 0;
        leader.IsVisible = false;
        leader.LegendText = "";
        _annotationPlottables.Add((plot, leader));
        _annotationPlottables.Add((plot, marker));
        _annotationPlottables.Add((plot, text));
        _notes.Add(new NoteRender(a, series, marker, text, leader));
    }

    private static LinePattern Pattern(CurveLine line) => line switch
    {
        CurveLine.Dashed => LinePattern.Dashed,
        CurveLine.DenselyDashed => LinePattern.DenselyDashed,
        CurveLine.Dotted => LinePattern.Dotted,
        _ => LinePattern.Solid,
    };

    /// <summary>A name inside a menu entry: "_" would be taken as an access key (and disappear), so it is doubled.</summary>
    private static string MenuText(string text) => Shorten(text).Replace("_", "__");

    private static string Shorten(string text) => text.Length <= MaxLabelLength ? text : text[..(MaxLabelLength - 1)] + "…";

    /// <summary>White text on dark colors, black on light ones.</summary>
    private static ScottPlot.Color ReadableOn(ScottPlot.Color c) =>
        0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 160 ? ScottPlot.Colors.Black : ScottPlot.Colors.White;

    /// <summary>
    /// Before each render: each name at the top of its graph, centered on its marker or on the visible part of its
    /// zone, hidden when outside the view; names that would overlap go on the next row down.
    /// </summary>
    private void PlaceAnnotationLabels()
    {
        PlaceNoteArrows();
        foreach (var group in _annotationLabels.GroupBy(l => l.Graph))
        {
            var plot = group.Key.Plot;
            var x = plot.Axes.Bottom.Range;
            var y = plot.Axes.Left.Range;
            var placed = new List<(AnnotationLabel Label, float Pixel, float Width)>();
            foreach (var label in group)
            {
                var a = label.Annotation;
                double x1 = _xs![a.Row];
                double x2 = a.Kind == AnnotationKind.Zone && a.EndRow < _xs.Length ? _xs[a.EndRow] : x1;
                double left = Math.Max(Math.Min(x1, x2), x.Min), right = Math.Min(Math.Max(x1, x2), x.Max);
                label.Text.IsVisible = left <= right;
                if (!label.Text.IsVisible)
                    continue;
                double center = (left + right) / 2;
                label.Text.Location = new Coordinates(center, y.Max);
                float pixel = plot.GetPixel(new Coordinates(center, y.Max), plot.Axes.Bottom, plot.Axes.Left).X;
                float width = label.Text.LabelText.Length * label.Text.LabelStyle.FontSize * 0.6f + 8 * _scale;
                placed.Add((label, pixel, width));
            }

            // Greedy rows: each name goes on the first row where it does not touch the previous one.
            var rowEnds = new List<float>();
            foreach (var (label, pixel, width) in placed.OrderBy(p => p.Pixel))
            {
                float start = pixel - width / 2;
                int row = rowEnds.FindIndex(end => start > end + 4 * _scale);
                if (row < 0)
                {
                    rowEnds.Add(0);
                    row = rowEnds.Count - 1;
                }
                rowEnds[row] = pixel + width / 2;
                label.Text.OffsetY = 3 * _scale + row * (label.Text.LabelStyle.FontSize * 1.5f + 6 * _scale);
            }
        }
    }

    /// <summary>Pixel of a note's point.</summary>
    private static Pixel NotePixel(NoteRender note)
    {
        var plot = note.Series.Graph.Plot;
        return plot.GetPixel(note.Marker.Location, plot.Axes.Bottom, note.Series.Axis);
    }

    /// <summary>Before each render: a line from the bubble's nearest edge to the edge of the circle, when they are apart.</summary>
    private void PlaceNoteArrows()
    {
        foreach (var note in _notes)
        {
            var plot = note.Series.Graph.Plot;
            var p = NotePixel(note);
            var anchor = new Pixel(p.X + note.Text.OffsetX, p.Y + note.Text.OffsetY);

            // The bubble as really drawn (padding and border included), relative to its anchor: learned from the
            // last render of this same bubble and kept per note, since a drag recreates the bubble at every move.
            var last = note.Text.LabelLastRenderPixelRect;
            if (last.Width > 0 && _noteAnchors.TryGetValue(note.Text, out var lastAnchor))
                _noteBoxes[note.Annotation] = new PixelRect(last.Left - lastAnchor.X, last.Right - lastAnchor.X, last.Bottom - lastAnchor.Y, last.Top - lastAnchor.Y);
            _noteAnchors[note.Text] = anchor;
            var box = _noteBoxes.TryGetValue(note.Annotation, out var known)
                ? new PixelRect(anchor.X + known.Left, anchor.X + known.Right, anchor.Y + known.Bottom, anchor.Y + known.Top)
                : EstimatedBubble(note, anchor);

            // From just outside the bubble's edge to the edge of the circle.
            float gap = 1 * _scale + note.Leader.LineWidth / 2; // the line's thickness stays outside too
            var q = new Pixel(Math.Clamp(p.X, box.Left - gap, box.Right + gap), Math.Clamp(p.Y, box.Top - gap, box.Bottom + gap));
            float dx = p.X - q.X, dy = p.Y - q.Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            float radius = note.Marker.MarkerSize / 2;
            note.Leader.IsVisible = distance > radius + 3 * _scale;
            if (!note.Leader.IsVisible)
                continue;
            var end = new Pixel(p.X - dx / distance * radius, p.Y - dy / distance * radius); // stops at the circle
            note.Leader.Start = plot.GetCoordinates(q, plot.Axes.Bottom, note.Series.Axis);
            note.Leader.End = plot.GetCoordinates(end, plot.Axes.Bottom, note.Series.Axis);
        }
    }

    /// <summary>A note's bubble before it was ever drawn: from its text length and font size.</summary>
    private PixelRect EstimatedBubble(NoteRender note, Pixel anchor)
    {
        var style = note.Text.LabelStyle;
        float width = note.Text.LabelText.Length * style.FontSize * 0.6f + style.PixelPadding.Horizontal;
        float height = style.FontSize * 1.3f + style.PixelPadding.Vertical;
        return new PixelRect(anchor.X, anchor.X + width, anchor.Y, anchor.Y - height);
    }

    // ---------------------------------------------------------------- finding and dragging

    /// <summary>The annotation under a pixel: a note, a marker line or its name, a zone edge, the inside of a zone.</summary>
    private (AnnotationViewModel Annotation, AnnotationPart Part)? AnnotationAt(Pixel pixel)
    {
        if (_xs is null || !_vm.ShowAnnotations)
            return null;
        float tolerance = 6 * _scale;

        // The point first (the bubble may cover it), then the bubble.
        foreach (var note in _notes)
        {
            var p = NotePixel(note);
            if (Math.Abs(p.X - pixel.X) <= 1.5f * tolerance && Math.Abs(p.Y - pixel.Y) <= 1.5f * tolerance)
                return (note.Annotation, AnnotationPart.NotePoint);
        }
        foreach (var note in _notes)
        {
            if (note.Text.LabelLastRenderPixelRect.Contains(pixel))
                return (note.Annotation, AnnotationPart.NoteLabel);
        }
        foreach (var label in _annotationLabels.Where(l => l.Text.IsVisible))
        {
            if (label.Text.LabelLastRenderPixelRect.Contains(pixel))
                return (label.Annotation, label.Annotation.Kind == AnnotationKind.Zone ? AnnotationPart.Inside : AnnotationPart.Line);
        }

        var g = _graphs.FirstOrDefault(g =>
        {
            var r = g.Plot.LastRender.DataRect;
            return pixel.X >= r.Left && pixel.X <= r.Right && pixel.Y >= r.Top && pixel.Y <= r.Bottom;
        });
        if (g is null)
            return null;
        float PixelOf(int row) => g.Plot.GetPixel(new Coordinates(_xs[row], 0), g.Plot.Axes.Bottom, g.Plot.Axes.Left).X;
        bool Shown(AnnotationViewModel a) =>
            a.IsVisible && a.Row >= 0 && a.Row < _xs.Length && (a.Graph is null || a.Graph == g.Graph || _graphs.All(x => x.Graph != a.Graph));

        (AnnotationViewModel, AnnotationPart)? best = null;
        float bestDistance = tolerance;
        foreach (var a in _vm.Annotations.Where(a => a.Kind != AnnotationKind.Note && Shown(a)))
        {
            float d = Math.Abs(PixelOf(a.Row) - pixel.X);
            if (d <= bestDistance)
            {
                bestDistance = d;
                best = (a, a.Kind == AnnotationKind.Zone ? (a.Row <= a.EndRow ? AnnotationPart.Start : AnnotationPart.End) : AnnotationPart.Line);
            }
            if (a.Kind == AnnotationKind.Zone && a.EndRow < _xs.Length)
            {
                float d2 = Math.Abs(PixelOf(a.EndRow) - pixel.X);
                if (d2 <= bestDistance)
                {
                    bestDistance = d2;
                    best = (a, a.Row <= a.EndRow ? AnnotationPart.End : AnnotationPart.Start);
                }
            }
        }
        if (best is { } found)
            return found;

        // Inside a zone (the narrowest one when they overlap): for the menu only.
        return _vm.Annotations
            .Where(a => a.Kind == AnnotationKind.Zone && Shown(a) && a.EndRow < _xs.Length)
            .Select(a => (A: a, P1: PixelOf(a.Row), P2: PixelOf(a.EndRow)))
            .Where(z => pixel.X >= Math.Min(z.P1, z.P2) && pixel.X <= Math.Max(z.P1, z.P2))
            .OrderBy(z => Math.Abs(z.P2 - z.P1))
            .Select(z => ((AnnotationViewModel, AnnotationPart)?)(z.A, AnnotationPart.Inside))
            .FirstOrDefault();
    }

    private static bool IsDraggable(AnnotationPart part) => part != AnnotationPart.Inside;

    private bool TryStartAnnotationDrag(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || AnnotationAt(ToPixel(e)) is not { } hit || !IsDraggable(hit.Part))
            return false;
        _annotationDrag = hit;
        if (_notes.FirstOrDefault(n => n.Annotation == hit.Annotation) is { } note)
        {
            var p = NotePixel(note);
            var corner = new Pixel(p.X + note.Text.OffsetX, p.Y + note.Text.OffsetY);
            var mouse = ToPixel(e);
            // Bubble: keep the grab point under the mouse. Point: the bubble stays where it is on screen.
            _noteGrab = hit.Part == AnnotationPart.NoteLabel ? new Pixel(corner.X - mouse.X, corner.Y - mouse.Y) : corner;
        }
        _view.CaptureMouse();
        e.Handled = true; // no pan while moving an annotation
        return true;
    }

    private void DragAnnotation(MouseEventArgs e)
    {
        if (_annotationDrag is not { } drag || _xs is null)
            return;
        var a = drag.Annotation;
        var mouse = ToPixel(e);
        if (drag.Part == AnnotationPart.NoteLabel)
        {
            if (_notes.FirstOrDefault(n => n.Annotation == a) is { } dragged)
            {
                var p = NotePixel(dragged);
                (a.LabelOffsetX, a.LabelOffsetY) = ((mouse.X + _noteGrab.X - p.X) / _scale, (mouse.Y + _noteGrab.Y - p.Y) / _scale);
            }
            return;
        }
        var plot = _graphs[0].Plot;
        double x = plot.GetCoordinates(mouse, plot.Axes.Bottom, plot.Axes.Left).X;
        int row = _xSorted ? NearestSorted(_xs, x) : NearestUnsorted(_xs, x);
        if (row < 0)
            return;
        if (drag.Part == AnnotationPart.NotePoint)
        {
            a.Row = row; // redrawn: the point moves along its curve
            if (_notes.FirstOrDefault(n => n.Annotation == a) is { } moved)
            {
                var p = NotePixel(moved); // the bubble stays put: its offset follows the point
                (a.LabelOffsetX, a.LabelOffsetY) = ((_noteGrab.X - p.X) / _scale, (_noteGrab.Y - p.Y) / _scale);
            }
            return;
        }
        // Zone ends: the dragged edge moves (Row is the first edge, EndRow the second, whatever their order).
        if (drag.Part == AnnotationPart.End)
            a.EndRow = row;
        else
            a.Row = row; // redrawn through PlotChange.Annotations
    }

    private void EndAnnotationDrag()
    {
        if (_annotationDrag is { Annotation: { Kind: AnnotationKind.Zone } zone } && zone.Row > zone.EndRow)
            (zone.Row, zone.EndRow) = (zone.EndRow, zone.Row);
        _annotationDrag = null;
        _view.ReleaseMouseCapture();
    }

    // ---------------------------------------------------------------- plot menu

    /// <summary>Menu entries for the annotation under the mouse, then to add one at the mouse.</summary>
    private void AddAnnotationMenuItems(WpfPlotMenu menu)
    {
        if (_xs is null || _series.Count == 0)
            return;
        if (AnnotationAt(_lastMenuPixel) is { } hit)
        {
            var a = hit.Annotation;
            string name = MenuText(a.ListText);
            menu.Add(L.F("Modifier « {0} »…", name), _ => _vm.EditAnnotationCommand.Execute(a));
            if (a.Kind == AnnotationKind.Zone)
                menu.Add(L.T("Mesurer cette zone (curseurs X1 et X2)"), _ => _vm.MeasureZoneCommand.Execute(a));
            menu.Add(L.F("Supprimer « {0} »", name), _ => _vm.DeleteAnnotation(a));
            menu.AddSeparator();
        }

        menu.Add(L.T("Ajouter un repère ici"), plot => AddAnnotationAtMenu(plot, AnnotationKind.Marker, null));
        if (NearestSeries(_lastMenuPixel) is { } series)
            menu.Add(L.F("Ajouter une note sur « {0} »", MenuText(series.Item.Name)), plot => AddAnnotationAtMenu(plot, AnnotationKind.Note, series.Item));
        menu.Add(L.T("Surligner une zone ici"), plot => AddAnnotationAtMenu(plot, AnnotationKind.Zone, null));
        menu.AddSeparator();
    }

    /// <summary>The curve of the graph under the pixel whose point at that X is nearest vertically.</summary>
    private Series? NearestSeries(Pixel pixel)
    {
        var g = _graphs.FirstOrDefault(g =>
        {
            var r = g.Plot.LastRender.DataRect;
            return pixel.X >= r.Left && pixel.X <= r.Right && pixel.Y >= r.Top && pixel.Y <= r.Bottom;
        });
        if (g is null || _xs is null)
            return null;
        double x = g.Plot.GetCoordinates(pixel, g.Plot.Axes.Bottom, g.Plot.Axes.Left).X;
        int row = _xSorted ? NearestSorted(_xs, x) : NearestUnsorted(_xs, x);
        if (row < 0)
            return null;
        return _series.Where(s => s.Graph == g && row < s.Ys.Length && double.IsFinite(s.Ys[row]))
            .OrderBy(s => Math.Abs(g.Plot.GetPixel(new Coordinates(_xs[row], s.Ys[row]), g.Plot.Axes.Bottom, s.Axis).Y - pixel.Y))
            .FirstOrDefault();
    }

    private void AddAnnotationAtMenu(Plot plot, AnnotationKind kind, ChannelItemViewModel? curve)
    {
        if (_xs is null)
            return;
        double x = plot.GetCoordinates(_lastMenuPixel, plot.Axes.Bottom, plot.Axes.Left).X;
        int Row(double value) => _xSorted ? NearestSorted(_xs, value) : NearestUnsorted(_xs, value);
        int row = Row(x);
        if (row < 0)
            return;
        AnnotationViewModel a;
        switch (kind)
        {
            case AnnotationKind.Zone:
                // A tenth of the view around the click; the edges are then dragged where wanted.
                double half = plot.Axes.Bottom.Range.Span / 20;
                int start = Row(x - half), end = Row(x + half);
                if (end == start)
                    end = Math.Min(_xs.Length - 1, start + 1);
                a = _vm.AddZone(start, end);
                break;
            case AnnotationKind.Note when curve is not null:
                a = _vm.AddNote(curve, row);
                break;
            default:
                a = _vm.AddMarker(row);
                break;
        }
        var position = new System.Windows.Point(_lastMenuPixel.X / _view.DisplayScale, _lastMenuPixel.Y / _view.DisplayScale);
        ShowAnnotationEditor(a, position, isNew: true);
    }

    /// <summary>
    /// A small box at the mouse to name a new annotation and pick its color: Entrée keeps it, Échap removes it,
    /// "Réglages…" opens every setting (size, line, opacity, graph).
    /// </summary>
    private void ShowAnnotationEditor(AnnotationViewModel a, System.Windows.Point position, bool isNew)
    {
        var box = new Wpf.Ui.Controls.TextBox { MinWidth = 220, Text = a.Text };
        var header = new System.Windows.Controls.TextBlock { Text = a.KindLabel, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        header.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "AppMenuForeground");
        var swatches = new System.Windows.Controls.WrapPanel { Margin = new Thickness(0, 8, 0, 0), MaxWidth = 240 };
        foreach (string hex in AnnotationViewModel.Swatches)
        {
            var c = ScottPlot.Color.FromHex(hex);
            var swatch = new System.Windows.Controls.Border
            {
                Width = 18, Height = 18, Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B)),
                Cursor = Cursors.Hand, ToolTip = hex,
            };
            swatch.MouseLeftButtonDown += (_, _) => a.Color = hex;
            swatches.Children.Add(swatch);
        }
        var more = new Wpf.Ui.Controls.Button { Content = L.T("Réglages…"), Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        var hint = new System.Windows.Controls.TextBlock { Text = L.T("Entrée : valider — Échap : annuler"), FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
        hint.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "AppSecondaryTextBrush");
        var border = new System.Windows.Controls.Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = new System.Windows.Controls.StackPanel { Children = { header, box, swatches, more, hint } },
        };
        border.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "AppMenuBackground");
        border.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "AppMenuBorder");
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            Child = border,
            PlacementTarget = _view,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
            HorizontalOffset = position.X + 8,
            VerticalOffset = position.Y + 8,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        bool cancelled = false;
        void Apply()
        {
            if (!cancelled && box.Text.Trim().Length > 0 && box.Text.Trim() != a.Text)
                a.Text = box.Text.Trim();
        }
        box.KeyDown += (_, k) =>
        {
            if (k.Key == Key.Escape)
            {
                cancelled = true;
                popup.IsOpen = false;
                if (isNew)
                    _vm.DeleteAnnotation(a);
                k.Handled = true;
            }
            else if (k.Key == Key.Enter)
            {
                k.Handled = true;
                popup.IsOpen = false;
            }
        };
        more.Click += (_, _) =>
        {
            popup.IsOpen = false;
            _vm.EditAnnotationCommand.Execute(a);
        };
        popup.Closed += (_, _) => Apply(); // a click elsewhere keeps what was typed
        popup.IsOpen = true;
        _view.Dispatcher.BeginInvoke(() =>
        {
            Keyboard.Focus(box);
            box.SelectAll();
        }, DispatcherPriority.Input);
    }
}
