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
        if (change is not (PlotChange.Curves or PlotChange.Appearance or PlotChange.Dataset) || _refreshPending)
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
            var curves = spec.Axes
                .SelectMany(a => a.Curves.Select(c => new GraphCurveRow(c, AxisText(a), choices, MoveCurve)))
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

    private static string AxisText(AxisSpec axis) =>
        $"axe {(string.IsNullOrEmpty(axis.Label) ? "sans unité" : axis.Label)} ({(axis.IsRight ? "droite" : "gauche")})";

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

    private void OnBoundKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is System.Windows.Controls.TextBox box)
            box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
