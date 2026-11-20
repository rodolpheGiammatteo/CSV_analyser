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
            SourcePath = Dataset?.FilePath,
            XAxis = SelectedXAxis?.Channel?.SourceName ?? "",
            GraphColumns = GraphColumns > 1 ? GraphColumns : null,
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
                HeightFactor = graph.HeightFactor != 1 ? graph.HeightFactor : null,
            };
            var used = layout.FirstOrDefault(s => s.Graph == graph)?.Axes.Select(a => a.Key).ToHashSet() ?? [];
            foreach (var (key, settings) in graph.AxisSettings)
            {
                bool customized = settings.IsManual || settings.AlignValue != 0 || settings.Side != AxisSide.Auto || settings.Title.Trim().Length > 0;
                if (!customized && !used.Contains(key))
                    continue;
                var axis = new AxisConfig
                {
                    Key = key, Manual = settings.IsManual, Min = settings.ParsedMin, Max = settings.ParsedMax,
                    AlignValue = settings.AlignValue != 0 ? settings.AlignValue : null,
                    Side = settings.Side != AxisSide.Auto ? settings.Side.ToString() : null,
                    Title = settings.Title.Trim().Length > 0 ? settings.Title.Trim() : null,
                };
                if (used.Contains(key) && PlotView?.AxisRange(graph, key) is { } r)
                    axis.View = new RangeConfig(r.Min, r.Max);
                g.Axes.Add(axis);
            }
            config.Graphs.Add(g);
        }

        foreach (var item in PlottedChannels.Concat(Channels.Where(c => !c.IsPlotted && !c.IsDefaultAppearance)))
            config.Curves.Add(ToConfig(item));
        config.Computed.AddRange(ComputedChannels.Select(c => c.Definition!.Clone()));
        if (IsMeasuring || MeasureA is not null || IsMeasuringY)
        {
            int yGraph = MeasureYGraph is { } mg ? Graphs.IndexOf(mg) : -1;
            config.Measure = new MeasureConfig
            {
                Enabled = IsMeasuring, A = MeasureA, B = MeasureB,
                YEnabled = IsMeasuringY,
                YGraph = yGraph >= 0 ? yGraph : null,
                YAxis = yGraph >= 0 ? MeasureYAxisKey : null,
                Y1 = yGraph >= 0 ? MeasureY1 : null,
                Y2 = yGraph >= 0 ? MeasureY2 : null,
            };
        }
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
            Opacity = item.Opacity != 1 ? item.Opacity : null,
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
                if (gc.HeightFactor is { } hf && double.IsFinite(hf))
                    graph.HeightFactor = Math.Clamp(hf, 0.1, 10);
                foreach (var ac in gc.Axes)
                {
                    var axisSettings = graph.GetAxisSettings(ac.Key);
                    axisSettings.Apply(ac.Manual, ac.Min, ac.Max);
                    axisSettings.AlignValueText = ac.AlignValue is { } av ? av.ToString("R", System.Globalization.CultureInfo.CurrentCulture) : "0";
                    axisSettings.Side = Enum.TryParse<AxisSide>(ac.Side, out var side) ? side : AxisSide.Auto;
                    axisSettings.Title = ac.Title ?? "";
                }
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
                if (cc.Opacity is { } opacity && double.IsFinite(opacity))
                    item.Opacity = Math.Clamp(opacity, 0.1, 1);
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
            GraphColumns = Math.Clamp(config.GraphColumns ?? 1, 1, 4);
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
            if (measure.YGraph is { } yg && yg >= 0 && yg < created.Count && Graphs.Contains(created[yg]) && measure.YAxis is { } yAxis)
            {
                MeasureYGraph = created[yg];
                MeasureYAxisKey = yAxis;
                MeasureY1 = restoreView ? measure.Y1 : null;
                MeasureY2 = restoreView ? measure.Y2 : null;
            }
            if (IsMeasuringY != measure.YEnabled)
                IsMeasuringY = measure.YEnabled;
            if (IsMeasuring == measure.Enabled)
                RaisePlotChanged(PlotChange.Measure);
            else
                IsMeasuring = measure.Enabled;
        }
        return missing;
    }

    /// <summary>
    /// Back to the state of a first opening: one graph, no curve, original names and styles, no computed curve,
    /// default X axis, no measurement cursor (right-click on the eraser). Undoable with Ctrl+Z.
    /// </summary>
    [RelayCommand]
    private void ResetLayout()
    {
        if (Dataset is null)
            return;
        var defaultX = XAxisOptions.FirstOrDefault();
        IsMeasuring = false;
        IsMeasuringY = false;
        ApplyConfiguration(new ViewConfiguration { XAxis = defaultX?.Channel?.SourceName ?? "" }, restoreView: false);
        CommitNow();
        StatusText = L.T("Mise en page réinitialisée (Ctrl+Z pour annuler)");
    }

    /// <summary>Every Y axis of every graph back to automatic (typed bounds dropped), then everything fitted (right-click on the fit buttons).</summary>
    [RelayCommand]
    private void AutoAllAxes()
    {
        if (Dataset is null)
            return;
        _suppressPlotEvents = true;
        try
        {
            foreach (var graph in Graphs)
            {
                foreach (var (_, settings) in graph.AxisSettings)
                    settings.IsAuto = true;
            }
        }
        finally
        {
            _suppressPlotEvents = false;
        }
        RaisePlotChanged(PlotChange.AutoScale); // one fit (and one undo step)
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
        StatusText += L.T(" — mise en page précédente retrouvée");
    }

    // ---------------------------------------------------------------- projects

    /// <summary>The project file being edited (null until saved or opened).</summary>
    public string? ProjectPath { get; private set; }

    /// <summary>Ctrl+S: saves the project (CSV path + layout) where it was, or asks where the first time.</summary>
    [RelayCommand]
    private void SaveProject() => TrySaveProject(saveAs: false);

    [RelayCommand]
    private void SaveProjectAs() => TrySaveProject(saveAs: true);

    /// <summary>Returns false when the user cancelled the dialog or the file could not be written.</summary>
    public bool TrySaveProject(bool saveAs)
    {
        if (Dataset is not { } ds)
        {
            ShowNeedsFile();
            return false;
        }
        string? path = saveAs ? null : ProjectPath;
        if (path is null)
        {
            var dialog = new SaveFileDialog
            {
                Title = L.T("Enregistrer le projet"),
                Filter = "Projet CSV Analyser (*.json)|*.json",
                FileName = ProjectPath is { } current ? Path.GetFileName(current) : Path.GetFileNameWithoutExtension(ds.FilePath) + " - projet.json",
                InitialDirectory = ExistingFolder(Path.GetDirectoryName(ProjectPath)) ?? ExistingFolder(_settings.LastConfigDirectory) ?? Path.GetDirectoryName(ds.FilePath),
            };
            if (dialog.ShowDialog() != true)
                return false;
            path = dialog.FileName;
        }
        try
        {
            CaptureConfiguration().Save(path);
            ProjectPath = path;
            _settings.LastConfigDirectory = Path.GetDirectoryName(path);
            _settings.Save();
            MarkSaved();
            StatusText = L.F("Projet enregistré : {0}", Path.GetFileName(path));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, L.T("Enregistrement impossible"), MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// Before closing or opening something else: offers to save the project when something changed.
    /// Returns false when the user cancels.
    /// </summary>
    public bool ConfirmSaveChanges(string action)
    {
        if (!IsDirty)
            return true;
        string name = ProjectPath is { } p ? L.F("le projet « {0} »", Path.GetFileNameWithoutExtension(p)) : L.T("le projet (fichier CSV et mise en page)");
        var answer = MessageBox.Show(
            L.F("Enregistrer {0} avant {1} ?\n\nOui : enregistrer\nNon : continuer sans enregistrer\nAnnuler : revenir", name, action),
            L.T("Modifications non enregistrées"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => TrySaveProject(saveAs: false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    [RelayCommand]
    private async Task OpenProject()
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("Ouvrir un projet"),
            Filter = L.T("Projet CSV Analyser (*.json)|*.json|Tous les fichiers (*.*)|*.*"),
            InitialDirectory = ExistingFolder(_settings.LastConfigDirectory) ?? ExistingFolder(_settings.LastDirectory),
        };
        if (dialog.ShowDialog() == true)
            await OpenProjectFileAsync(dialog.FileName);
    }

    /// <summary>
    /// Opens a project: its CSV file (found by its path, next to the project, or asked) with its layout.
    /// When another CSV is open, the user may instead apply the layout to that file.
    /// </summary>
    public async Task OpenProjectFileAsync(string path)
    {
        ViewConfiguration config;
        try
        {
            config = ViewConfiguration.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(ex.Message, L.T("Projet illisible"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        string? csv = FindProjectCsv(config, path);
        if (Dataset is { } ds)
        {
            bool same = csv is not null && string.Equals(Path.GetFullPath(csv), Path.GetFullPath(ds.FilePath), StringComparison.OrdinalIgnoreCase);
            if (!same)
            {
                string csvName = config.SourceFile ?? (csv is null ? "?" : Path.GetFileName(csv));
                var answer = MessageBox.Show(
                    L.F("Ce projet a été fait avec « {0} ».\n\nOui : ouvrir « {0} » avec cette mise en page\nNon : appliquer la mise en page au fichier ouvert « {1} »\nAnnuler : ne rien faire", csvName, Path.GetFileName(ds.FilePath)),
                    L.T("Ouvrir le projet"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel)
                    return;
                if (answer == MessageBoxResult.No)
                {
                    ApplyConfigurationFile(path);
                    return;
                }
            }
            if (!ConfirmSaveChanges(L.T("d'ouvrir ce projet")))
                return;
            if (same)
            {
                ApplyLoadedProject(config, path);
                return;
            }
        }

        csv ??= AskForCsv(config);
        if (csv is null)
            return;
        if (await LoadCsvAsync(csv, restoreSession: false))
            ApplyLoadedProject(config, path);
    }

    private void ApplyLoadedProject(ViewConfiguration config, string path)
    {
        var missing = ApplyConfiguration(config, restoreView: true);
        ProjectPath = path;
        _settings.LastConfigDirectory = Path.GetDirectoryName(path);
        _settings.Save();
        ResetHistory();
        StatusText = L.F("Projet ouvert : {0}", Path.GetFileName(path)) + (missing.Count > 0 ? L.F(" — {0} élément(s) ignoré(s)", missing.Count) : "");
        ReportMissing(missing);
    }

    /// <summary>The CSV of a project: its saved path, else a file of the same name next to the project.</summary>
    private static string? FindProjectCsv(ViewConfiguration config, string projectPath)
    {
        if (config.SourcePath is { } saved && File.Exists(saved))
            return saved;
        if (config.SourceFile is { } name && Path.GetDirectoryName(projectPath) is { } folder && File.Exists(Path.Combine(folder, name)))
            return Path.Combine(folder, name);
        return null;
    }

    private string? AskForCsv(ViewConfiguration config)
    {
        string name = config.SourceFile ?? L.T("le fichier CSV");
        if (MessageBox.Show(L.F("« {0} » est introuvable (déplacé ou renommé ?).\nVoulez-vous le chercher ?", name), L.T("Fichier introuvable"),
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return null;
        var dialog = new OpenFileDialog
        {
            Title = L.F("Où est « {0} » ?", name),
            Filter = L.T("Fichiers CSV (*.csv;*.txt)|*.csv;*.txt|Tous les fichiers (*.*)|*.*"),
            FileName = config.SourceFile ?? "",
            InitialDirectory = ExistingFolder(Path.GetDirectoryName(config.SourcePath)) ?? ExistingFolder(_settings.LastDirectory),
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
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
            Title = L.T("Appliquer une configuration au fichier ouvert"),
            Filter = L.T("Projet ou configuration CSV Analyser (*.json)|*.json|Tous les fichiers (*.*)|*.*"),
            InitialDirectory = ExistingFolder(_settings.LastConfigDirectory) ?? ExistingFolder(_settings.LastDirectory),
        };
        if (dialog.ShowDialog() == true)
            ApplyConfigurationFile(dialog.FileName);
    }

    /// <summary>Applies the layout of a project or configuration to the open file (its own CSV is not opened).</summary>
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
            CommitNow(); // undoable
            _settings.LastConfigDirectory = Path.GetDirectoryName(path);
            _settings.Save();
            StatusText = L.F("Configuration appliquée : {0}", Path.GetFileName(path))
                + (missing.Count > 0 ? L.F(" — {0} élément(s) ignoré(s)", missing.Count) : "");
            ReportMissing(missing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(ex.Message, L.T("Configuration illisible"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ReportMissing(IReadOnlyList<string> missing)
    {
        if (missing.Count == 0 || Dataset is not { } ds)
            return;
        string list = string.Join("\n", missing.Take(15).Select(m => "• " + m));
        if (missing.Count > 15)
            list += L.F("\n… et {0} autre(s)", missing.Count - 15);
        MessageBox.Show(
            L.F("Ces colonnes n'existent pas dans {0} ; elles ont été ignorées :\n\n{1}", Path.GetFileName(ds.FilePath), list),
            L.T("Mise en page appliquée"), MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private static void ShowNeedsFile() =>
        MessageBox.Show(L.T("Ouvrez d'abord un fichier CSV."), L.T("Aucun fichier"), MessageBoxButton.OK, MessageBoxImage.Information);

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
            new(option.Channel is { } ch ? Header(ch) : L.T("N° de ligne"), option.GetValues(ds)),
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
        StatusText = L.F("Export vers {0}…", Path.GetFileName(path));
        var progress = new Progress<double>(p => LoadProgress = p * 100);
        try
        {
            await Task.Run(() =>
            {
                // BOM: Excel then reads accents correctly.
                using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16);
                CsvExporter.Write(writer, columns, rows, options, progress);
            });
            StatusText = L.F("Exporté : {0} — {1:N0} lignes × {2} colonnes", Path.GetFileName(path), CountRows(rows), columns.Count + (options.DateColumn is null ? 0 : 1));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = L.F("Export impossible : {0}", ex.Message);
            MessageBox.Show(ex.Message, L.T("Export impossible"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
