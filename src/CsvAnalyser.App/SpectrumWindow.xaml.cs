using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CsvAnalyser.App.Plotting;
using CsvAnalyser.App.ViewModels;
using CsvAnalyser.Core.Analysis;
using CsvAnalyser.Core.Export;
using ScottPlot;
using Wpf.Ui.Controls;

namespace CsvAnalyser.App;

/// <summary>Amplitude spectrum (FFT) of some plotted curves over the visible range, the A–B range or everything.</summary>
public partial class SpectrumWindow : FluentWindow
{
    private enum SpectrumRange
    {
        Visible,
        BetweenCursors,
        All,
    }

    private readonly MainViewModel _vm;
    private readonly List<(System.Windows.Controls.CheckBox Box, ChannelItemViewModel Item)> _curves = [];
    private readonly bool _ready;
    private int _computeId;
    private float _scale = 1;

    // Axes: automatic (refitted at each computation) until the user zooms, pans or types a bound; then kept
    // when an option changes (except Y when switching dB on or off: other units).
    private bool _xAuto = true, _yAuto = true;
    private bool? _lastDecibel;
    private AxisLimits? _expected; // the limits as last set by this window, to notice the user's changes
    private readonly List<(double[] X, double[] Y)> _shown = [];
    private double _maxFrequency;
    private double _binWidth;
    private (IAxis Axis, Pixel Pixel)? _axisClick;
    private bool _axisPanGesture;

    public SpectrumWindow(MainViewModel vm, GraphViewModel? graph)
    {
        InitializeComponent();
        _vm = vm;
        // Its panels follow the interface size: the window starts larger too, so the plot keeps its room.
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width * Controls.UiScaling.Scale, area.Width - 20);
        Height = Math.Min(Height * Controls.UiScaling.Scale, area.Height - 20);

        var ranges = new List<StyleChoice<SpectrumRange>>();
        if (vm.PlotView?.XRange is not null)
            ranges.Add(new(SpectrumRange.Visible, "Plage visible du graphe"));
        if (vm.IsMeasuring && vm.RowsBetweenCursors is not null)
            ranges.Add(new(SpectrumRange.BetweenCursors, "Entre les curseurs X1 et X2"));
        ranges.Add(new(SpectrumRange.All, "Toutes les données"));
        RangeCombo.ItemsSource = ranges;
        RangeCombo.SelectedValue = vm.IsMeasuring && vm.RowsBetweenCursors is not null ? SpectrumRange.BetweenCursors : ranges[0].Value;

        WindowCombo.ItemsSource = new List<StyleChoice<FftWindow>>
        {
            new(FftWindow.Hann, "Hann (usage général)"),
            new(FftWindow.FlatTop, "Flat top (amplitudes précises)"),
            new(FftWindow.Rectangular, "Rectangulaire"),
        };
        WindowCombo.SelectedIndex = 0;

