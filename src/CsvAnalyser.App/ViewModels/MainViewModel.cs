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
    /// <summary>Name, unit, color or style of a curve: updated in place, the view is kept.</summary>
    Appearance,
    AutoScale,
    UndoView,
    RedoView,
}

public partial class MainViewModel : ObservableObject, IChannelHost
{
    private readonly AppSettings _settings;
    private bool _suppressPlotEvents;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasData))]
    [NotifyPropertyChangedFor(nameof(ShowPlotHint))]
    [NotifyPropertyChangedFor(nameof(PlotHint))]
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
    private string _statusText = "Ouvrez un fichier CSV ou glissez-le dans la fenêtre.";

    [ObservableProperty]
    private string _title = AppInfo.FullTitle;

    [ObservableProperty]
    private string _cursorXText = "Survolez le graphe";

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
        ChannelsView = CollectionViewSource.GetDefaultView(Channels);
        ChannelsView.Filter = o => MatchesSearch((ChannelItemViewModel)o);
        PlottedChannels.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowPlotHint));
            OnPropertyChanged(nameof(PlotHint));
        };
    }

    public event EventHandler<PlotChange>? PlotChanged;

    public ObservableCollection<ChannelItemViewModel> Channels { get; } = [];

    public ICollectionView ChannelsView { get; }

    public ObservableCollection<ChannelItemViewModel> PlottedChannels { get; } = [];

    public ObservableCollection<XAxisOption> XAxisOptions { get; } = [];

    public IReadOnlyList<PlotTheme> Themes => PlotThemes.All;

    public bool HasData => Dataset is not null;

    public bool ShowPlotHint => PlottedChannels.Count == 0;

    public string PlotHint => HasData
        ? "Cochez des courbes dans la liste pour les afficher"
        : "Ouvrez un fichier CSV ou glissez-le ici";

    public string XAxisLabel
    {
        get
        {
            var option = SelectedXAxis;
            if (option?.Channel is null)
                return "N° de ligne";
            if (option.Channel == Dataset?.Time?.Channel)
                return string.IsNullOrEmpty(option.Channel.Unit) ? "Temps" : $"Temps ({option.Channel.Unit})";
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
            Title = "Ouvrir un fichier CSV",
            Filter = "Fichiers CSV (*.csv;*.txt)|*.csv;*.txt|Tous les fichiers (*.*)|*.*",
        };
        if (_settings.LastDirectory is { } dir && Directory.Exists(dir))
            dialog.InitialDirectory = dir;

        if (dialog.ShowDialog() == true)
            await LoadFileAsync(dialog.FileName);
    }

    public async Task LoadFileAsync(string path)
    {
        if (IsLoading)
            return;

        IsLoading = true;
        LoadProgress = 0;
        StatusText = $"Chargement de {Path.GetFileName(path)}…";
        var progress = new Progress<double>(p => LoadProgress = p * 100);
        try
        {
            var dataset = await Task.Run(() => CsvImporter.Load(path, progress));
            ApplyDataset(dataset);
            _settings.LastDirectory = Path.GetDirectoryName(path);
            _settings.Save();
        }
        catch (Exception ex)
        {
            StatusText = $"Impossible de lire {Path.GetFileName(path)} : {ex.Message}";
            MessageBox.Show(ex.Message, "Erreur de lecture", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyDataset(Dataset dataset)
    {
        _suppressPlotEvents = true;
        try
        {
            PlottedChannels.Clear();
            Channels.Clear();
            XAxisOptions.Clear();

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

            Dataset = dataset;
            SelectedXAxis = timeOption ?? rowOption;
            Title = $"{Path.GetFileName(dataset.FilePath)} — {AppInfo.FullTitle}";
            StatusText = BuildStatus(dataset);
        }
        finally
        {
            _suppressPlotEvents = false;
        }
        RaisePlotChanged(PlotChange.Dataset);
    }

    private static string BuildStatus(Dataset ds)
    {
        var f = ds.Format;
        string delimiter = f.Delimiter switch { '\t' => "tabulation", ';' => "« ; »", ',' => "« , »", var c => $"« {c} »" };
        string status = $"{ds.RowCount:N0} lignes × {ds.Channels.Count} courbes — séparateur {delimiter}, décimale « {f.DecimalSeparator} » — chargé en {ds.LoadDuration.TotalSeconds:0.00} s";
        if (ds.Time?.Origin is { } origin)
            status += $" — début {origin:dd/MM/yyyy HH:mm:ss}";
        if (ds.IgnoredColumns.Count > 0)
            status += $" — {ds.IgnoredColumns.Count} colonne(s) texte ignorée(s)";
        return status;
    }

    void IChannelHost.OnPlottedChanged(ChannelItemViewModel item)
    {
        if (item.IsPlotted)
        {
            if (PlottedChannels.Contains(item))
                return;
            if (!item.IsColorCustom)
                item.Color = NextColor(except: item);
            PlottedChannels.Add(item);
        }
        else
        {
            PlottedChannels.Remove(item);
            item.CursorText = "";
        }
        RaisePlotChanged(PlotChange.Curves);
    }

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
        string.IsNullOrWhiteSpace(SearchText)
        || item.Name.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase)
        || item.Channel.SourceName.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase);

    partial void OnSearchTextChanged(string value) => ChannelsView.Refresh();

    partial void OnSelectedXAxisChanged(XAxisOption? value)
    {
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
    private void UndoView() => RaisePlotChanged(PlotChange.UndoView);

    [RelayCommand]
    private void RedoView() => RaisePlotChanged(PlotChange.RedoView);

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
        if (!_suppressPlotEvents)
            PlotChanged?.Invoke(this, change);
    }
}
