using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CsvAnalyser.App.Plotting;
using CsvAnalyser.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace CsvAnalyser.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly PlotController _plotController;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _plotController = new PlotController(PlotView, viewModel);

        ApplyAppTheme();
        viewModel.EditChannelRequested += (_, item) =>
            new CurveSettingsWindow(item, GraphLayout.AxisChoices(item, viewModel.BuildLayout())) { Owner = this }.ShowDialog();
        PlaceCursorCard();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CursorPanelPlacement))
                PlaceCursorCard();
        };
        viewModel.EditAxesRequested += (_, graph) =>
            new AxesWindow(viewModel, graph) { Owner = this }.ShowDialog();
        viewModel.PlotChanged += (_, change) =>
        {
            if (change == PlotChange.Theme)
                ApplyAppTheme();
        };
    }

    private void ApplyAppTheme()
    {
        bool dark = _viewModel.SelectedTheme.IsDarkApp;
        ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.None);

        // Opaque equivalents of the Fluent secondary/placeholder text: semi-transparent text renders far too
        // light here. Top-level resources override the theme dictionaries.
        SetBrush("AppSecondaryTextBrush", dark ? 0xC8 : 0x5C);
        SetBrush("AppDisabledTextBrush", dark ? 0x6A : 0x90);
        SetBrush("TextControlPlaceholderForeground", dark ? 0xA8 : 0x70);
        SetBrush("TextPlaceholderColorBrush", dark ? 0xA8 : 0x70);
        // Menus (curve list, plot right-click) were nearly transparent.
        SetBrush("AppMenuBackground", dark ? 0x2B : 0xFB);
        SetBrush("AppMenuBorder", dark ? 0x4A : 0xB8);
    }

    /// <summary>Moves the cursor card to the host chosen in the settings (left panel, right of the plot, under the plot).</summary>
    private void PlaceCursorCard()
    {
        var target = _viewModel.CursorPanelPlacement switch
        {
            CursorPanelPlacement.Right => CursorHostRight,
            CursorPanelPlacement.Bottom => CursorHostBottom,
            _ => CursorHostLeft,
        };
        foreach (var host in new[] { CursorHostLeft, CursorHostRight, CursorHostBottom })
        {
            if (host != target && host.Content == CursorCard)
                host.Content = null;
            host.Visibility = host == target ? Visibility.Visible : Visibility.Collapsed;
        }
        target.Content = CursorCard;
    }

    private void OnFileDropZoneClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.OpenCommand.CanExecute(null))
            _viewModel.OpenCommand.Execute(null);
    }

    private void HighlightFileDropZone(bool on) =>
        FileDropFrame.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, on ? "SystemAccentColorPrimaryBrush" : "AppSecondaryTextBrush");

    private static void SetBrush(string key, int gray)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)gray, (byte)gray, (byte)gray));
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) =>
        new SettingsWindow(_viewModel) { Owner = this }.ShowDialog();

    // ---- Drag a curve (from the list or the cursor card) onto a graph ----

    private Point? _dragStart;
    private ChannelItemViewModel? _dragItem;

    private void OnCurvePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragItem = (e.OriginalSource as FrameworkElement ?? (e.OriginalSource as FrameworkContentElement)?.Parent as FrameworkElement)
            ?.DataContext as ChannelItemViewModel;
    }

    private void OnCurvePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start || _dragItem is not { } item)
            return;
        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance * 2
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance * 2)
            return;

        _dragStart = null;
        _dragItem = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(ChannelItemViewModel), item), DragDropEffects.Move);
        HideDropTargets();
    }

    private void OnPlotDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ChannelItemViewModel)))
            return;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        NewGraphZone.Visibility = Visibility.Visible;
        var position = e.GetPosition(PlotView);
        bool overNewGraph = IsOverNewGraphZone(position);
        NewGraphZone.BorderThickness = new Thickness(overNewGraph ? 3 : 1);

        if (!overNewGraph && _plotController.GraphIndexAt(position) is { } index && _plotController.GraphBounds(index) is { } bounds)
        {
            Canvas.SetLeft(DropHighlight, bounds.Left + 2);
            Canvas.SetTop(DropHighlight, bounds.Top + 2);
            DropHighlight.Width = Math.Max(0, bounds.Width - 4);
            DropHighlight.Height = Math.Max(0, bounds.Height - 4);
            DropHighlight.Visibility = Visibility.Visible;
        }
        else
        {
            DropHighlight.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPlotDragLeave(object sender, DragEventArgs e)
    {
        var position = e.GetPosition(PlotView);
        if (position.X < 0 || position.Y < 0 || position.X > PlotView.ActualWidth || position.Y > PlotView.ActualHeight)
            HideDropTargets();
    }

    private void OnPlotDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ChannelItemViewModel)) is not ChannelItemViewModel item)
            return;
        e.Handled = true;
        var position = e.GetPosition(PlotView);
        GraphViewModel? target = IsOverNewGraphZone(position)
            ? null
            : _viewModel.GraphAt(_plotController.GraphIndexAt(position) ?? 0);
        HideDropTargets();
        _viewModel.MoveChannelToGraph(item, target);
    }

    private bool IsOverNewGraphZone(Point position) =>
        position.Y >= PlotView.ActualHeight - NewGraphZone.Height - NewGraphZone.Margin.Bottom - 4;

    private void HideDropTargets()
    {
        NewGraphZone.Visibility = Visibility.Collapsed;
        DropHighlight.Visibility = Visibility.Collapsed;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool file = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = file ? DragDropEffects.Copy : DragDropEffects.None;
        HighlightFileDropZone(file);
        e.Handled = true;
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e) => HighlightFileDropZone(false);

    private async void OnDrop(object sender, DragEventArgs e)
    {
        HighlightFileDropZone(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            await _viewModel.LoadFileAsync(files[0]);
    }
}
