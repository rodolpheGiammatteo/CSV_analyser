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
            MessageBox.Show(L.F("« {0} » est utilisée par : {1}.\nSupprimez ou modifiez d'abord ces courbes.", item.Name, string.Join(", ", users)),
                L.T("Suppression impossible"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(L.F("Supprimer la courbe calculée « {0} » ?", item.Name), L.T("Supprimer"), MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        item.IsPlotted = false;
        Channels.Remove(item);
        StatusText = L.F("Courbe calculée supprimée : {0}", item.Name);
        // Its notes have nothing to point at any more (Ctrl+Z brings both back).
        var notes = Annotations.Where(a => a.Curve == item).ToList();
        foreach (var note in notes)
            Annotations.Remove(note);
        if (notes.Count > 0)
        {
            AnnotationsChanged();
            StatusText += " " + L.F("({0} note(s) supprimée(s))", notes.Count);
        }
    }

    [RelayCommand]
    private void DuplicateCurve(ChannelItemViewModel? item)
    {
        if (item is not null)
            DuplicateCurves([item]);
    }

    /// <summary>
    /// Copies of curves: a computed curve gets the same calculation, a file column becomes the formula
    /// « [column] »; the copy is named « … (copie) », keeps the style and graph and comes right after the computed curves.
    /// </summary>
    public void DuplicateCurves(IReadOnlyList<ChannelItemViewModel> items)
    {
        if (Dataset is null || items.Count == 0)
            return;
        var copies = new List<string>();
        foreach (var item in items)
        {
            var def = item.Definition?.Clone() ?? new ComputedCurveConfig
            {
                Kind = ComputedCurveConfig.Formula,
                Expression = "[" + item.Channel.SourceName + "]",
            };
            def.Name = CopyName(item.Name);
            def.Unit = item.Unit;
            double[] values;
            try
            {
                values = ComputeValues(def);
            }
            catch (Exception ex) when (ex is FormulaException or InvalidOperationException or ArgumentException)
            {
                StatusText = L.F("« {0} » n'a pas pu être dupliquée : {1}", item.Name, ex.Message);
                return;
            }
            var copy = CreateComputedItem(def, values);
            _suppressPlotEvents = true;
            try
            {
                CopyStyle(item, copy);
                Channels.Insert(ComputedChannels.Count(), copy);
                if (item.IsPlotted)
                    copy.IsPlotted = true;
            }
            finally
            {
                _suppressPlotEvents = false;
            }
            copies.Add(def.Name);
        }
        RaisePlotChanged(PlotChange.Curves);
        StatusText = copies.Count == 1
            ? L.F("Courbe dupliquée : {0}", copies[0])
            : L.F("{0} courbes dupliquées", copies.Count);
    }

    /// <summary>« name (copie) », « name (copie 2) »… whichever is free.</summary>
    private string CopyName(string name)
    {
        bool Taken(string candidate) => Channels.Any(c => c.Channel.SourceName == candidate || c.Name == candidate);
        string result = L.F("{0} (copie)", name);
        for (int k = 2; Taken(result); k++)
            result = L.F("{0} (copie {1})", name, k);
        return result;
    }

    private static void CopyStyle(ChannelItemViewModel from, ChannelItemViewModel to)
    {
        to.Graph = from.Graph;
        to.AxisChoice = from.AxisChoice;
        to.Line = from.Line;
        to.LineWidth = from.LineWidth;
        to.Marker = from.Marker;
        to.MarkerSize = from.MarkerSize;
        to.Opacity = from.Opacity;
        if (from.IsColorCustom)
            to.ColorHex = from.ColorHex;
        else
            to.Color = from.Color;
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
        var ds = Dataset ?? throw new InvalidOperationException(L.T("Aucun fichier ouvert."));
        if (def.Kind == ComputedCurveConfig.Formula)
        {
            var formula = Formula.Parse(def.Expression ?? "");
            var t = TimeBase();
            return formula.Evaluate(ds.RowCount, name =>
                name is "t" or "T" && FindCurve(name) is null ? t : FindCurve(name)?.Channel.Values);
        }

        var source = FindCurve(def.Source ?? "")?.Channel.Values
            ?? throw new InvalidOperationException(L.F("Courbe inconnue : « {0} ».", def.Source));
        var time = TimeBase();
        double p = def.Parameter ?? double.NaN;
        return def.Kind switch
        {
            ComputedCurveConfig.MovingAverage when p > 0 => SignalFilters.MovingAverage(time, source, p),
            ComputedCurveConfig.LowPass when p > 0 => SignalFilters.LowPass(time, source, p, def.ZeroPhase),
            ComputedCurveConfig.HighPass when p > 0 => SignalFilters.HighPass(time, source, p, def.ZeroPhase),
            ComputedCurveConfig.Derivative => SignalFilters.Derivative(time, source),
            ComputedCurveConfig.Integral => SignalFilters.Integral(time, source),
            ComputedCurveConfig.MovingAverage => throw new InvalidOperationException(L.T("La largeur de la moyenne doit être un nombre positif.")),
            ComputedCurveConfig.LowPass or ComputedCurveConfig.HighPass => throw new InvalidOperationException(L.T("La fréquence de coupure doit être un nombre positif.")),
            _ => throw new InvalidOperationException(L.F("Type de calcul inconnu : {0}.", def.Kind)),
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
            return L.T("Donnez un nom à la courbe.");
        var clash = Channels.FirstOrDefault(c => c != editing
            && (c.Channel.SourceName == def.Name || c.Name == def.Name));
        if (clash is not null)
            return L.F("Une courbe s'appelle déjà « {0} ».", def.Name);
        if (editing is not null && UsesCurve(def, editing))
            return L.T("Une courbe ne peut pas se calculer à partir d'elle-même.");

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
            StatusText = L.F("Courbe calculée ajoutée : {0}", def.Name);
        }
        else
        {
            ReplaceItem(editing, item);
            RecomputeDependents(item);
            StatusText = L.F("Courbe calculée modifiée : {0}", def.Name);
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
            CopyStyle(old, replacement);

            int index = Channels.IndexOf(old);
            Channels[index] = replacement;
            foreach (var note in Annotations.Where(a => a.Curve == old))
                note.Curve = replacement; // the notes stay on the edited curve
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
                StatusText = L.F("« {0} » n'a pas pu être recalculée : {1}", item.Name, ex.Message);
            }
        }
    }

    /// <summary>Configurations: removes the computed curves, then creates those of the configuration. Returns the errors.</summary>
    private List<string> RebuildComputed(IReadOnlyList<ComputedCurveConfig> definitions)
    {
        // A curve whose definition did not change is kept as is (undo/redo stays fast on big files).
        static string Key(ComputedCurveConfig d) => System.Text.Json.JsonSerializer.Serialize(d);
        var previous = new Dictionary<string, ChannelItemViewModel>();
        foreach (var item in ComputedChannels.ToList())
        {
            previous.TryAdd(Key(item.Definition!), item);
            Channels.Remove(item);
        }
        var errors = new List<string>();
        bool recomputed = false; // a later curve may use one that was recomputed: recompute the rest too
        foreach (var def in definitions)
        {
            if (Channels.Any(c => c.Channel.SourceName == def.Name))
            {
                errors.Add(L.F("{0} (nom déjà pris)", def.Name));
                continue;
            }
            if (!recomputed && previous.Remove(Key(def), out var kept))
            {
                Channels.Insert(ComputedChannels.Count(), kept);
                continue;
            }
            recomputed = true;
            try
            {
                Channels.Insert(ComputedChannels.Count(), CreateComputedItem(def, ComputeValues(def)));
            }
            catch (Exception ex) when (ex is FormulaException or InvalidOperationException or ArgumentException)
            {
                errors.Add(L.F("{0} (courbe calculée : {1})", def.Name, ex.Message));
            }
        }
        return errors;
    }
}
