using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsvAnalyser.Core.Configuration;

namespace CsvAnalyser.App.ViewModels;

/// <summary>
/// Annotations: markers, highlighted zones and notes on a point. They belong to the file (saved in its session and
/// in projects, never applied to another file), are drawn by the plot controller, and every change is an undo step.
/// </summary>
public partial class MainViewModel : IAnnotationHost
{
    public ObservableCollection<AnnotationViewModel> Annotations { get; } = [];

    /// <summary>Eye button of the annotation panel: every annotation shown or hidden (the list keeps them).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnnotationsHidden))]
    private bool _showAnnotations = true;

    public bool AnnotationsHidden => !ShowAnnotations;

    [RelayCommand]
    private void ToggleShowAnnotations() => ShowAnnotations = !ShowAnnotations;

    public bool HasAnnotations => Annotations.Count > 0;

    public bool HasZones => Annotations.Any(a => a.Kind == AnnotationKind.Zone);

    /// <summary>The plot asks to edit an annotation (opens its settings window).</summary>
    public event EventHandler<AnnotationViewModel>? EditAnnotationRequested;

    partial void OnShowAnnotationsChanged(bool value) => RaisePlotChanged(PlotChange.Annotations);

    public void OnAnnotationChanged(AnnotationViewModel annotation)
    {
        if (!_suppressPlotEvents)
            RaisePlotChanged(PlotChange.Annotations);
    }

    public string DescribeRow(int row)
    {
        if (Dataset is not { } ds || SelectedXAxis is not { } option)
            return "";
        var xs = option.GetValues(ds);
        if (row < 0 || row >= xs.Length)
            return "?";
        string value = xs[row].ToString("0.###", CultureInfo.CurrentCulture);
        if (option.Channel is { } ch && ch == ds.Time?.Channel)
            return $"t = {value} {ch.Unit}".TrimEnd();
        return option.Channel is null ? L.F("ligne {0}", value) : $"x = {value}";
    }

    /// <summary>The X value of a row on the current X axis (NaN when unknown).</summary>
    public double XOfRow(int row)
    {
        if (Dataset is not { } ds || SelectedXAxis is not { } option)
            return double.NaN;
        var xs = option.GetValues(ds);
        return row >= 0 && row < xs.Length ? xs[row] : double.NaN;
    }

    // ---------------------------------------------------------------- creation

    public AnnotationViewModel AddMarker(int row) => Add(new AnnotationViewModel(this, AnnotationKind.Marker, row));

    public AnnotationViewModel AddZone(int startRow, int endRow) =>
        Add(new AnnotationViewModel(this, AnnotationKind.Zone, Math.Min(startRow, endRow)) { EndRow = Math.Max(startRow, endRow) });

    public AnnotationViewModel AddNote(ChannelItemViewModel curve, int row) =>
        Add(new AnnotationViewModel(this, AnnotationKind.Note, row) { Curve = curve });

    private AnnotationViewModel Add(AnnotationViewModel annotation)
    {
        Annotations.Add(annotation);
        AnnotationsChanged();
        if (!ShowAnnotations)
            ShowAnnotations = true; // a new annotation must be seen
        return annotation;
    }

    /// <summary>Measure panel: a zone exactly between the cursors A and B.</summary>
    [RelayCommand]
    private void ZoneFromMeasure()
    {
        if (RowsBetweenCursors is not { } rows || rows.End <= rows.Start)
            return;
        var zone = AddZone(rows.Start, rows.End - 1);
        EditAnnotationRequested?.Invoke(this, zone);
    }

    // ---------------------------------------------------------------- list actions

    [RelayCommand]
    private void EditAnnotation(AnnotationViewModel? annotation)
    {
        if (annotation is not null)
            EditAnnotationRequested?.Invoke(this, annotation);
    }

    [RelayCommand]
    public void DeleteAnnotation(AnnotationViewModel? annotation)
    {
        if (annotation is null || !Annotations.Remove(annotation))
            return;
        AnnotationsChanged();
    }

