using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsvAnalyser.App.Services;
using CsvAnalyser.Core.Configuration;
using CsvAnalyser.Core.Data;
using CsvAnalyser.Core.Export;
using Microsoft.Win32;

namespace CsvAnalyser.App.ViewModels;

/// <summary>Configurations ("recipes"), the per-file session and the CSV export.</summary>
public partial class MainViewModel
{
    /// <summary>Set by the plot controller.</summary>
    public IPlotView? PlotView { get; set; }

    /// <summary>Reopening a file restores the layout it had when it was left.</summary>
    [ObservableProperty]
    private bool _restoreLayout;

    partial void OnRestoreLayoutChanged(bool value)
    {
        _settings.RestoreLayout = value;
        _settings.Save();
    }

    // ---------------------------------------------------------------- capture

    public ViewConfiguration CaptureConfiguration()
    {
        var config = new ViewConfiguration
        {
            SourceFile = Dataset is { } ds ? Path.GetFileName(ds.FilePath) : null,
            XAxis = SelectedXAxis?.Channel?.SourceName ?? "",
        };
        if (PlottedChannels.Count > 0 && PlotView?.XRange is { } x)
            config.XRange = new RangeConfig(x.Min, x.Max);

        var layout = BuildLayout();
        foreach (var graph in Graphs)
        {
            var g = new GraphConfig
            {
                Title = string.IsNullOrWhiteSpace(graph.Name) ? null : graph.Name.Trim(),
                AlignZeros = graph.AlignZeros,
                Legend = graph.LegendCorner.ToString(),
                LegendOffsetX = graph.LegendOffset?.X,
                LegendOffsetY = graph.LegendOffset?.Y,
            };
            var used = layout.FirstOrDefault(s => s.Graph == graph)?.Axes.Select(a => a.Key).ToHashSet() ?? [];
            foreach (var (key, settings) in graph.AxisSettings)
            {
                if (!settings.IsManual && !used.Contains(key))
                    continue;
                var axis = new AxisConfig { Key = key, Manual = settings.IsManual, Min = settings.ParsedMin, Max = settings.ParsedMax };
                if (used.Contains(key) && PlotView?.AxisRange(graph, key) is { } r)
                    axis.View = new RangeConfig(r.Min, r.Max);
                g.Axes.Add(axis);
            }
            config.Graphs.Add(g);
        }

        foreach (var item in PlottedChannels.Concat(Channels.Where(c => !c.IsPlotted && !c.IsDefaultAppearance)))
            config.Curves.Add(ToConfig(item));
        config.Computed.AddRange(ComputedChannels.Select(c => c.Definition!.Clone()));
        if (IsMeasuring || MeasureA is not null)
            config.Measure = new MeasureConfig { Enabled = IsMeasuring, A = MeasureA, B = MeasureB };
        return config;
    }

    private CurveConfig ToConfig(ChannelItemViewModel item)
    {
        int graphIndex = item.Graph is { } graph ? Graphs.IndexOf(graph) : -1;
        return new CurveConfig
        {
            Column = item.Channel.SourceName,
            Name = item.Name != item.OriginalName ? item.Name : null,
            Unit = item.Unit != item.OriginalUnit ? item.Unit : null,
            Plotted = item.IsPlotted,
            Graph = graphIndex >= 0 ? graphIndex : null,
            Axis = item.AxisChoice == ChannelItemViewModel.AutoAxis ? null : item.AxisChoice,
            Color = item.IsColorCustom ? item.ColorHex : null,
            Line = item.Line != CurveLine.Solid ? item.Line.ToString() : null,
            LineWidth = item.LineWidth != CurveStyleChoices.DefaultLineWidth ? item.LineWidth : null,
            Marker = item.Marker != CurveMarker.None ? item.Marker.ToString() : null,
            MarkerSize = item.MarkerSize != CurveStyleChoices.DefaultMarkerSize ? item.MarkerSize : null,
        };
    }

