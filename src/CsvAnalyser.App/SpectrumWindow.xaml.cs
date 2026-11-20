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

    public SpectrumWindow(MainViewModel vm, GraphViewModel? graph)
    {
        InitializeComponent();
        _vm = vm;

        var ranges = new List<StyleChoice<SpectrumRange>>();
        if (vm.PlotView?.XRange is not null)
            ranges.Add(new(SpectrumRange.Visible, "Plage visible du graphe"));
        if (vm.IsMeasuring && vm.RowsBetweenCursors is not null)
            ranges.Add(new(SpectrumRange.BetweenCursors, "Entre les curseurs A et B"));
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
        if (PlotView.Menu is ScottPlot.WPF.WpfPlotMenu menu)
        {
            menu.Clear();
            menu.Add(L.T("Tout ajuster (X et Y)"), plot =>
            {
                plot.Axes.AutoScale();
                PlotView.Refresh();
            });
            menu.AddSeparator();
            menu.Add(L.T("Copier l'image"), _ => PlotTools.CopyToClipboard(RenderImage(SkiaSharp.SKEncodedImageFormat.Png)));
            menu.Add(L.T("Enregistrer l'image…"), _ => PlotTools.SaveWithDialog(RenderImage,
                System.IO.Path.GetFileNameWithoutExtension(vm.Dataset?.FilePath ?? "spectre") + " - " + L.T("spectre") + ".png"));
        }
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
        plot.Clear();
        ApplyTheme(plot);
        var notes = new List<string>();
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
            var (pf, pa) = result.Peak;
            line.LegendText = L.F("{0} — pic à {1} {2} : {3} {4}", item.LegendText, Format(pf), FrequencyUnit, Format(pa), item.Unit).TrimEnd();
        }

        plot.Axes.Bottom.Label.Text = L.F("Fréquence ({0})", FrequencyUnit);
        string unit = results.Select(r => r.Item.Unit).Distinct().Count() == 1 ? results[0].Item.Unit : "";
        plot.Axes.Left.Label.Text = decibel ? "Amplitude (dB)" : unit.Length > 0 ? $"Amplitude ({unit})" : "Amplitude";
        plot.Legend.IsVisible = first is not null;
        plot.Axes.AutoScale();
        if (first is not null)
            plot.Axes.SetLimitsX(0, first.Frequencies[^1]);
        PlotView.Refresh();

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
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex(t.MajorGrid);
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
