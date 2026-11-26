using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsvAnalyser.Core.Data;
using CsvAnalyser.Core.Import;
using Microsoft.Win32;

namespace CsvAnalyser.App.ViewModels;

/// <summary>
/// Several CSV files compared: the opened file is the first <see cref="DataSource"/>; files added with
/// "Ajouter un fichier pour comparer" follow, each drawn on its own X shifted by its offset. With a single file
/// nothing of this is visible (no group in the curve list, no file name after the curves).
/// </summary>
public partial class MainViewModel
{
    /// <summary>The loaded files: the opened one first, then the files added to compare.</summary>
    public ObservableCollection<DataSource> Sources { get; } = [];

    public DataSource? PrimarySource => Sources.Count > 0 ? Sources[0] : null;

    public bool HasSeveralFiles => Sources.Count > 1;

    /// <summary>Compared files: a column plotted for a file takes the color of the same column of the opened file, and a line pattern per file.</summary>
    [ObservableProperty]
    private bool _compareSameColor;

    partial void OnCompareSameColorChanged(bool value)
    {
        _settings.CompareSameColor = value;
        _settings.Save();
        if (HasSeveralFiles)
        {
            foreach (var item in PlottedChannels.Where(c => c.IsCompared && !c.IsColorCustom))
                item.Color = CompareColor(item) ?? item.Color;
            RaisePlotChanged(PlotChange.Appearance);
        }
    }

    /// <summary>The X values of a curve: its file's X for the chosen X axis, offset included.</summary>
    public double[] XValuesFor(ChannelItemViewModel item) =>
        (item.Source ?? PrimarySource) is { } source && SelectedXAxis is { } option ? source.XValues(option) : [];

    /// <summary>True when the X of that curve's file is ascending.</summary>
    public bool IsXSortedFor(ChannelItemViewModel item) =>
        (item.Source ?? PrimarySource) is not { } source || SelectedXAxis is not { } option || source.IsXSorted(option);

    /// <summary>Line pattern of a compared file when <see cref="CompareSameColor"/> is on (its curves keep the same colors).</summary>
    public CurveLine? CompareLine(ChannelItemViewModel item) =>
        CompareSameColor && item.IsCompared && item.Line == CurveLine.Solid
            ? (item.Source!.Index % 3) switch { 1 => CurveLine.Dashed, 2 => CurveLine.Dotted, _ => CurveLine.DenselyDashed }
            : null;

    /// <summary>With <see cref="CompareSameColor"/>: the color of the same column of the opened file.</summary>
    private ScottPlot.Color? CompareColor(ChannelItemViewModel item)
    {
        if (!CompareSameColor || !item.IsCompared)
            return null;
        var twin = Channels.FirstOrDefault(c => c.Source == PrimarySource && !c.IsComputed && c.Channel.SourceName == item.Channel.SourceName);
        return twin is { IsPlotted: true } || twin is { IsColorCustom: true } ? twin.Color : null;
    }

    /// <summary>The opened file becomes the only source (a new file is opened).</summary>
    private DataSource ResetSources(Dataset dataset)
    {
        Sources.Clear();
        var primary = new DataSource(dataset, 0, OnSourceOffsetChanged);
        Sources.Add(primary);
        UpdateSourceDisplay();
        return primary;
    }

