using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsvAnalyser.App.Services;
using CsvAnalyser.App.Theming;
using CsvAnalyser.Core.Data;
using CsvAnalyser.Core.Import;
using Microsoft.Win32;

namespace CsvAnalyser.App.ViewModels;

public enum PlotChange
{
    Dataset,
    Curves,
    XAxis,
    Theme,
    PlotFont,
    /// <summary>Title or legend of a graph.</summary>
    GraphAppearance,
    AutoScaleX,
    AutoScaleY,
    /// <summary>Name, unit, color or style of a curve: updated in place, the view is kept.</summary>
    Appearance,
    /// <summary>Auto/manual range of an axis or zero alignment of a graph: Y ranges are recomputed.</summary>
    AxisSettings,
    AutoScale,
    UndoView,
    RedoView,
    /// <summary>Measurement cursors shown or hidden.</summary>
    Measure,
    /// <summary>Number of columns of graphs.</summary>
    Layout,
    /// <summary>Which mouse button zooms and which pans.</summary>
    MouseButtons,
    /// <summary>Background grid settings (applied in place).</summary>
    Grid,
    /// <summary>Annotations added, moved, edited, shown or hidden (redrawn in place).</summary>
    Annotations,
}

public partial class MainViewModel : ObservableObject, IChannelHost
{
    private readonly AppSettings _settings;
    private bool _suppressPlotEvents;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasData))]
    [NotifyPropertyChangedFor(nameof(ShowPlotHint))]
    [NotifyPropertyChangedFor(nameof(PlotHint))]
    [NotifyPropertyChangedFor(nameof(ShowFileDropZone))]
    private Dataset? _dataset;

    [ObservableProperty]
    private XAxisOption? _selectedXAxis;

    [ObservableProperty]
    private PlotTheme _selectedTheme;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private double _loadProgress;

    [ObservableProperty]
    private string _statusText = L.T("Ouvrez un fichier CSV ou glissez-le dans la fenêtre.");

    [ObservableProperty]
    private string _title = AppInfo.FullTitle;

    [ObservableProperty]
    private string _cursorXText = L.T("Survolez le graphe");

    /// <summary>Set by the plot controller from its zoom history. Only grays the buttons out (see HistoryButton style).</summary>
    [ObservableProperty]
    private bool _canUndoView;

    [ObservableProperty]
    private bool _canRedoView;

    /// <summary>Zoom factor of the toolbar, side panel and status bar (1 = 100 %).</summary>
    [ObservableProperty]
    private double _uiScale;

    /// <summary>Zoom factor of the plot texts (ticks, axis labels, legend).</summary>
    [ObservableProperty]
    private double _plotFontScale;

    public const double MinUiScale = 0.7, MaxUiScale = 1.5;
    public const double MinPlotFontScale = 0.6, MaxPlotFontScale = 2.0;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _selectedTheme = PlotThemes.ByName(settings.ThemeName);
        _uiScale = ClampOrDefault(settings.UiScale, MinUiScale, MaxUiScale);
        _plotFontScale = ClampOrDefault(settings.PlotFontScale, MinPlotFontScale, MaxPlotFontScale);
        _cursorPanelPlacement = Enum.IsDefined(settings.CursorPanel) ? settings.CursorPanel : CursorPanelPlacement.Left;
        _showCursorLabels = settings.ShowCursorLabels;
        _restoreLayout = settings.RestoreLayout;
        _leftDragZooms = settings.LeftDragZooms;
        _checkUpdatesAtStartup = settings.CheckUpdatesAtStartup;
        ChannelsView = CollectionViewSource.GetDefaultView(Channels);
        ChannelsView.Filter = o => MatchesSearch((ChannelItemViewModel)o);
        Channels.CollectionChanged += (_, _) => SyncComputedXAxisOptions();
        PlottedChannels.CollectionChanged += (_, _) =>
        {
            RefreshPlottedFilter();
            OnPropertyChanged(nameof(ShowPlotHint));
            OnPropertyChanged(nameof(PlotHint));
        };
    }

    public event EventHandler<PlotChange>? PlotChanged;

    /// <summary>The user preferences (window placement is saved by the main window).</summary>
    public AppSettings Settings => _settings;

    public ObservableCollection<ChannelItemViewModel> Channels { get; } = [];

    public ICollectionView ChannelsView { get; }

    public ObservableCollection<ChannelItemViewModel> PlottedChannels { get; } = [];

    public ObservableCollection<XAxisOption> XAxisOptions { get; } = [];

    public IReadOnlyList<PlotTheme> Themes => PlotThemes.All;

    public bool HasData => Dataset is not null;

    public bool ShowPlotHint => HasData && PlottedChannels.Count == 0;

    /// <summary>Before any file is open, the plot area is a drop zone.</summary>
    public bool ShowFileDropZone => !HasData;

    public string PlotHint => L.T("Cochez des courbes dans la liste pour les afficher, ou faites-les glisser sur le graphe");

    public string XAxisLabel
    {
        get
        {
            var option = SelectedXAxis;
            if (option?.Channel is null)
                return L.T("N° de ligne");
            if (option.Channel == Dataset?.Time?.Channel)
                return string.IsNullOrEmpty(option.Channel.Unit) ? L.T("Temps") : L.F("Temps ({0})", option.Channel.Unit);
            return string.IsNullOrEmpty(option.Channel.Unit) ? option.Channel.DisplayName : $"{option.Channel.DisplayName} ({option.Channel.Unit})";
        }
    }

    public string FormatX(double x)
    {
        string text = x.ToString("0.######", CultureInfo.CurrentCulture);
        var time = Dataset?.Time;
        if (SelectedXAxis?.Channel is { } ch && time is not null && ch == time.Channel)
        {
            text = $"t = {text} {ch.Unit}".TrimEnd();
            if (time.Origin is { } origin)
                text += $"   ({origin.AddSeconds(x):dd/MM/yyyy HH:mm:ss.fff})";
            return text;
        }
        return $"x = {text}";
    }

    public static string FormatValue(double value, string unit)
    {
        if (double.IsNaN(value))
            return "—";
        string text = value.ToString("G6", CultureInfo.CurrentCulture);
        return string.IsNullOrEmpty(unit) ? text : $"{text} {unit}";
    }

    private bool CanOpen() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("Ouvrir un fichier CSV ou un projet"),
            Filter = L.T("Fichiers CSV ou projets (*.csv;*.txt;*.json)|*.csv;*.txt;*.json|Fichiers CSV (*.csv;*.txt)|*.csv;*.txt|Projets (*.json)|*.json|Tous les fichiers (*.*)|*.*"),
        };
        if (_settings.LastDirectory is { } dir && Directory.Exists(dir))
            dialog.InitialDirectory = dir;

        if (dialog.ShowDialog() == true)
            await LoadFileAsync(dialog.FileName);
    }

    /// <summary>Opens a CSV file, or a project (.json) with its CSV file (dialog, drop, command line).</summary>
    public async Task LoadFileAsync(string path)
    {
        if (IsLoading)
            return;
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            await OpenProjectFileAsync(path);
            return;
        }
        if (Dataset is not null && !ConfirmSaveChanges(L.T("d'ouvrir un autre fichier")))
            return;
        if (await LoadCsvAsync(path, restoreSession: true))
        {
            ProjectPath = null;
            ResetHistory();
            AddRecent(path);
        }
    }

    /// <summary>Reads a CSV file; <paramref name="restoreSession"/> brings back the layout it had when last left.</summary>
    private async Task<bool> LoadCsvAsync(string path, bool restoreSession)
    {
        if (IsLoading)
            return false;

        SaveSession();
        IsLoading = true;
        LoadProgress = 0;
        StatusText = L.F("Chargement de {0}…", Path.GetFileName(path));
        var progress = new Progress<double>(p => LoadProgress = p * 100);
        try
        {
            var dataset = await Task.Run(() => CsvImporter.Load(path, progress));
            ApplyDataset(dataset);
            if (restoreSession)
                RestoreSession(path);
            _settings.LastDirectory = Path.GetDirectoryName(path);
            _settings.Save();
            return true;
        }
        catch (Exception ex)
        {
            StatusText = L.F("Impossible de lire {0} : {1}", Path.GetFileName(path), ex.Message);
            MessageBox.Show(ex.Message, L.T("Erreur de lecture"), MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyDataset(Dataset dataset)
    {
        _suppressPlotEvents = true;
        _xAxisSyncSuspended = true;
        try
        {
            PlottedChannels.Clear();
            Channels.Clear();
            XAxisOptions.Clear();
            Graphs.Clear();
            Graphs.Add(NewGraph());

            foreach (var channel in dataset.Channels)
                Channels.Add(new ChannelItemViewModel(channel, this));

            XAxisOption? timeOption = null;
            if (dataset.Time is { } time)
            {
                timeOption = new XAxisOption(time.Channel, isTime: true);
                XAxisOptions.Add(timeOption);
            }
            var rowOption = new XAxisOption(null);
            XAxisOptions.Add(rowOption);
            foreach (var channel in dataset.Channels.Where(c => c != dataset.Time?.Channel))
                XAxisOptions.Add(new XAxisOption(channel));

            ForgetMeasureCursors();
            ForgetMeasureY();
            Annotations.Clear(); // they belong to the previous file (its session keeps them)
            Dataset = dataset;
            OnPropertyChanged(nameof(HasAnnotations));
            OnPropertyChanged(nameof(HasZones));
            SelectedXAxis = timeOption ?? rowOption;
            Title = $"{Path.GetFileName(dataset.FilePath)} — {AppInfo.FullTitle}";
            StatusText = BuildStatus(dataset);
        }
        finally
        {
            _suppressPlotEvents = false;
            _xAxisSyncSuspended = false;
        }
        RaisePlotChanged(PlotChange.Dataset);
    }

    private static string BuildStatus(Dataset ds)
    {
        var f = ds.Format;
        string delimiter = f.Delimiter switch { '\t' => L.T("tabulation"), ';' => "« ; »", ',' => "« , »", var c => $"« {c} »" };
        string status = L.F("{0:N0} lignes × {1} courbes — séparateur {2}, décimale « {3} » — chargé en {4:0.00} s", ds.RowCount, ds.Channels.Count, delimiter, f.DecimalSeparator, ds.LoadDuration.TotalSeconds);
        if (ds.Time?.Origin is { } origin)
            status += L.F(" — début {0:dd/MM/yyyy HH:mm:ss}", origin);
        if (ds.IgnoredColumns.Count > 0)
            status += L.F(" — {0} colonne(s) texte ignorée(s)", ds.IgnoredColumns.Count);
        return status;
    }

    void IChannelHost.OnPlottedChanged(ChannelItemViewModel item)
    {
        if (item.IsPlotted)
        {
            if (PlottedChannels.Contains(item))
                return;
            if (item.Graph is null || !Graphs.Contains(item.Graph))
                item.Graph = Graphs[0];
            if (!item.IsColorCustom)
                item.Color = NextColor(except: item);
            PlottedChannels.Add(item);
        }
        else
        {
            PlottedChannels.Remove(item);
            item.CursorText = "";
            RemoveEmptyGraphs();
        }
        RaisePlotChanged(PlotChange.Curves);
    }

    // ---- Stacked graphs ----

    private int _nextGraphId;

    /// <summary>The stacked graphs, top to bottom. There is always at least one.</summary>
    public ObservableCollection<GraphViewModel> Graphs { get; } = [];

    /// <summary>Lets the axes window show the current range when switching an axis to manual. Set by the plot controller.</summary>
    public Func<GraphViewModel, string, (double Min, double Max)?>? CurrentAxisRange { get; set; }

    public IReadOnlyList<GraphSpec> BuildLayout() => GraphLayout.Build(Graphs, PlottedChannels);

    private GraphViewModel NewGraph() => new(
        ++_nextGraphId,
        onSettingsChanged: () => RaisePlotChanged(PlotChange.AxisSettings),
        onAppearanceChanged: () => RaisePlotChanged(PlotChange.GraphAppearance))
    {
        Number = Graphs.Count + 1,
        OnLayoutChangedHandler = () => RaisePlotChanged(PlotChange.Layout),
    };

    /// <summary>The plotted curves grouped by graph, for the cursor card.</summary>
    public ObservableCollection<CursorGroup> CursorGroups { get; } = [];

    private void RefreshCursorGroups()
    {
        CursorGroups.Clear();
        foreach (var graph in Graphs)
        {
            var curves = PlottedChannels.Where(c => c.Graph == graph).ToList();
            if (curves.Count > 0)
                CursorGroups.Add(new CursorGroup(graph, curves, ShowTitle: Graphs.Count > 1));
        }
    }

    /// <summary>Puts a curve in a graph (null = a new graph at the bottom) and plots it if needed.</summary>
    public void MoveChannelToGraph(ChannelItemViewModel item, GraphViewModel? target)
    {
        if (target is null)
        {
            if (item.IsPlotted && item.Graph is { } current && Graphs.Contains(current)
                && PlottedChannels.All(c => c == item || c.Graph != current))
                return; // Already alone in its graph: a new graph would change nothing.
            target = NewGraph();
            Graphs.Add(target);
        }

        bool wasPlotted = item.IsPlotted;
        item.Graph = target;
        if (wasPlotted)
        {
            RemoveEmptyGraphs();
            RaisePlotChanged(PlotChange.Curves);
        }
        else
        {
            item.IsPlotted = true; // Raises the change itself.
        }
    }

    /// <summary>Puts several curves in one graph (null = one new graph for all of them) and plots them; one plot update.</summary>
    public void MoveChannelsToGraph(IReadOnlyList<ChannelItemViewModel> items, GraphViewModel? target)
    {
        if (items.Count <= 1)
        {
            if (items.Count == 1)
                MoveChannelToGraph(items[0], target);
            return;
        }
        _suppressPlotEvents = true;
        try
        {
            if (target is null)
            {
                target = NewGraph();
                Graphs.Add(target);
            }
            foreach (var item in items)
            {
                item.Graph = target;
                if (!item.IsPlotted)
                    item.IsPlotted = true;
            }
            RemoveEmptyGraphs();
        }
        finally
        {
            _suppressPlotEvents = false;
        }
        RaisePlotChanged(PlotChange.Curves);
    }

    /// <summary>Graph number <paramref name="index"/> (0-based), or null.</summary>
    public GraphViewModel? GraphAt(int index) => index >= 0 && index < Graphs.Count ? Graphs[index] : null;

    private void RemoveEmptyGraphs()
    {
        for (int i = Graphs.Count - 1; i >= 0 && Graphs.Count > 1; i--)
        {
            var graph = Graphs[i];
            if (!graph.KeepWhenEmpty && !PlottedChannels.Any(c => c.Graph == graph))
                Graphs.RemoveAt(i);
        }
        for (int i = 0; i < Graphs.Count; i++)
            Graphs[i].Number = i + 1;
    }

    /// <summary>Opens the axes window, scrolled to a graph when one is given (context menu of a graph).</summary>
    public event EventHandler<GraphViewModel?>? EditAxesRequested;

    [RelayCommand]
    private void EditAxes(GraphViewModel? graph) => EditAxesRequested?.Invoke(this, graph);

    void IChannelHost.OnAppearanceChanged(ChannelItemViewModel item)
    {
        foreach (var option in XAxisOptions)
        {
            if (option.Channel == item.Channel)
                option.RefreshLabel();
        }
        if (SelectedXAxis?.Channel == item.Channel)
            OnPropertyChanged(nameof(XAxisLabel));
        RaisePlotChanged(PlotChange.Appearance);
    }

    ScottPlot.Color IChannelHost.PickAutoColor(ChannelItemViewModel item) => NextColor(except: item);

    /// <summary>Opens the settings of a curve (pencil button or double-click in the list).</summary>
    public event EventHandler<ChannelItemViewModel>? EditChannelRequested;

    [RelayCommand]
    private void EditChannel(ChannelItemViewModel? item)
    {
        if (item is not null)
            EditChannelRequested?.Invoke(this, item);
    }

    private ScottPlot.Color NextColor(ChannelItemViewModel? except = null)
    {
        var palette = SelectedTheme.Palette;
        var used = PlottedChannels.Where(c => c != except).Select(c => c.Color).ToHashSet();
        foreach (var hex in palette)
        {
            var color = ScottPlot.Color.FromHex(hex);
            if (!used.Contains(color))
                return color;
        }
        return ScottPlot.Color.FromHex(palette[PlottedChannels.Count % palette.Length]);
    }

    private bool MatchesSearch(ChannelItemViewModel item) =>
        (!ShowPlottedOnly || item.IsPlotted)
        && (string.IsNullOrWhiteSpace(SearchText)
            || item.Name.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase)
            || item.Channel.SourceName.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase));

    partial void OnSearchTextChanged(string value) => ChannelsView.Refresh();

    /// <summary>The curve list shows only the ticked curves (filter button beside the search box).</summary>
    [ObservableProperty]
    private bool _showPlottedOnly;

    partial void OnShowPlottedOnlyChanged(bool value) => ChannelsView.Refresh();

    private bool _plottedFilterRefreshPending;

    /// <summary>With the filter on, an unticked curve leaves the list (once per burst of changes, after the click).</summary>
    private void RefreshPlottedFilter()
    {
        if (!ShowPlottedOnly || _plottedFilterRefreshPending)
            return;
        _plottedFilterRefreshPending = true;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _plottedFilterRefreshPending = false;
            ChannelsView.Refresh();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>While the whole list is rebuilt (file opened, configuration or undo): synced once at the end.</summary>
    private bool _xAxisSyncSuspended;

    /// <summary>
    /// The computed curves are X axis choices too (after the file's columns). An edited curve is a new channel: the
    /// X axis follows it; a deleted one gives the X axis back to the default (time, else row number).
    /// </summary>
    private void SyncComputedXAxisOptions()
    {
        if (Dataset is null || _xAxisSyncSuspended)
            return;
        var computed = Channels.Where(c => c.IsComputed).Select(c => c.Channel).ToList();
        var selected = SelectedXAxis;
        foreach (var stale in XAxisOptions.Where(o => o.IsComputed && !computed.Contains(o.Channel!)).ToList())
            XAxisOptions.Remove(stale);
        XAxisOption? successor = null;
        foreach (var channel in computed)
        {
            if (XAxisOptions.Any(o => o.Channel == channel))
                continue;
            var option = new XAxisOption(channel, isComputed: true);
            XAxisOptions.Add(option);
            if (selected?.Channel is { } old && old.SourceName == channel.SourceName)
                successor = option;
        }
        if (selected is not null && !XAxisOptions.Contains(selected))
            SelectedXAxis = successor ?? XAxisOptions.FirstOrDefault();
    }

    partial void OnSelectedXAxisChanged(XAxisOption? value)
    {
        ForgetMeasureCursors();
        RefreshAnnotationPositions();
        OnPropertyChanged(nameof(XAxisLabel));
        RaisePlotChanged(PlotChange.XAxis);
    }

    partial void OnSelectedThemeChanged(PlotTheme value)
    {
        // Curves whose color was picked by hand keep it; the others take the new palette.
        _suppressPlotEvents = true;
        try
        {
            for (int i = 0; i < PlottedChannels.Count; i++)
            {
                if (!PlottedChannels[i].IsColorCustom)
                    PlottedChannels[i].Color = ScottPlot.Color.FromHex(value.Palette[i % value.Palette.Length]);
            }
        }
        finally
        {
            _suppressPlotEvents = false;
        }

        _settings.ThemeName = value.Name;
        _settings.Save();
        RaisePlotChanged(PlotChange.Theme);
    }

    /// <summary>Ticks or unticks several curves at once (one plot update).</summary>
    public void SetPlotted(IEnumerable<ChannelItemViewModel> items, bool plotted)
    {
        var changed = items.Where(i => i.IsPlotted != plotted).ToList();
        if (changed.Count == 0)
            return;
        _suppressPlotEvents = true;
        try
        {
            foreach (var item in changed)
                item.IsPlotted = plotted;
        }
        finally
        {
            _suppressPlotEvents = false;
        }
        RaisePlotChanged(PlotChange.Curves);
    }

    [RelayCommand]
    private void ClearSelection()
    {
        _suppressPlotEvents = true;
        try
        {
            foreach (var item in PlottedChannels.ToList())
                item.IsPlotted = false;
        }
        finally
        {
            _suppressPlotEvents = false;
        }
        RaisePlotChanged(PlotChange.Curves);
    }

    [RelayCommand]
    private void AutoScale() => RaisePlotChanged(PlotChange.AutoScale);

    [RelayCommand]
    private void AutoScaleX() => RaisePlotChanged(PlotChange.AutoScaleX);

    [RelayCommand]
    private void AutoScaleY() => RaisePlotChanged(PlotChange.AutoScaleY);

    [RelayCommand]
    private void UndoView() => Undo();

    [RelayCommand]
    private void RedoView() => Redo();

    partial void OnUiScaleChanged(double value)
    {
        _settings.UiScale = Math.Round(value, 2);
        _settings.Save();
    }

    partial void OnPlotFontScaleChanged(double value)
    {
        _settings.PlotFontScale = Math.Round(value, 2);
        _settings.Save();
        RaisePlotChanged(PlotChange.PlotFont);
    }

    [RelayCommand]
    private void ResetDisplaySizes()
    {
        UiScale = 1;
        PlotFontScale = 1;
    }

    private static double ClampOrDefault(double value, double min, double max) =>
        double.IsFinite(value) && value > 0 ? Math.Clamp(value, min, max) : 1;

    private void RaisePlotChanged(PlotChange change)
    {
        if (change is PlotChange.Curves or PlotChange.Dataset)
            RefreshCursorGroups();
        if (IsMeasuring && change is PlotChange.Curves or PlotChange.Dataset or PlotChange.Appearance or PlotChange.XAxis)
            ScheduleMeasurements();
        if (IsMeasuringY && change is PlotChange.Curves or PlotChange.Dataset or PlotChange.Appearance)
        {
            RefreshMeasureYChoices();
            UpdateMeasureYSummary();
        }
        if (!_suppressPlotEvents)
        {
            PlotChanged?.Invoke(this, change);
            if (change is PlotChange.Curves or PlotChange.Appearance or PlotChange.AxisSettings or PlotChange.GraphAppearance
                or PlotChange.Layout or PlotChange.XAxis or PlotChange.Measure or PlotChange.Annotations)
                ScheduleCommit();
        }
    }

    // ---- Cursor panel placement ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CursorGroupsOrientation))]
    [NotifyPropertyChangedFor(nameof(CursorGroupWidth))]
    private CursorPanelPlacement _cursorPanelPlacement;

    /// <summary>
    /// Fixed group width on the right and under the plot (so the panel never changes size while the values change);
    /// NaN on the left, where the column width decides.
    /// </summary>
    public double CursorGroupWidth => CursorPanelPlacement == CursorPanelPlacement.Left ? double.NaN : 280;

    /// <summary>Groups side by side when the panel is under the plot, stacked otherwise.</summary>
    public System.Windows.Controls.Orientation CursorGroupsOrientation =>
        CursorPanelPlacement == CursorPanelPlacement.Bottom
            ? System.Windows.Controls.Orientation.Horizontal
            : System.Windows.Controls.Orientation.Vertical;

    public static IReadOnlyList<StyleChoice<CursorPanelPlacement>> CursorPanelChoices { get; } =
    [
        new(CursorPanelPlacement.Left, "À gauche, sous la liste des courbes"),
        new(CursorPanelPlacement.Right, "À droite du graphe"),
        new(CursorPanelPlacement.Bottom, "Sous le graphe"),
    ];

    partial void OnCursorPanelPlacementChanged(CursorPanelPlacement value)
    {
        _settings.CursorPanel = value;
        _settings.Save();
    }

    /// <summary>Also write each curve's value in the graph, next to its point under the cursor.</summary>
    [ObservableProperty]
    private bool _showCursorLabels;

    /// <summary>Left-button drag = zoom rectangle and wheel-button drag = pan (true), or the reverse.</summary>
    [ObservableProperty]
    private bool _leftDragZooms;

    public static IReadOnlyList<StyleChoice<bool>> MouseChoices { get; } =
    [
        new(true, "Clic gauche : zoom sur un rectangle — molette : déplacer"),
        new(false, "Clic gauche : déplacer — molette : zoom sur un rectangle"),
    ];

    partial void OnLeftDragZoomsChanged(bool value)
    {
        _settings.LeftDragZooms = value;
        _settings.Save();
        RaisePlotChanged(PlotChange.MouseButtons);
    }

    partial void OnShowCursorLabelsChanged(bool value)
    {
        _settings.ShowCursorLabels = value;
        _settings.Save();
    }
}