    // ---------------------------------------------------------------- apply

    /// <summary>
    /// Replaces the current layout by <paramref name="config"/>. Columns absent from the file are ignored and returned.
    /// <paramref name="restoreView"/> also restores the zoom (same file only).
    /// </summary>
    public IReadOnlyList<string> ApplyConfiguration(ViewConfiguration config, bool restoreView)
    {
        if (Dataset is null)
            return [];

        var missing = config.MissingColumns(Channels.Where(c => !c.IsComputed).Select(c => c.Channel.SourceName)).ToList();
        var created = new List<GraphViewModel>();

        _suppressPlotEvents = true;
        try
        {
            foreach (var item in PlottedChannels.ToList())
                item.IsPlotted = false;
            foreach (var item in Channels)
            {
                if (!item.IsDefaultAppearance)
                    item.ResetAppearanceCommand.Execute(null);
                item.Graph = null;
            }

            // Names and units first: a formula may use a renamed curve.
            foreach (var cc in config.Curves)
            {
                if (Channels.FirstOrDefault(c => !c.IsComputed && c.Channel.SourceName == cc.Column) is { } item)
                {
                    if (cc.Name is { Length: > 0 } name)
                        item.Name = name;
                    if (cc.Unit is { } unit)
                        item.Unit = unit;
                }
            }
            missing.AddRange(RebuildComputed(config.Computed));

            var bySource = new Dictionary<string, ChannelItemViewModel>(StringComparer.Ordinal);
            foreach (var item in Channels)
                bySource.TryAdd(item.Channel.SourceName, item);

            Graphs.Clear();
            foreach (var gc in config.Graphs.Count > 0 ? config.Graphs : [new GraphConfig()])
            {
                var graph = NewGraph();
                graph.Name = gc.Title ?? "";
                graph.AlignZeros = gc.AlignZeros;
                if (Enum.TryParse<LegendCorner>(gc.Legend, out var corner))
                    graph.LegendCorner = corner;
                if (gc.LegendOffsetX is { } ox && gc.LegendOffsetY is { } oy)
                    graph.LegendOffset = (ox, oy);
                foreach (var ac in gc.Axes)
                    graph.GetAxisSettings(ac.Key).Apply(ac.Manual, ac.Min, ac.Max);
                Graphs.Add(graph);
                created.Add(graph);
            }

            foreach (var cc in config.Curves)
            {
                if (!bySource.TryGetValue(cc.Column, out var item))
                    continue;
                if (cc.Name is { Length: > 0 } name)
                    item.Name = name;
                if (cc.Unit is { } unit)
                    item.Unit = unit;
                item.AxisChoice = cc.Axis ?? ChannelItemViewModel.AutoAxis;
                if (Enum.TryParse<CurveLine>(cc.Line, out var line))
                    item.Line = line;
                if (cc.LineWidth is { } width && double.IsFinite(width))
                    item.LineWidth = Math.Clamp(width, 0.5, 6);
                if (Enum.TryParse<CurveMarker>(cc.Marker, out var marker))
                    item.Marker = marker;
                if (cc.MarkerSize is { } size && double.IsFinite(size))
                    item.MarkerSize = Math.Clamp(size, 2, 16);
                if (cc.Color is { } hex)
                    item.ColorHex = hex;
                item.Graph = cc.Graph is { } gi && gi >= 0 && gi < created.Count ? created[gi] : created[0];
                if (cc.Plotted)
                    item.IsPlotted = true;
            }

            var xOption = config.XAxis switch
            {
                null => null,
                "" => XAxisOptions.FirstOrDefault(o => o.Channel is null),
                var column => XAxisOptions.FirstOrDefault(o => o.Channel?.SourceName == column),
            };
            if (xOption is not null)
                SelectedXAxis = xOption;
            RemoveEmptyGraphs();
        }
        finally
        {
            _suppressPlotEvents = false;
        }
        RaisePlotChanged(PlotChange.Dataset);

        if (restoreView && PlotView is { } view && PlottedChannels.Count > 0)
        {
            var y = new List<(GraphViewModel, string, double, double)>();
            for (int i = 0; i < config.Graphs.Count && i < created.Count; i++)
            {
                if (!Graphs.Contains(created[i]))
                    continue;
                foreach (var ac in config.Graphs[i].Axes)
                {
                    if (ac.View is { IsValid: true } r)
                        y.Add((created[i], ac.Key, r.Min, r.Max));
                }
            }
            view.SetView(config.XRange is { IsValid: true } x ? (x.Min, x.Max) : null, y);
        }

        if (config.Measure is { } measure)
        {
            if (restoreView && measure.A is { } a && measure.B is { } b && double.IsFinite(a) && double.IsFinite(b))
                SetMeasureCursors(a, b);
            if (IsMeasuring == measure.Enabled)
                RaisePlotChanged(PlotChange.Measure);
            else
                IsMeasuring = measure.Enabled;
        }
        return missing;
    }