    [RelayCommand(CanExecute = nameof(CanAddCompareFile))]
    private async Task AddCompareFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("Ajouter un fichier pour comparer"),
            Filter = L.T("Fichiers CSV (*.csv;*.txt)|*.csv;*.txt|Tous les fichiers (*.*)|*.*"),
            Multiselect = true,
        };
        if (_settings.LastDirectory is { } dir && Directory.Exists(dir))
            dialog.InitialDirectory = dir;
        if (dialog.ShowDialog() != true)
            return;
        foreach (var path in dialog.FileNames)
            await AddCompareFileAsync(path);
    }

    private bool CanAddCompareFile() => Dataset is not null && !IsLoading;

    /// <summary>Loads a CSV file and adds it to compare with the opened one. Not an undo step: the history restarts.</summary>
    public async Task<bool> AddCompareFileAsync(string path, double offset = 0, bool resetHistory = true)
    {
        if (Dataset is null || IsLoading)
            return false;
        if (Sources.Any(s => string.Equals(s.FilePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = L.F("{0} est déjà ouvert.", Path.GetFileName(path));
            return false;
        }
        IsLoading = true;
        LoadProgress = 0;
        StatusText = L.F("Chargement de {0}…", Path.GetFileName(path));
        var progress = new Progress<double>(p => LoadProgress = p * 100);
        try
        {
            var dataset = await Task.Run(() => CsvImporter.Load(path, progress));
            var source = new DataSource(dataset, Sources.Count, OnSourceOffsetChanged);
            source.SetOffsetSilently(offset);
            Sources.Add(source);
            foreach (var channel in dataset.Channels)
                Channels.Add(new ChannelItemViewModel(channel, this) { Source = source });
            UpdateSourceDisplay();
            if (resetHistory)
                ResetHistory();
            StatusText = L.F("{0} ajouté pour comparer ({1:N0} lignes).", Path.GetFileName(path), dataset.RowCount);
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

    /// <summary>A compared file leaves (its curves too). Not an undo step: the history restarts.</summary>
    [RelayCommand]
    private void RemoveSource(DataSource? source)
    {
        if (source is null || source.IsPrimary || !Sources.Contains(source))
            return;
        _suppressPlotEvents = true;
        try
        {
            foreach (var item in Channels.Where(c => c.Source == source).ToList())
            {
                if (item.IsPlotted)
                    item.IsPlotted = false;
                Channels.Remove(item);
            }
            Annotations.Where(a => a.Curve?.Source == source).ToList().ForEach(a => Annotations.Remove(a));
            Sources.Remove(source);
            for (int i = 0; i < Sources.Count; i++)
                Sources[i].Index = i;
            RemoveEmptyGraphs();
        }
        finally
        {
            _suppressPlotEvents = false;
        }
        UpdateSourceDisplay();
        ResetHistory();
        RaisePlotChanged(PlotChange.Curves);
        RaisePlotChanged(PlotChange.Annotations);
    }

    /// <summary>Plots the same column of every file, in the graph of the clicked curve.</summary>
    public void PlotForAllFiles(ChannelItemViewModel item)
    {
        var graph = item.IsPlotted ? item.Graph : null;
        var twins = Channels.Where(c => !c.IsComputed && c.Channel.SourceName == item.Channel.SourceName).ToList();
        _suppressPlotEvents = true;
        try
        {
            foreach (var twin in twins.OrderBy(c => c.Source?.Index ?? 0))
            {
                if (graph is not null)
                    twin.Graph = graph;
                if (!twin.IsPlotted)
                    twin.IsPlotted = true;
                graph ??= twin.Graph;
            }
        }
        finally
        {
            _suppressPlotEvents = false;
        }
        RaisePlotChanged(PlotChange.Curves);
    }

    /// <summary>Several files: the curve names show their file, and the list groups the curves by file.</summary>
    private void UpdateSourceDisplay()
    {
        bool several = Sources.Count > 1;
        foreach (var source in Sources)
            source.ShowName = several;
        foreach (var item in Channels)
            item.RefreshSourceLabel();
        if (several && ChannelsView.GroupDescriptions.Count == 0)
            ChannelsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ChannelItemViewModel.Source)));
        else if (!several && ChannelsView.GroupDescriptions.Count > 0)
            ChannelsView.GroupDescriptions.Clear();
        OnPropertyChanged(nameof(HasSeveralFiles));
        AddCompareFileCommand.NotifyCanExecuteChanged();
        RefreshCursorGroups();
    }

    /// <summary>The offset of a file was typed: redrawn with the same view, one undo step.</summary>
    private void OnSourceOffsetChanged(DataSource source) => RaisePlotChanged(PlotChange.Sources);
}