    /// <summary>Shows the annotation: a zone fills the view with a margin, a marker or note is centered (zoom kept).</summary>
    [RelayCommand]
    private void GoToAnnotation(AnnotationViewModel? annotation)
    {
        if (annotation is null || PlotView is not { } view || view.XRange is not { } current)
            return;
        double x = XOfRow(annotation.Row);
        if (!double.IsFinite(x))
            return;
        if (annotation.Kind == AnnotationKind.Zone)
        {
            double x2 = XOfRow(annotation.EndRow);
            double min = Math.Min(x, x2), max = Math.Max(x, x2);
            double margin = Math.Max((max - min) * 0.15, (current.Max - current.Min) * 0.001);
            view.SetView((min - margin, max + margin), []);
        }
        else
        {
            double half = (current.Max - current.Min) / 2;
            view.SetView((x - half, x + half), []);
        }
        if (!annotation.IsVisible)
            annotation.IsVisible = true;
        if (!ShowAnnotations)
            ShowAnnotations = true;
    }

    /// <summary>Zone statistics: the cursors A and B are put on the zone ends and the measurements shown.</summary>
    [RelayCommand]
    private void MeasureZone(AnnotationViewModel? annotation)
    {
        if (annotation is not { Kind: AnnotationKind.Zone })
            return;
        double a = XOfRow(Math.Min(annotation.Row, annotation.EndRow)), b = XOfRow(Math.Max(annotation.Row, annotation.EndRow));
        if (!double.IsFinite(a) || !double.IsFinite(b))
            return;
        if (!IsMeasuring)
            IsMeasuring = true;
        SetMeasureCursors(a, b);
        RaisePlotChanged(PlotChange.Measure);
    }

    [RelayCommand]
    private void DeleteAllAnnotations()
    {
        if (Annotations.Count == 0)
            return;
        var answer = System.Windows.MessageBox.Show(L.F("Supprimer les {0} annotations ? (Ctrl+Z pour annuler)", Annotations.Count),
            L.T("Annotations"), System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.OK)
            return;
        Annotations.Clear();
        AnnotationsChanged();
    }

    /// <summary>Choices of the "graph" setting: every graph, or one of them.</summary>
    public IReadOnlyList<StyleChoice<GraphViewModel?>> AnnotationGraphChoices() =>
        [new(null, "Tous les graphes"), .. Graphs.Select(g => new StyleChoice<GraphViewModel?>(g, g.Title))];

    private void AnnotationsChanged()
    {
        OnPropertyChanged(nameof(HasAnnotations));
        OnPropertyChanged(nameof(HasZones));
        RaisePlotChanged(PlotChange.Annotations);
    }

    /// <summary>The "zone" column of the CSV export: the text of the zones containing each row.</summary>
    public Core.Export.ExportTextColumn ZoneExportColumn() =>
        new(L.T("zone"), AnnotationConfig.ZoneNames(Annotations.Where(a => a.Kind == AnnotationKind.Zone).Select(a => a.ToConfig(Graphs)).ToList()));

    // ---------------------------------------------------------------- configuration

    private List<AnnotationConfig>? CaptureAnnotations() =>
        Annotations.Count == 0 ? null : Annotations.Select(a => a.ToConfig(Graphs)).ToList();

    /// <summary>Replaces the annotations by those of a configuration (same file only), without raising plot events.</summary>
    private void LoadAnnotations(IReadOnlyList<AnnotationConfig>? configs)
    {
        Annotations.Clear();
        if (configs is not null && Dataset is { } ds)
        {
            foreach (var c in configs)
            {
                if (AnnotationViewModel.FromConfig(this, c, ds.RowCount, Graphs,
                        column => Channels.FirstOrDefault(ch => ch.Channel.SourceName == column)) is { } a)
                    Annotations.Add(a);
            }
        }
        OnPropertyChanged(nameof(HasAnnotations));
        OnPropertyChanged(nameof(HasZones));
    }

    /// <summary>The X axis changed: positions are shown in the new unit.</summary>
    private void RefreshAnnotationPositions()
    {
        foreach (var a in Annotations)
            a.RefreshPosition();
    }
}
