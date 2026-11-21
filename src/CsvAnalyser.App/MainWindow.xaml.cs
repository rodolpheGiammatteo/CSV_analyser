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
        // Once the window is shown, so a slow network never delays the start.
        ContentRendered += async (_, _) => await viewModel.CheckUpdatesQuietlyAsync();
        viewModel.EditAnnotationRequested += (_, annotation) =>
            new AnnotationSettingsWindow(annotation, viewModel) { Owner = this }.ShowDialog();
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
        viewModel.ExportRequested += async (_, _) =>
        {
            var export = new ExportViewModel(viewModel);
            var window = new ExportWindow(export, System.IO.Path.GetDirectoryName(viewModel.Dataset?.FilePath)) { Owner = this };
            if (window.ShowDialog() == true && window.ChosenPath is { } path)
                await viewModel.ExportAsync(path, export.Columns, export.Rows, export.Options);
        };
        viewModel.ComputedCurveRequested += (_, request) =>
            new ComputedCurveWindow(viewModel, new ComputedCurveViewModel(viewModel, request)) { Owner = this }.ShowDialog();
        viewModel.SpectrumRequested += (_, graph) =>
        {
            if (viewModel.PlottedChannels.Count == 0)
            {
                System.Windows.MessageBox.Show(L.T("Cochez d'abord les courbes à analyser."), L.T("Spectre"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }
            new SpectrumWindow(viewModel, graph) { Owner = this }.Show();
        };
        RestorePlacement();
        Closing += (_, e) =>
        {
            if (!viewModel.ConfirmSaveChanges(L.T("de fermer")))
            {
                e.Cancel = true;
                return;
            }
            viewModel.SaveSession();
            SavePlacement();
        };
        LeftPanel.SizeChanged += (_, _) => LimitCursorCard();
        PlotColumn.SizeChanged += (_, _) => LimitCursorCard();
        BodyGrid.SizeChanged += (_, _) => LimitCurveList();
        CursorHostRight.SizeChanged += (_, _) => LimitCurveList();
    }

    // ---- Shift+click: tick or untick every curve between two clicks ----

    private ChannelItemViewModel? _lastCheckClick;

    private void OnCurveCheckClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChannelItemViewModel item)
            return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _lastCheckClick is { } anchor)
        {
            var visible = _viewModel.ChannelsView.Cast<ChannelItemViewModel>().ToList();
            int a = visible.IndexOf(anchor), b = visible.IndexOf(item);
            if (a >= 0 && b >= 0)
            {
                var range = visible.GetRange(Math.Min(a, b), Math.Abs(a - b) + 1);
                _viewModel.SetPlotted(range, item.IsPlotted);
                SelectCurves(range); // the range is also selected, ready to be dragged onto a graph
                return; // the anchor stays for the next Shift+click
            }
        }
        _lastCheckClick = item;
    }

    // ---- Curve list shown or hidden (Ctrl+L) ----

    private double _curveListWidth = 330;

    private void OnToggleCurveList(object sender, RoutedEventArgs e) => SetCurveListHidden(LeftPanel.Visibility == Visibility.Visible);

    private void SetCurveListHidden(bool hidden)
    {
        if (hidden == (LeftPanel.Visibility != Visibility.Visible))
            return;
        if (hidden)
        {
            _curveListWidth = CurveColumn.ActualWidth > 150 ? CurveColumn.ActualWidth : _curveListWidth;
            CurveColumn.MinWidth = 0;
            CurveColumn.Width = new GridLength(0);
        }
        else
        {
            CurveColumn.MinWidth = 200;
            CurveColumn.Width = new GridLength(_curveListWidth);
        }
        LeftPanel.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        CurveSplitter.Visibility = LeftPanel.Visibility;
        LimitCurveList();
        CurveListButton.Icon = new SymbolIcon(hidden ? SymbolRegular.PanelLeftExpand24 : SymbolRegular.PanelLeftContract24);
        _viewModel.Settings.CurveListHidden = hidden;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SetCurveListHidden(LeftPanel.Visibility == Visibility.Visible);
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    // ---- Window size and curve list width, kept between sessions ----

    private void RestorePlacement()
    {
        var s = _viewModel.Settings;
        if (s.WindowWidth is > 300 and var w && s.WindowHeight is > 200 and var h
            && s.WindowLeft is { } left && s.WindowTop is { } top)
        {
            // Only if the window would still be visible (a screen may have been unplugged).
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (screen.IntersectsWith(new Rect(left + 40, top + 10, Math.Max(1, w - 80), 40)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
                Width = w;
                Height = h;
            }
        }
        if (s.WindowMaximized)
            WindowState = WindowState.Maximized;
        if (s.CurveListWidth is > 150 and < 2000 and var listWidth)
        {
            CurveColumn.Width = new GridLength(listWidth);
            _curveListWidth = listWidth;
        }
        if (s.CurveListHidden)
            SetCurveListHidden(true);
    }

    private void SavePlacement()
    {
        var s = _viewModel.Settings;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            s.WindowLeft = bounds.Left;
            s.WindowTop = bounds.Top;
            s.WindowWidth = bounds.Width;
            s.WindowHeight = bounds.Height;
        }
        s.WindowMaximized = WindowState == WindowState.Maximized;
        s.CurveListWidth = LeftPanel.Visibility == Visibility.Visible ? CurveColumn.ActualWidth : _curveListWidth;
        s.Save();
    }

    /// <summary>Width always left to the graphs, so the list cannot cover them nor push the splitter out of reach.</summary>
    private const double MinPlotWidth = 320;

    /// <summary>Caps the curve list width to what the window leaves after the graphs and the right cursor card.</summary>
    private void LimitCurveList()
    {
        if (BodyGrid.ActualWidth <= 0)
            return;
        double max = Math.Max(CurveColumn.MinWidth,
            BodyGrid.ActualWidth - 8 - CursorRightColumn.ActualWidth - MinPlotWidth);
        CurveColumn.MaxWidth = max;
        if (LeftPanel.Visibility == Visibility.Visible && CurveColumn.Width.IsAbsolute && CurveColumn.Width.Value > max)
            CurveColumn.Width = new GridLength(max);
    }

    /// <summary>The cursor card scrolls instead of growing past part of its area (it used to hide the curve list).</summary>
    private void LimitCursorCard()
    {
        CursorHostLeft.MaxHeight = Math.Max(120, LeftPanel.ActualHeight * 0.45);
        double scale = Math.Max(0.1, _viewModel.UiScale);
        CursorHostRight.MaxHeight = Math.Max(120, PlotColumn.ActualHeight / scale - 8);
        CursorHostBottom.MaxHeight = Math.Max(100, PlotColumn.ActualHeight * 0.35 / scale);
    }

    /// <summary>The recent files, most recent first, in a menu under the clock button.</summary>
    private void OnRecentMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = (UIElement)sender,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        if (_viewModel.RecentFiles.Count == 0)
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = L.T("Aucun fichier récent"), IsEnabled = false });
        foreach (var recent in _viewModel.RecentFiles)
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem
            {
                Header = recent.Name,
                ToolTip = recent.Path,
                Icon = new SymbolIcon(recent.IsProject ? SymbolRegular.DocumentData24 : SymbolRegular.DocumentTable24),
                Command = _viewModel.OpenRecentCommand,
                CommandParameter = recent.Path,
            });
        }
        if (_viewModel.RecentFiles.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = L.T("Vider la liste"), Command = _viewModel.ClearRecentCommand });
        }
        menu.IsOpen = true;
    }

    /// <summary>A toolbar button whose menu opens on a left click.</summary>
    private void OnOpenButtonMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } button)
            return;
        menu.DataContext = button.DataContext; // not inherited when opened from code
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
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
        SetBrush("AppMenuForeground", dark ? 0xF0 : 0x1A);
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

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(_viewModel) { Owner = this };
        settings.ShowDialog();
        if (settings.RestartRequested)
            Restart();
    }

    /// <summary>Closes (offering to save) and starts again on the same file: a new language applies.</summary>
    private void Restart()
    {
        string? reopen = _viewModel.ProjectPath is { } project && !_viewModel.IsDirty ? project : _viewModel.Dataset?.FilePath;
        Close();
        if (IsVisible)
            return; // the user cancelled
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        if (reopen is not null)
            start.ArgumentList.Add(reopen);
        System.Diagnostics.Process.Start(start);
    }

    // ---- Drag a curve (from the list or the cursor card) onto a graph ----

    private Point? _dragStart;
    private ChannelItemViewModel? _dragItem;

    /// <summary>The curves being dragged (the selection of the list when the dragged curve is part of it).</summary>
    private IReadOnlyList<ChannelItemViewModel> _draggedCurves = [];

    private void OnCurvePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragItem = (e.OriginalSource as FrameworkElement ?? (e.OriginalSource as FrameworkContentElement)?.Parent as FrameworkElement)
            ?.DataContext as ChannelItemViewModel;
        if (sender != CurveListBox || _dragItem is not { } item || IsInsideButton(e.OriginalSource as DependencyObject))
            return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Ctrl+click: add to or remove from the selection, without ticking.
            e.Handled = true;
            if (CurveListBox.SelectedItems.Contains(item))
                CurveListBox.SelectedItems.Remove(item);
            else
                CurveListBox.SelectedItems.Add(item);
            _lastCheckClick = item;
            return;
        }
        if (e.ClickCount == 2)
        {
            // Double-click: the curve settings. The first click of it ticked or unticked the curve: put it back.
            e.Handled = true;
            item.IsPlotted = !item.IsPlotted;
            _dragStart = null;
            _viewModel.EditChannelCommand.Execute(item);
            return;
        }
        // A plain click on a curve outside the selection selects that curve only (a click inside keeps it, to drag it).
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && !CurveListBox.SelectedItems.Contains(item))
            SelectCurves([item]);
    }

    private void SelectCurves(IReadOnlyList<ChannelItemViewModel> items)
    {
        CurveListBox.SelectedItems.Clear();
        foreach (var item in items)
            CurveListBox.SelectedItems.Add(item);
    }

    private static bool IsInsideButton(DependencyObject? element)
    {
        for (var e = element; e is not null; e = e is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(e) : LogicalTreeHelper.GetParent(e))
        {
            if (e is System.Windows.Controls.Primitives.ButtonBase and not System.Windows.Controls.Primitives.ToggleButton)
                return true;
            if (e is ListBoxItem)
                return false;
        }
        return false;
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
        // From the list, a curve of a multiple selection takes the whole selection along (in list order).
        _draggedCurves = sender == CurveListBox && CurveListBox.SelectedItems.Count > 1 && CurveListBox.SelectedItems.Contains(item)
            ? _viewModel.ChannelsView.Cast<ChannelItemViewModel>().Where(CurveListBox.SelectedItems.Contains).ToList()
            : [item];
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(ChannelItemViewModel), item), DragDropEffects.Move);
        _draggedCurves = [];
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
        _viewModel.MoveChannelsToGraph(_draggedCurves.Contains(item) ? _draggedCurves : [item], target);
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
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
            return;
        await _viewModel.LoadFileAsync(files[0]); // a .json project opens with its CSV (or applies to the open one)
    }
}
