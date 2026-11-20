using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CsvAnalyser.App.ViewModels;

public enum LegendCorner
{
    UpperRight,
    UpperLeft,
    LowerRight,
    LowerLeft,
    Hidden,
}

/// <summary>One of the stacked graphs. Curves point to their graph (<see cref="ChannelItemViewModel.Graph"/>).</summary>
public sealed partial class GraphViewModel : ObservableObject
{
    private readonly Action _onSettingsChanged;
    private readonly Action _onAppearanceChanged;
    private readonly Dictionary<string, AxisRangeSettings> _axes = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private int _number;

    /// <summary>Title given by the user, drawn above the graph (empty = none, "Graphe n" in lists).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _name = "";

    [ObservableProperty]
    private bool _alignZeros;

    [ObservableProperty]
    private LegendCorner _legendCorner = LegendCorner.UpperRight;

    /// <summary>
    /// Distance of the legend from its corner (device-independent units), set by dragging it;
    /// null = ScottPlot's default margin.
    /// </summary>
    public (double X, double Y)? LegendOffset { get; set; }

    public GraphViewModel(int id, Action onSettingsChanged, Action onAppearanceChanged)
    {
        Id = id;
        _onSettingsChanged = onSettingsChanged;
        _onAppearanceChanged = onAppearanceChanged;
    }

    /// <summary>Stable identity (the number changes when an upper graph is removed).</summary>
    public int Id { get; }

    public string Title => string.IsNullOrWhiteSpace(Name) ? $"Graphe {Number}" : Name.Trim();

    public static IReadOnlyList<StyleChoice<LegendCorner>> LegendChoices { get; } =
    [
        new(LegendCorner.UpperRight, "En haut à droite"),
        new(LegendCorner.UpperLeft, "En haut à gauche"),
        new(LegendCorner.LowerRight, "En bas à droite"),
        new(LegendCorner.LowerLeft, "En bas à gauche"),
        new(LegendCorner.Hidden, "Masquée"),
    ];

    partial void OnNameChanged(string value) => _onAppearanceChanged();

    partial void OnLegendCornerChanged(LegendCorner value)
    {
        LegendOffset = null; // A corner picked from the list starts at the default margin.
        _onAppearanceChanged();
    }

    /// <summary>Range settings of the axis <paramref name="key"/> (see <see cref="GraphLayout.AxisKey"/>), created on first use.</summary>
    public AxisRangeSettings GetAxisSettings(string key)
    {
        if (!_axes.TryGetValue(key, out var settings))
            _axes[key] = settings = new AxisRangeSettings(_onSettingsChanged);
        return settings;
    }

    public bool TryGetAxisSettings(string key, out AxisRangeSettings settings) => _axes.TryGetValue(key, out settings!);

    partial void OnAlignZerosChanged(bool value) => _onSettingsChanged();
}

/// <summary>Automatic (fit the data) or manual range of one Y axis.</summary>
public sealed partial class AxisRangeSettings : ObservableObject
{
    private readonly Action _onChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuto))]
    private bool _isManual;

    [ObservableProperty]
    private string _minText = "";

    [ObservableProperty]
    private string _maxText = "";

    public AxisRangeSettings(Action onChanged) => _onChanged = onChanged;

    public bool IsAuto
    {
        get => !IsManual;
        set => IsManual = !value;
    }

    /// <summary>The manual range, when manual mode is on and both bounds are valid numbers with min &lt; max.</summary>
    public (double Min, double Max)? ManualRange =>
        IsManual && TryParse(MinText, out double min) && TryParse(MaxText, out double max) && min < max
            ? (min, max)
            : null;

    public bool HasError => IsManual && ManualRange is null;

    public static string Format(double value) => value.ToString("G6", CultureInfo.CurrentCulture);

    /// <summary>Fills empty bounds with the range currently shown, without touching the plot.</summary>
    public void PrefillIfEmpty(double min, double max)
    {
        if (!string.IsNullOrWhiteSpace(MinText) || !string.IsNullOrWhiteSpace(MaxText))
            return;
#pragma warning disable MVVMTK0034 // Writing the fields on purpose: no change notification to the plot.
        _minText = Format(min);
        _maxText = Format(max);
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(MinText));
        OnPropertyChanged(nameof(MaxText));
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    partial void OnIsManualChanged(bool value) => Changed();

    // Typing a bound means "use it": switch to manual (which notifies the plot by itself).
    partial void OnMinTextChanged(string value) => ManualOrChanged();

    partial void OnMaxTextChanged(string value) => ManualOrChanged();

    private void ManualOrChanged()
    {
        if (IsManual)
            Changed();
        else
            IsManual = true;
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(HasError));
        _onChanged();
    }
}
