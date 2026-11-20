using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CsvAnalyser.App.ViewModels;
using Wpf.Ui.Controls;

namespace CsvAnalyser.App;

/// <summary>
/// Graphs window: add, reorder and remove graphs, number of columns, which curve goes in which graph,
/// title/legend/zero alignment, and auto/manual range of every Y axis. Changes apply live.
/// </summary>
public partial class AxesWindow : FluentWindow
{
    private readonly MainViewModel _vm;
    private bool _refreshPending;

    public AxesWindow(MainViewModel viewModel, GraphViewModel? focus)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel; // the columns combo
        viewModel.BeginGraphEditing();
        Rebuild();

        viewModel.PlotChanged += OnPlotChanged;
        Closed += (_, _) =>
        {
            viewModel.PlotChanged -= OnPlotChanged;
            viewModel.EndGraphEditing();
        };

        if (focus is not null && viewModel.Graphs.IndexOf(focus) is > 0 and var index)
        {
            Loaded += (_, _) =>
            {
                if (GraphList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement item)
                    item.BringIntoView();
            };
        }
    }

    /// <summary>The cards are rebuilt after a change of the graphs or curves (once, after the current event).</summary>
    private void OnPlotChanged(object? sender, PlotChange change)
    {
        if (change is not (PlotChange.Curves or PlotChange.Appearance or PlotChange.Dataset or PlotChange.AxisSettings or PlotChange.Layout) || _refreshPending)
            return;
        _refreshPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _refreshPending = false;
            double offset = Scroller.VerticalOffset;
            Rebuild();
            Dispatcher.BeginInvoke(() => Scroller.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
        }, DispatcherPriority.Background);
    }

    private void Rebuild()
    {
        var layout = _vm.BuildLayout();
        var choices = _vm.Graphs.Select(g => new GraphChoice(g,
            string.IsNullOrWhiteSpace(g.Name) ? $"Graphe {g.Number}" : $"Graphe {g.Number} — {g.Name.Trim()}")).ToList();
        choices.Add(new GraphChoice(null, "＋ Nouveau graphe"));

        var groups = new List<GraphAxesViewModel>();
        for (int i = 0; i < layout.Count; i++)
        {
            var spec = layout[i];
            // In drawing order: the last curve is drawn on top.
            var ordered = spec.Curves.OrderBy(c => _vm.PlottedChannels.IndexOf(c)).ToList();
            var curves = ordered
                .Select((c, k) => new GraphCurveRow(c, GraphLayout.AxisChoices(c, layout), choices, MoveCurve, k > 0, k < ordered.Count - 1))
                .ToList();
            var axes = spec.Axes.Select(a =>
            {
                // Switching to manual starts from what is on screen.
                if (_vm.CurrentAxisRange?.Invoke(spec.Graph, a.Key) is { } range)
                    a.Settings.PrefillIfEmpty(range.Min, range.Max);
                return new AxisRowViewModel(a);
            }).ToList();
            groups.Add(new GraphAxesViewModel(spec.Graph, curves, axes, _vm.Channels, i, layout.Count));
        }
        GraphList.ItemsSource = groups;
    }

    private void MoveCurve(ChannelItemViewModel item, GraphViewModel? target)
    {
        // After the combo has finished its own update.
        Dispatcher.BeginInvoke(() =>
        {
            _vm.MoveChannelToGraph(item, target);
            if (target is null && _vm.Graphs.LastOrDefault() is { } created)
                created.KeepWhenEmpty = true;
        }, DispatcherPriority.Background);
    }

    private static GraphAxesViewModel? GroupOf(object sender) => (sender as FrameworkElement)?.DataContext as GraphAxesViewModel;

    private void OnAddGraph(object sender, RoutedEventArgs e)
    {
        _vm.AddEmptyGraph();
        Dispatcher.BeginInvoke(() => Scroller.ScrollToEnd(), DispatcherPriority.ContextIdle);
    }

    private void OnMoveUp(object sender, RoutedEventArgs e)
    {
        if (GroupOf(sender) is { } g)
            _vm.MoveGraph(g.Graph, -1);
    }

    private void OnMoveDown(object sender, RoutedEventArgs e)
    {
        if (GroupOf(sender) is { } g)
            _vm.MoveGraph(g.Graph, +1);
    }

    private void OnRemoveGraph(object sender, RoutedEventArgs e)
    {
        if (GroupOf(sender) is not { } g)
            return;
        if (!g.IsEmpty && System.Windows.MessageBox.Show(
                $"Supprimer {g.Graph.Title} ? Ses {g.Curves.Count} courbe(s) seront masquées.", "Supprimer le graphe",
                System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.OK)
            return;
        _vm.RemoveGraph(g.Graph);
    }

    private void OnAddCurve(object? sender, object picked)
    {
        if (GroupOf(sender!) is { } g && picked is ChannelItemViewModel item)
            _vm.MoveChannelToGraph(item, g.Graph);
    }

    private void OnHideCurve(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GraphCurveRow row)
            row.Item.IsPlotted = false;
    }

    private static ChannelItemViewModel? CurveOf(object sender) => ((sender as FrameworkElement)?.DataContext as GraphCurveRow)?.Item;

    private void OnCurveUp(object sender, RoutedEventArgs e)
    {
        if (CurveOf(sender) is { } item)
            _vm.MoveCurveInGraph(item, -1);
    }

    private void OnCurveDown(object sender, RoutedEventArgs e)
    {
        if (CurveOf(sender) is { } item)
            _vm.MoveCurveInGraph(item, +1);
    }

    private void OnCurveSettings(object sender, RoutedEventArgs e)
    {
        if (CurveOf(sender) is { } item)
            new CurveSettingsWindow(item, GraphLayout.AxisChoices(item, _vm.BuildLayout())) { Owner = this }.ShowDialog();
    }

    /// <summary>A small palette under the color square: one click picks a color, "Automatique" follows the theme.</summary>
    private void OnColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor || CurveOf(sender) is not { } item)
            return;
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        var swatches = new System.Windows.Controls.WrapPanel { Width = 10 * 28 };
        foreach (var hex in CurveStyleChoices.Swatches)
        {
            var swatch = new System.Windows.Controls.Button
            {
                Style = (Style)FindResource("ColorButton"),
                Margin = new Thickness(0, 0, 6, 6),
                Background = new System.Windows.Media.BrushConverter().ConvertFromString(hex) as System.Windows.Media.Brush,
                ToolTip = hex,
            };
            swatch.Click += (_, _) =>
            {
                popup.IsOpen = false;
                item.SetColorCommand.Execute(hex);
            };
            swatches.Children.Add(swatch);
        }
        var auto = new Wpf.Ui.Controls.Button { Content = "Automatique (suit le thème)", Margin = new Thickness(0, 2, 0, 0) };
        auto.Click += (_, _) =>
        {
            popup.IsOpen = false;
            item.UseAutoColorCommand.Execute(null);
        };
        var border = new System.Windows.Controls.Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = new System.Windows.Controls.StackPanel { Children = { swatches, auto } },
        };
        border.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "AppMenuBackground");
        border.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "AppMenuBorder");
        popup.Child = border;
        popup.IsOpen = true;
    }

    private void OnBoundKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is System.Windows.Controls.TextBox box)
            box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
