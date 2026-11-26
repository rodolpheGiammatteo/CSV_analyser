using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsvAnalyser.Core.Data;

namespace CsvAnalyser.App.ViewModels;

/// <summary>What a curve needs from the main view model.</summary>
public interface IChannelHost
{
    void OnPlottedChanged(ChannelItemViewModel item);

    /// <summary>Name, unit, color or line/marker style changed.</summary>
    void OnAppearanceChanged(ChannelItemViewModel item);

    /// <summary>A theme palette color not used by another plotted curve.</summary>
    ScottPlot.Color PickAutoColor(ChannelItemViewModel item);
}

public partial class ChannelItemViewModel : ObservableObject
{
    private readonly IChannelHost _host;
    private readonly string _originalName;
    private readonly string _originalUnit;

    [ObservableProperty]
    private bool _isPlotted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Brush))]
    [NotifyPropertyChangedFor(nameof(ColorHex))]
    private ScottPlot.Color _color = ScottPlot.Colors.Gray;

    /// <summary>False while the color follows the theme palette; true once the user picked one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorModeLabel))]
    private bool _isColorCustom;

    public string ColorModeLabel => IsColorCustom ? L.T("choisie") : L.T("automatique (suit le thème)");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLineVisible))]
    private CurveLine _line = CurveLine.Solid;

    [ObservableProperty]
    private double _lineWidth = CurveStyleChoices.DefaultLineWidth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMarkers))]
    private CurveMarker _marker = CurveMarker.None;

    [ObservableProperty]
    private double _markerSize = CurveStyleChoices.DefaultMarkerSize;

    /// <summary>0 = a marker on every sample; else about one marker every this many pixels (readable on dense curves).</summary>
    [ObservableProperty]
    private int _markerSpacing;

    /// <summary>Opacity of the curve, 0.1 to 1 (a noisy curve made transparent no longer hides the others).</summary>
    [ObservableProperty]
    private double _opacity = 1;

    [ObservableProperty]
    private string _cursorText = "";

    /// <summary>The stacked graph showing this curve (kept when unchecked, so it comes back to the same place).</summary>
    [ObservableProperty]
    private GraphViewModel? _graph;

    /// <summary>
    /// Which Y axis of its graph the curve uses: <see cref="AutoAxis"/> (shared with the curves of the same unit),
    /// <see cref="OwnAxisChoice"/> (an axis of its own), or the key of another axis (to share it whatever the units).
    /// </summary>
    [ObservableProperty]
    private string _axisChoice = AutoAxis;

    public const string AutoAxis = "";
    public const string OwnAxisChoice = "own";

    public ChannelItemViewModel(Channel channel, IChannelHost host)
    {
        Channel = channel;
        _host = host;
        _originalName = channel.DisplayName;
        _originalUnit = channel.Unit;
    }

    public Channel Channel { get; }

    /// <summary>The file of this curve (the opened one, or a file added to compare); computed curves belong to the opened one.</summary>
    public DataSource? Source { get; set; }

    /// <summary>A column of a file added to compare (not the opened one).</summary>
    public bool IsCompared => Source is { IsPrimary: false };

    /// <summary>The name and, with several files, the file it comes from ("Vitesse — essai 2"): cursor panel.</summary>
    public string ShortName => Name + (Source?.Suffix ?? "");

    /// <summary>The file name was shown or hidden (a file added or removed): legends and cursor panel follow.</summary>
    public void RefreshSourceLabel()
    {
        OnPropertyChanged(nameof(LegendText));
        OnPropertyChanged(nameof(ShortName));
    }

    /// <summary>How the curve is computed; null for a column of the file.</summary>
    public CsvAnalyser.Core.Configuration.ComputedCurveConfig? Definition { get; init; }

    public bool IsComputed => Definition is not null;

    public string Name
    {
        get => Channel.DisplayName;
        set
        {
            if (Channel.DisplayName == value)
                return;
            Channel.DisplayName = value;
            OnLabelChanged();
        }
    }

    public string Unit
    {
        get => Channel.Unit;
        set
        {
            value = value.Trim();
            if (Channel.Unit == value)
                return;
            Channel.Unit = value;
            OnLabelChanged();
        }
    }

    public string UnitLabel => string.IsNullOrEmpty(Unit) ? "" : $"[{Unit}]";

    public string LegendText => (string.IsNullOrEmpty(Unit) ? Name : $"{Name} [{Unit}]") + (Source?.Suffix ?? "");

    public bool IsRenamed => Name != Channel.SourceName;

    public string OriginalName => _originalName;

    public string OriginalUnit => _originalUnit;

    /// <summary>Nothing changed by the user: name, unit, color, line, markers and axis as when the file was opened.</summary>
    public bool IsDefaultAppearance =>
        Name == _originalName && Unit == _originalUnit && !IsColorCustom
        && Line == CurveLine.Solid && LineWidth == CurveStyleChoices.DefaultLineWidth
        && Marker == CurveMarker.None && MarkerSize == CurveStyleChoices.DefaultMarkerSize && MarkerSpacing == 0 && Opacity == 1
        && AxisChoice == AutoAxis;

    public bool IsLineVisible => Line != CurveLine.None;

    public bool HasMarkers => Marker != CurveMarker.None;

    public SolidColorBrush Brush
    {
        get
        {
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(Color.A, Color.R, Color.G, Color.B));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>"#RRGGBB"; setting a valid value picks that color.</summary>
    public string ColorHex
    {
        get => $"#{Color.R:X2}{Color.G:X2}{Color.B:X2}";
        set
        {
            string hex = value.Trim().TrimStart('#');
            if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                SetColor("#" + hex);
            else
                OnPropertyChanged(); // Puts the current value back in the text box.
        }
    }

    /// <summary>Called when the settings window closes: an emptied name falls back to the original one.</summary>
    public void CommitEdits()
    {
        if (string.IsNullOrWhiteSpace(Name))
            Name = _originalName;
        else if (Name != Name.Trim())
            Name = Name.Trim();
    }

    [RelayCommand]
    private void SetColor(string hex)
    {
        IsColorCustom = true;
        Color = ScottPlot.Color.FromHex(hex);
    }

    [RelayCommand]
    private void UseAutoColor()
    {
        IsColorCustom = false;
        Color = _host.PickAutoColor(this);
    }

    [RelayCommand]
    private void ResetAppearance()
    {
        Name = _originalName;
        Unit = _originalUnit;
        Line = CurveLine.Solid;
        LineWidth = CurveStyleChoices.DefaultLineWidth;
        Marker = CurveMarker.None;
        MarkerSize = CurveStyleChoices.DefaultMarkerSize;
        MarkerSpacing = 0;
        Opacity = 1;
        AxisChoice = AutoAxis;
        UseAutoColor();
    }

    private void OnLabelChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Unit));
        OnPropertyChanged(nameof(UnitLabel));
        OnPropertyChanged(nameof(LegendText));
        OnPropertyChanged(nameof(ShortName));
        OnPropertyChanged(nameof(IsRenamed));
        _host.OnAppearanceChanged(this);
    }

    partial void OnIsPlottedChanged(bool value) => _host.OnPlottedChanged(this);

    /// <summary>Marks the curve as plotted without telling the host (it was put in the plotted list directly).</summary>
    public void SetPlottedSilently(bool value)
    {
#pragma warning disable MVVMTK0034
        _isPlotted = value;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(IsPlotted));
    }

    partial void OnColorChanged(ScottPlot.Color value) => _host.OnAppearanceChanged(this);

    partial void OnLineChanged(CurveLine value) => _host.OnAppearanceChanged(this);

    partial void OnLineWidthChanged(double value) => _host.OnAppearanceChanged(this);

    partial void OnMarkerChanged(CurveMarker value) => _host.OnAppearanceChanged(this);

    partial void OnMarkerSizeChanged(double value) => _host.OnAppearanceChanged(this);

    partial void OnMarkerSpacingChanged(int value) => _host.OnAppearanceChanged(this);

    partial void OnOpacityChanged(double value) => _host.OnAppearanceChanged(this);

    partial void OnAxisChoiceChanged(string value) => _host.OnAppearanceChanged(this);
}