    // ---------------------------------------------------------------- session (per file)

    /// <summary>Remembers the layout of the open file (before opening another one, and when the app closes).</summary>
    public void SaveSession()
    {
        if (Dataset is { } ds && RestoreLayout)
            SessionStore.Save(ds.FilePath, CaptureConfiguration());
    }

    private void RestoreSession(string path)
    {
        if (!RestoreLayout || SessionStore.TryLoad(path) is not { } session)
            return;
        ApplyConfiguration(session, restoreView: true);
        StatusText += " — mise en page précédente retrouvée";
    }

    // ---------------------------------------------------------------- recipes

    [RelayCommand]
    private void SaveConfiguration()
    {
        if (Dataset is not { } ds)
        {
            ShowNeedsFile();
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Enregistrer la configuration",
            Filter = "Configuration CSV Analyser (*.json)|*.json",
            FileName = Path.GetFileNameWithoutExtension(ds.FilePath) + " - configuration.json",
            InitialDirectory = ExistingFolder(_settings.LastConfigDirectory) ?? Path.GetDirectoryName(ds.FilePath),
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            CaptureConfiguration().Save(dialog.FileName);
            _settings.LastConfigDirectory = Path.GetDirectoryName(dialog.FileName);
            _settings.Save();
            StatusText = $"Configuration enregistrée : {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Enregistrement impossible", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void OpenConfiguration()
    {
        if (Dataset is null)
        {
            ShowNeedsFile();
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "Appliquer une configuration",
            Filter = "Configuration CSV Analyser (*.json)|*.json|Tous les fichiers (*.*)|*.*",
            InitialDirectory = ExistingFolder(_settings.LastConfigDirectory) ?? ExistingFolder(_settings.LastDirectory),
        };
        if (dialog.ShowDialog() == true)
            ApplyConfigurationFile(dialog.FileName);
    }

    /// <summary>Applies a saved configuration to the open file (menu, or a .json file dropped on the window).</summary>
    public void ApplyConfigurationFile(string path)
    {
        if (Dataset is not { } ds)
        {
            ShowNeedsFile();
            return;
        }
        try
        {
            var config = ViewConfiguration.Load(path);
            bool sameFile = string.Equals(config.SourceFile, Path.GetFileName(ds.FilePath), StringComparison.OrdinalIgnoreCase);
            var missing = ApplyConfiguration(config, restoreView: sameFile);
            _settings.LastConfigDirectory = Path.GetDirectoryName(path);
            _settings.Save();
            StatusText = $"Configuration appliquée : {Path.GetFileName(path)}"
                + (missing.Count > 0 ? $" — {missing.Count} colonne(s) absente(s) ignorée(s)" : "");
            if (missing.Count > 0)
            {
                string list = string.Join("\n", missing.Take(15).Select(m => "• " + m));
                if (missing.Count > 15)
                    list += $"\n… et {missing.Count - 15} autre(s)";
                MessageBox.Show(
                    $"Ces colonnes de la configuration n'existent pas dans {Path.GetFileName(ds.FilePath)} ; elles ont été ignorées :\n\n{list}",
                    "Configuration appliquée", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(ex.Message, "Configuration illisible", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void ShowNeedsFile() =>
        MessageBox.Show("Ouvrez d'abord un fichier CSV.", "Aucun fichier", MessageBoxButton.OK, MessageBoxImage.Information);

    private static string? ExistingFolder(string? folder) => folder is not null && Directory.Exists(folder) ? folder : null;

    // ---------------------------------------------------------------- CSV export

    /// <summary>Opens the export window (handled by the main window).</summary>
    public event EventHandler? ExportRequested;

    [RelayCommand]
    private void Export()
    {
        if (Dataset is null)
            ShowNeedsFile();
        else
            ExportRequested?.Invoke(this, EventArgs.Empty);
    }

    public string ExportDelimiter
    {
        get => _settings.ExportDelimiter ?? ";";
        set
        {
            _settings.ExportDelimiter = value;
            _settings.Save();
        }
    }

    public string ExportDecimal
    {
        get => _settings.ExportDecimal ?? ",";
        set
        {
            _settings.ExportDecimal = value;
            _settings.Save();
        }
    }

    /// <summary>The rows whose X value is inside [min, max] on the current X axis.</summary>
    public ExportRows RowsInXRange(double min, double max)
    {
        var ds = Dataset!;
        var option = SelectedXAxis!;
        var xs = option.GetValues(ds);
        if (option.IsSorted)
            return new ExportRows(Core.Analysis.SortedSearch.LowerBound(xs, min), Core.Analysis.SortedSearch.UpperBound(xs, max));
        return new ExportRows(0, xs.Length, row => xs[row] >= min && xs[row] <= max);
    }

    public static int CountRows(ExportRows rows)
    {
        if (rows.Filter is not { } filter)
            return Math.Max(0, rows.End - rows.Start);
        int count = 0;
        for (int i = rows.Start; i < rows.End; i++)
        {
            if (filter(i))
                count++;
        }
        return count;
    }

    /// <summary>The X column first, then the chosen curves (the X column is not repeated).</summary>
    public IReadOnlyList<ExportColumn> ExportColumnsFor(bool plottedOnly)
    {
        var ds = Dataset!;
        var option = SelectedXAxis!;
        var columns = new List<ExportColumn>
        {
            new(option.Channel is { } ch ? Header(ch) : "N° de ligne", option.GetValues(ds)),
        };
        var curves = plottedOnly ? PlottedChannels.AsEnumerable() : Channels;
        foreach (var item in curves.Where(c => c.Channel != option.Channel))
            columns.Add(new ExportColumn(Header(item.Channel), item.Channel.Values));
        return columns;

        static string Header(Channel c) => string.IsNullOrEmpty(c.Unit) ? c.DisplayName : $"{c.DisplayName} [{c.Unit}]";
    }

    public async Task ExportAsync(string path, IReadOnlyList<ExportColumn> columns, ExportRows rows, CsvExportOptions options)
    {
        if (IsLoading)
            return;
        IsLoading = true;
        LoadProgress = 0;
        StatusText = $"Export vers {Path.GetFileName(path)}…";
        var progress = new Progress<double>(p => LoadProgress = p * 100);
        try
        {
            await Task.Run(() =>
            {
                // BOM: Excel then reads accents correctly.
                using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16);
                CsvExporter.Write(writer, columns, rows, options, progress);
            });
            StatusText = $"Exporté : {Path.GetFileName(path)} — {CountRows(rows):N0} lignes × {columns.Count + (options.DateColumn is null ? 0 : 1)} colonnes";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = $"Export impossible : {ex.Message}";
            MessageBox.Show(ex.Message, "Export impossible", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
