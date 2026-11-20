using System.Windows;
using CommunityToolkit.Mvvm.Input;
using CsvAnalyser.Core.Analysis;
using CsvAnalyser.Core.Configuration;
using CsvAnalyser.Core.Data;

namespace CsvAnalyser.App.ViewModels;

/// <summary>What the computed-curve window is asked to edit: a new curve (optionally from a source) or an existing one.</summary>
public sealed record ComputedCurveRequest(ChannelItemViewModel? Editing, ChannelItemViewModel? Source);

/// <summary>Computed curves (formulas and filters) and the spectrum window.</summary>
public partial class MainViewModel
{
    public event EventHandler<ComputedCurveRequest>? ComputedCurveRequested;

    /// <summary>Opens the spectrum window, for a graph's curves (null = the plotted curves).</summary>
    public event EventHandler<GraphViewModel?>? SpectrumRequested;

    public IEnumerable<ChannelItemViewModel> ComputedChannels => Channels.Where(c => c.IsComputed);

    [RelayCommand]
    private void NewComputed(ChannelItemViewModel? source)
    {
        if (Dataset is null)
            ShowNeedsFile();
        else
            ComputedCurveRequested?.Invoke(this, new ComputedCurveRequest(null, source));
    }

    [RelayCommand]
    private void EditComputed(ChannelItemViewModel? item)
    {
        if (item?.IsComputed == true)
            ComputedCurveRequested?.Invoke(this, new ComputedCurveRequest(item, null));
    }