        var initial = graph is null
            ? vm.PlottedChannels.Take(4).ToList()
            : vm.PlottedChannels.Where(c => c.Graph == graph).ToList();
        foreach (var item in vm.PlottedChannels)
        {
            var box = new System.Windows.Controls.CheckBox
            {
                Margin = new Thickness(0, 0, 16, 4),
                IsChecked = initial.Contains(item),
                Content = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    Children =
                    {
                        new System.Windows.Shapes.Rectangle { Width = 10, Height = 10, Margin = new Thickness(0, 0, 6, 0), Fill = item.Brush, RadiusX = 2, RadiusY = 2 },
                        new System.Windows.Controls.TextBlock { Text = item.LegendText, VerticalAlignment = System.Windows.VerticalAlignment.Center },
                    },
                },
            };
            box.Checked += OnOptionChanged;
            box.Unchecked += OnOptionChanged;
            _curves.Add((box, item));
            CurveList.Items.Add(box);
        }

        PlotView.MouseMove += OnPlotMouseMove;
        // Same mouse buttons as the main graph (Paramètres), and the same image export.
        PlotTools.ApplyMouseButtons(PlotView, vm.LeftDragZooms);
        PlotView.UserInputProcessor.DoubleLeftClickBenchmark(false);
        PlotView.UserInputProcessor.UserActionResponses.RemoveAll(r => r is ScottPlot.Interactivity.UserActionResponses.SingleClickAutoscale);
        SetupMenu(null);
        PlotView.PreviewMouseDown += OnPlotMouseDown;
        PlotView.AddHandler(MouseUpEvent, new MouseButtonEventHandler(OnPlotMouseUp), handledEventsToo: true);
        PlotView.PreviewMouseWheel += (_, _) => Dispatcher.BeginInvoke(NoticeUserView, System.Windows.Threading.DispatcherPriority.Background);
        Loaded += (_, _) =>
        {
            _scale = (float)VisualTreeHelper.GetDpi(PlotView).DpiScaleX;
            Recompute();
        };
        _ready = true;
    }

    private bool IsTime => _vm.SelectedXAxis?.Channel is { } ch && ch == _vm.Dataset?.Time?.Channel;

    private string FrequencyUnit => IsTime ? "Hz" : L.T("cycles/unité X");

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_ready && IsLoaded)
            Recompute();
    }

    private async void Recompute()
    {
        int id = ++_computeId;
        var ds = _vm.Dataset;
        var option = _vm.SelectedXAxis;
        if (ds is null || option is null)
            return;
        if (!option.IsSorted)
        {
            ShowMessage(L.T("L'axe X doit être croissant pour calculer un spectre (choisissez le temps ou le n° de ligne)."));
            return;
        }

        var xs = option.GetValues(ds);
        ExportRows rows = (SpectrumRange)(RangeCombo.SelectedValue ?? SpectrumRange.All) switch
        {
            SpectrumRange.Visible when _vm.PlotView?.XRange is { } x => _vm.RowsInXRange(x.Min, x.Max),
            SpectrumRange.BetweenCursors when _vm.RowsBetweenCursors is { } r => r,
            _ => ExportRows.All(ds.RowCount),
        };
        var window = (FftWindow)(WindowCombo.SelectedValue ?? FftWindow.Hann);
        bool removeMean = RemoveMeanBox.IsChecked == true;
        bool decibel = DecibelBox.IsChecked == true;
        var selected = _curves.Where(c => c.Box.IsChecked == true).Select(c => c.Item).ToList();
        if (selected.Count == 0)
        {
            ShowMessage(L.T("Cochez au moins une courbe."));
            return;
        }

        StatusText.Text = L.T("Calcul…");
        List<(ChannelItemViewModel Item, SpectrumResult? Result, string? Error)> results;
        try
        {
            results = await Task.Run(() => selected.Select(item =>
            {
                try
                {
                    return (item, (SpectrumResult?)Spectrum.Compute(xs, item.Channel.Values, rows.Start, rows.End, window, removeMean), (string?)null);
                }
                catch (InvalidOperationException ex)
                {
                    return (item, null, ex.Message);
                }
            }).ToList());
        }
        catch (OutOfMemoryException)
        {
            ShowMessage(L.T("Plage trop grande pour la mémoire disponible : réduisez-la."));
            return;
        }
        if (id != _computeId)
            return; // a newer request is running

        var plot = PlotView.Plot;
        var keep = plot.Axes.GetLimits();
        plot.Clear();
        _shown.Clear();
        PeaksPanel.Children.Clear();
        PeaksUnitText.Text = L.F("Fréquence ({0}) — amplitude", FrequencyUnit);
        ApplyTheme(plot);
        var notes = new List<string>();
        var peaks = new List<(ChannelItemViewModel Item, IReadOnlyList<(double Frequency, double Amplitude)> List)>();
        SpectrumResult? first = null;
        foreach (var (item, result, error) in results)
        {
            if (result is null)
            {
                notes.Add($"{item.Name} : {error}");
                continue;
            }
            first ??= result;
            double[] ys = decibel
                ? result.Amplitudes.Select(a => 20 * Math.Log10(Math.Max(a, 1e-12))).ToArray()
                : result.Amplitudes;
            var line = plot.Add.SignalXY(result.Frequencies, ys, item.Color);
            line.LineWidth = 1.2f * _scale;
            line.LegendText = item.LegendText;
            _shown.Add((result.Frequencies, ys));
            peaks.Add((item, result.Peaks(PeakCount)));
        }
        // Peak marks last, so they are drawn over every curve.
        foreach (var (item, list) in peaks)
            AddPeaks(plot, item, list, decibel);

        plot.Axes.Bottom.Label.Text = L.F("Fréquence ({0})", FrequencyUnit);
        string unit = results.Select(r => r.Item.Unit).Distinct().Count() == 1 ? results[0].Item.Unit : "";
        plot.Axes.Left.Label.Text = decibel ? "Amplitude (dB)" : unit.Length > 0 ? $"Amplitude ({unit})" : "Amplitude";
        plot.Legend.IsVisible = first is not null;
        if (_lastDecibel is { } wasDecibel && wasDecibel != decibel)
            _yAuto = true; // other units: the old Y bounds mean nothing
        _lastDecibel = decibel;
        if (first is not null)
        {
            _maxFrequency = first.Frequencies[^1];
            _binWidth = first.Frequencies[1];
            if (_xAuto)
                FitX();
            else
                plot.Axes.SetLimitsX(keep.Left, keep.Right);
            if (_yAuto)
                FitY();
            else
                plot.Axes.SetLimitsY(keep.Bottom, keep.Top);
        }
        ViewChanged();

        if (first is not null)
        {
            StatusText.Text = L.F("{0:N0} points — échantillonnage {1} {2} — résolution {3} {2} — fréquence max {4} {2}",
                    first.SampleCount, Format(first.SampleRate), FrequencyUnit, Format(first.Frequencies[1]), Format(first.Frequencies[^1]))
                + (notes.Count > 0 ? " — " + string.Join(" ; ", notes) : "");
        }
        else
        {
            StatusText.Text = string.Join(" ; ", notes);
        }
    }

    private const int PeakCount = 5;

    /// <summary>Numbered marks on the curve and the list on the right: frequency and amplitude of each peak.</summary>
    private void AddPeaks(Plot plot, ChannelItemViewModel item, IReadOnlyList<(double Frequency, double Amplitude)> peaks, bool decibel)
    {
        float font = _scale * (float)_vm.PlotFontScale;
        var header = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 2) };
        header.Children.Add(new System.Windows.Shapes.Rectangle { Width = 10, Height = 10, Margin = new Thickness(0, 0, 6, 0), Fill = item.Brush, RadiusX = 2, RadiusY = 2 });
        header.Children.Add(new System.Windows.Controls.TextBlock { Text = item.LegendText, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 225 });
        PeaksPanel.Children.Add(header);
        if (peaks.Count == 0)
        {
            PeaksPanel.Children.Add(new System.Windows.Controls.TextBlock { Text = L.T("Aucun pic"), Margin = new Thickness(16, 0, 0, 0) });
            return;
        }
        for (int i = 0; i < peaks.Count; i++)
        {
            var (f, a) = peaks[i];
            double y = decibel ? 20 * Math.Log10(Math.Max(a, 1e-12)) : a;
            var marker = plot.Add.Marker(f, y, MarkerShape.OpenCircle, 9 * _scale, item.Color);
            marker.LegendText = "";
            var label = plot.Add.Text((i + 1).ToString(CultureInfo.CurrentCulture), f, y);
            label.Alignment = Alignment.LowerCenter;
            label.OffsetY = -6 * _scale;
            label.LabelStyle.FontSize = 11 * font;
            label.LabelStyle.Bold = true;
            label.LabelStyle.ForeColor = item.Color;

            string amplitude = decibel ? Format(y) + " dB" : (Format(a) + " " + item.Unit).TrimEnd();
            var row = new Grid
            {
                Margin = new Thickness(16, 0, 0, 1),
                Background = Brushes.Transparent, // clickable between the texts
                ToolTip = L.T("Clic : zoomer sur ce pic"),
                Cursor = Cursors.Hand,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(22) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
            };
            var number = new System.Windows.Controls.TextBlock { Text = (i + 1).ToString(CultureInfo.CurrentCulture) + ".", FontWeight = FontWeights.SemiBold };
            var frequencyText = new System.Windows.Controls.TextBlock { Text = Format(f), FontFamily = new FontFamily("Consolas") };
            var amplitudeText = new System.Windows.Controls.TextBlock { Text = amplitude, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(frequencyText, 1);
            Grid.SetColumn(amplitudeText, 2);
            row.Children.Add(number);
            row.Children.Add(frequencyText);
            row.Children.Add(amplitudeText);
            double frequency = f;
            row.PreviewMouseLeftButtonDown += (_, e) =>
            {
                ZoomOnPeak(frequency);
                e.Handled = true;
            };
            PeaksPanel.Children.Add(row);
        }
    }

    /// <summary>X around a peak (half its frequency on each side, at least 10 bins, never wider than now); X becomes manual.</summary>
    private void ZoomOnPeak(double frequency)
    {
        double half = Math.Min(PlotView.Plot.Axes.Bottom.Range.Span / 2, Math.Max(frequency * 0.5, 10 * _binWidth));
        double left = Math.Max(0, frequency - half);
        PlotView.Plot.Axes.SetLimitsX(left, left + 2 * half);
        _xAuto = false;
        if (_yAuto)
            FitY();
        ViewChanged();
    }

    // ---------------------------------------------------------------- axes

    private void FitX()
    {
        if (_maxFrequency > 0)
            PlotView.Plot.Axes.SetLimitsX(0, _maxFrequency);
        else
            PlotView.Plot.Axes.AutoScaleX();
    }

    /// <summary>Y fitted to the curves in the visible X range.</summary>
    private void FitY()
    {
        var x = PlotView.Plot.Axes.Bottom.Range;
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var (xs, ys) in _shown)
        {
            int i = Array.BinarySearch(xs, x.Min);
            for (i = i < 0 ? ~i : i; i < xs.Length && xs[i] <= x.Max; i++)
            {
                double v = ys[i];
                if (double.IsFinite(v))
                {
                    min = Math.Min(min, v);
                    max = Math.Max(max, v);
                }
            }
        }
        if (min > max)
            return;
        double margin = max > min ? (max - min) * 0.05 : Math.Max(Math.Abs(max) * 0.1, 1e-12);
        PlotView.Plot.Axes.SetLimitsY(min - margin, max + margin);
    }

    /// <summary>X and/or Y back to automatic; an automatic Y follows the visible X range.</summary>
    private void SetAuto(bool x, bool y)
    {
        if (x)
        {
            _xAuto = true;
            FitX();
        }
        if (y)
            _yAuto = true;
        if (_yAuto)
            FitY();
        ViewChanged();
    }

    /// <summary>The window changed the limits: redraw and remember them.</summary>
    private void ViewChanged()
    {
        PlotView.Refresh();
        _expected = PlotView.Plot.Axes.GetLimits();
    }

    /// <summary>After a mouse gesture: an axis whose limits changed is now kept as the user left it.</summary>
    private void NoticeUserView()
    {
        var now = PlotView.Plot.Axes.GetLimits();
        if (_expected is { } e)
        {
            if (now.Left != e.Left || now.Right != e.Right)
                _xAuto = false;
            if (now.Bottom != e.Bottom || now.Top != e.Top)
                _yAuto = false;
        }
        _expected = now;
    }

    private Pixel ToPixel(MouseEventArgs e)
    {
        var p = e.GetPosition(PlotView);
        return new Pixel((float)(p.X * PlotView.DisplayScale), (float)(p.Y * PlotView.DisplayScale));
    }

    /// <summary>The Y or X axis under a pixel outside the data area (else null).</summary>
    private IAxis? AxisAt(Pixel pixel)
    {
        var plot = PlotView.Plot;
        var r = plot.LastRender.DataRect;
        if (pixel.X >= r.Left && pixel.X <= r.Right && pixel.Y >= r.Top && pixel.Y <= r.Bottom)
            return null;
        var axis = plot.GetAxis(pixel);
        return axis == plot.Axes.Left || axis == plot.Axes.Bottom ? axis : null;
    }

    private void OnPlotMouseDown(object sender, MouseButtonEventArgs e)
    {
        _axisClick = null;
        switch (e.ChangedButton)
        {
            case MouseButton.Right:
                SetupMenu(AxisAt(ToPixel(e)));
                break;
            case MouseButton.Left when AxisAt(ToPixel(e)) is { } axis:
                _axisClick = (axis, ToPixel(e));
                if (_vm.LeftDragZooms)
                {
                    // Like the main graph: a left drag that starts on an axis moves that axis, even when left drag zooms.
                    PlotTools.ApplyMouseButtons(PlotView, leftZooms: false);
                    _axisPanGesture = true;
                }
                break;
        }
    }

    private void OnPlotMouseUp(object sender, MouseButtonEventArgs e)
    {
        var click = _axisClick;
        _axisClick = null;
        if (_axisPanGesture)
        {
            _axisPanGesture = false;
            Dispatcher.BeginInvoke(() => PlotTools.ApplyMouseButtons(PlotView, _vm.LeftDragZooms), System.Windows.Threading.DispatcherPriority.Input);
        }
        var pixel = ToPixel(e);
        var position = e.GetPosition(PlotView);
        var button = e.ChangedButton;
        // After ScottPlot has applied the gesture.
        Dispatcher.BeginInvoke(() =>
        {
            NoticeUserView();
            if (button == MouseButton.Left && click is { } c
                && Math.Abs(pixel.X - c.Pixel.X) <= 4 * _scale && Math.Abs(pixel.Y - c.Pixel.Y) <= 4 * _scale)
                OpenBoundEditor(c.Axis, c.Pixel, position);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Left click on an axis: type its minimum (lower/left half) or maximum (upper/right half).</summary>
    private void OpenBoundEditor(IAxis axis, Pixel pixel, Point position)
    {
        var plot = PlotView.Plot;
        var data = plot.LastRender.DataRect;
        bool isY = axis == plot.Axes.Left;
        bool max = isY ? pixel.Y < (data.Top + data.Bottom) / 2 : pixel.X > (data.Left + data.Right) / 2;
        var range = axis.Range;
        string name = isY ? L.T("Axe Y") : L.T("Axe X");
        PlotTools.ShowBoundEditor(PlotView, position, $"{name} — {(max ? L.T("maximum") : L.T("minimum"))}", max ? range.Max : range.Min, value =>
        {
            double min = max ? range.Min : value, top = max ? value : range.Max;
            if (!(min < top))
                return L.T("Le minimum doit rester inférieur au maximum.");
            if (isY)
            {
                plot.Axes.SetLimitsY(min, top);
                _yAuto = false;
            }
            else
            {
                plot.Axes.SetLimitsX(min, top);
                _xAuto = false;
                if (_yAuto)
                    FitY();
            }
            ViewChanged();
            return null;
        });
    }

    /// <summary>The plot menu, as on the main graph; right-clicked on an axis, it starts with that axis back to automatic.</summary>
    private void SetupMenu(IAxis? axis)
    {
        if (PlotView.Menu is not ScottPlot.WPF.WpfPlotMenu menu)
            return;
        menu.Clear();
        if (axis is not null && axis == PlotView.Plot.Axes.Left)
        {
            string label = PlotView.Plot.Axes.Left.Label.Text;
            // "_" is an access key in a menu header: doubled so it shows.
            menu.Add(L.F("Axe « {0} » en automatique", (string.IsNullOrEmpty(label) ? L.T("Axe Y") : label).Replace("_", "__")), _ => SetAuto(x: false, y: true));
            menu.AddSeparator();
        }
        else if (axis is not null && axis == PlotView.Plot.Axes.Bottom)
        {
            menu.Add(L.T("Axe X en automatique (tout afficher)"), _ => SetAuto(x: true, y: false));
            menu.AddSeparator();
        }
        menu.Add(L.T("Ajuster ce graphe en Y"), _ => SetAuto(x: false, y: true));
        menu.Add(L.T("Ajuster X"), _ => SetAuto(x: true, y: false));
        menu.Add(L.T("Tout ajuster (X et Y)"), _ => SetAuto(x: true, y: true));
        menu.AddSeparator();
        menu.Add(L.T("Copier l'image"), _ => PlotTools.CopyToClipboard(RenderImage(SkiaSharp.SKEncodedImageFormat.Png)));
        menu.Add(L.T("Enregistrer l'image…"), _ => PlotTools.SaveWithDialog(RenderImage,
            System.IO.Path.GetFileNameWithoutExtension(_vm.Dataset?.FilePath ?? "spectre") + " - " + L.T("spectre") + ".png"));
    }

    private byte[] RenderImage(SkiaSharp.SKEncodedImageFormat format)
    {
        var size = new PixelSize((float)(PlotView.ActualWidth * PlotView.DisplayScale), (float)(PlotView.ActualHeight * PlotView.DisplayScale));
        var bytes = PlotTools.RenderPlot(PlotView.Plot, size, _vm.ImageScale, format);
        PlotView.Refresh();
        return bytes;
    }

    private void ShowMessage(string text)
    {
        PlotView.Plot.Clear();
        _shown.Clear();
        PeaksPanel.Children.Clear();
        PlotView.Refresh();
        StatusText.Text = text;
    }

    private void ApplyTheme(Plot plot)
    {
        var t = _vm.SelectedTheme;
        var axis = ScottPlot.Color.FromHex(t.AxisColor);
        var figure = ScottPlot.Color.FromHex(t.FigureBackground);
        plot.FigureBackground.Color = figure;
        plot.DataBackground.Color = ScottPlot.Color.FromHex(t.DataBackground);
        plot.Axes.Color(axis);
        PlotTools.ApplyGrid(plot, t.DataBackground, t.AxisColor, _vm.Grid, _scale);
        plot.Legend.BackgroundColor = figure.WithAlpha(0.85);
        plot.Legend.FontColor = axis;
        plot.Legend.OutlineColor = ScottPlot.Color.FromHex(t.MajorGrid);
        float font = _scale * (float)_vm.PlotFontScale;
        plot.Legend.FontSize = 12 * font;
        foreach (var a in plot.Axes.GetAxes())
        {
            a.TickLabelStyle.FontSize = 12 * font;
            a.Label.FontSize = 14 * font;
        }
    }

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(PlotView);
        var pixel = new Pixel((float)(p.X * PlotView.DisplayScale), (float)(p.Y * PlotView.DisplayScale));
        var c = PlotView.Plot.GetCoordinates(pixel);
        MouseText.Text = L.F("f = {0} {1}   amplitude = {2}", Format(c.X), FrequencyUnit, Format(c.Y));
    }

    private static string Format(double v) => double.IsFinite(v) ? v.ToString("G4", CultureInfo.CurrentCulture) : "—";
}