    [RelayCommand]
    private void DeleteComputed(ChannelItemViewModel? item)
    {
        if (item?.Definition is null)
            return;
        var users = ComputedChannels.Where(c => c != item && UsesCurve(c.Definition!, item)).Select(c => c.Name).ToList();
        if (users.Count > 0)
        {
            MessageBox.Show($"« {item.Name} » est utilisée par : {string.Join(", ", users)}.\nSupprimez ou modifiez d'abord ces courbes.",
                "Suppression impossible", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"Supprimer la courbe calculée « {item.Name} » ?", "Supprimer", MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        item.IsPlotted = false;
        Channels.Remove(item);
        StatusText = $"Courbe calculée supprimée : {item.Name}";
    }

    [RelayCommand]
    private void ShowSpectrum(GraphViewModel? graph)
    {
        if (Dataset is null)
            ShowNeedsFile();
        else
            SpectrumRequested?.Invoke(this, graph);
    }

    /// <summary>Time used by the filters: the time column when it is ascending, else the row number.</summary>
    public double[] TimeBase()
    {
        var ds = Dataset!;
        if (ds.Time?.Channel is { IsSortedAscending: true } time)
            return time.Values;
        return XAxisOptions.First(o => o.Channel is null).GetValues(ds);
    }

    /// <summary>True when the filters work in seconds (else in rows).</summary>
    public bool HasTimeBase => Dataset?.Time?.Channel is { IsSortedAscending: true };

    /// <summary>A curve by column name, then by displayed name, then by "name [unit]".</summary>
    public ChannelItemViewModel? FindCurve(string name) =>
        Channels.FirstOrDefault(c => c.Channel.SourceName == name)
        ?? Channels.FirstOrDefault(c => c.Name == name)
        ?? Channels.FirstOrDefault(c => c.LegendText == name);

    private bool UsesCurve(ComputedCurveConfig def, ChannelItemViewModel item)
    {
        var inputs = def.Kind == ComputedCurveConfig.Formula
            ? TryVariables(def.Expression)
            : [def.Source ?? ""];
        return inputs.Any(n => FindCurve(n) == item);
    }

    private static IReadOnlyList<string> TryVariables(string? expression)
    {
        try
        {
            return Formula.Parse(expression ?? "").Variables;
        }
        catch (FormulaException)
        {
            return [];
        }
    }

    /// <summary>Computes the values of a definition; throws with a French message when impossible.</summary>
    public double[] ComputeValues(ComputedCurveConfig def)
    {
        var ds = Dataset ?? throw new InvalidOperationException("Aucun fichier ouvert.");
        if (def.Kind == ComputedCurveConfig.Formula)
        {
            var formula = Formula.Parse(def.Expression ?? "");
            var t = TimeBase();
            return formula.Evaluate(ds.RowCount, name =>
                name is "t" or "T" && FindCurve(name) is null ? t : FindCurve(name)?.Channel.Values);
        }

        var source = FindCurve(def.Source ?? "")?.Channel.Values
            ?? throw new InvalidOperationException($"Courbe inconnue : « {def.Source} ».");
        var time = TimeBase();
        double p = def.Parameter ?? double.NaN;
        return def.Kind switch
        {
            ComputedCurveConfig.MovingAverage when p > 0 => SignalFilters.MovingAverage(time, source, p),
            ComputedCurveConfig.LowPass when p > 0 => SignalFilters.LowPass(time, source, p, def.ZeroPhase),
            ComputedCurveConfig.HighPass when p > 0 => SignalFilters.HighPass(time, source, p, def.ZeroPhase),
            ComputedCurveConfig.Derivative => SignalFilters.Derivative(time, source),
            ComputedCurveConfig.Integral => SignalFilters.Integral(time, source),
            ComputedCurveConfig.MovingAverage => throw new InvalidOperationException("La largeur de la moyenne doit être un nombre positif."),
            ComputedCurveConfig.LowPass or ComputedCurveConfig.HighPass => throw new InvalidOperationException("La fréquence de coupure doit être un nombre positif."),
            _ => throw new InvalidOperationException($"Type de calcul inconnu : {def.Kind}."),
        };
    }

    /// <summary>
    /// Creates a computed curve, or replaces <paramref name="editing"/> (keeping its style, graph and place).
    /// Returns an error message, or null when done.
    /// </summary>
    public string? AddOrReplaceComputed(ComputedCurveConfig def, ChannelItemViewModel? editing)
    {
        def.Name = def.Name.Trim();
        if (def.Name.Length == 0)
            return "Donnez un nom à la courbe.";
        var clash = Channels.FirstOrDefault(c => c != editing
            && (c.Channel.SourceName == def.Name || c.Name == def.Name));
        if (clash is not null)
            return $"Une courbe s'appelle déjà « {def.Name} ».";
        if (editing is not null && UsesCurve(def, editing))
            return "Une courbe ne peut pas se calculer à partir d'elle-même.";

        double[] values;
        try
        {
            values = ComputeValues(def);
        }
        catch (Exception ex) when (ex is FormulaException or InvalidOperationException or ArgumentException)
        {
            return ex.Message;
        }

        var item = CreateComputedItem(def, values);
        if (editing is null)
        {
            Channels.Insert(ComputedChannels.Count(), item);
            item.IsPlotted = true;
            StatusText = $"Courbe calculée ajoutée : {def.Name}";
        }
        else
        {
            ReplaceItem(editing, item);
            RecomputeDependents(item);
            StatusText = $"Courbe calculée modifiée : {def.Name}";
        }
        return null;
    }

    private ChannelItemViewModel CreateComputedItem(ComputedCurveConfig def, double[] values) =>
        new(new Channel(def.Name, values) { Unit = def.Unit.Trim() }, this) { Definition = def.Clone() };

    /// <summary>Puts <paramref name="replacement"/> in place of <paramref name="old"/>: same list position, graph, style, plotted state.</summary>
    private void ReplaceItem(ChannelItemViewModel old, ChannelItemViewModel replacement)
    {
        _suppressPlotEvents = true;
        try
        {
            replacement.Graph = old.Graph;
            replacement.AxisChoice = old.AxisChoice;
            replacement.Line = old.Line;
            replacement.LineWidth = old.LineWidth;
            replacement.Marker = old.Marker;
            replacement.MarkerSize = old.MarkerSize;
            if (old.IsColorCustom)
                replacement.ColorHex = old.ColorHex;
            else
                replacement.Color = old.Color;

            int index = Channels.IndexOf(old);
            Channels[index] = replacement;
            int plotted = PlottedChannels.IndexOf(old);
            if (plotted >= 0)
            {
                PlottedChannels[plotted] = replacement;
                replacement.SetPlottedSilently(true);
            }
        }
        finally
        {
            _suppressPlotEvents = false;
        }
        RaisePlotChanged(PlotChange.Curves);
    }

    /// <summary>After an edit, the computed curves defined later are computed again (they may use the edited one).</summary>
    private void RecomputeDependents(ChannelItemViewModel edited)
    {
        bool after = false;
        foreach (var item in ComputedChannels.ToList())
        {
            if (item == edited)
            {
                after = true;
                continue;
            }
            if (!after)
                continue;
            try
            {
                ReplaceItem(item, CreateComputedItem(item.Definition!, ComputeValues(item.Definition!)));
            }
            catch (Exception ex) when (ex is FormulaException or InvalidOperationException or ArgumentException)
            {
                StatusText = $"« {item.Name} » n'a pas pu être recalculée : {ex.Message}";
            }
        }
    }

    /// <summary>Configurations: removes the computed curves, then creates those of the configuration. Returns the errors.</summary>
    private List<string> RebuildComputed(IReadOnlyList<ComputedCurveConfig> definitions)
    {
        foreach (var item in ComputedChannels.ToList())
            Channels.Remove(item);
        var errors = new List<string>();
        foreach (var def in definitions)
        {
            if (Channels.Any(c => c.Channel.SourceName == def.Name))
            {
                errors.Add($"{def.Name} (nom déjà pris)");
                continue;
            }
            try
            {
                Channels.Insert(ComputedChannels.Count(), CreateComputedItem(def, ComputeValues(def)));
            }
            catch (Exception ex) when (ex is FormulaException or InvalidOperationException or ArgumentException)
            {
                errors.Add($"{def.Name} (courbe calculée : {ex.Message})");
            }
        }
        return errors;
    }
}
