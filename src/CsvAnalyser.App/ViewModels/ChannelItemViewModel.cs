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

    public string ColorModeLabel => IsColorCustom ? "choisie" : "automatique (suit le thème)";

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

    public string LegendText => string.IsNullOrEmpty(Unit) ? Name : $"{Name} [{Unit}]";

    public bool IsRenamed => Name != Channel.SourceName;

    public string OriginalName => _originalName;

    public string OriginalUnit => _originalUnit;

    /// <summary>Nothing changed by the user: name, unit, color, line, markers and axis as when the file was opened.</summary>
    public bool IsDefaultAppearance =>
        Name == _originalName && Unit == _originalUnit && !IsColorCustom
        && Line == CurveLine.Solid && LineWidth == CurveStyleChoices.DefaultLineWidth
        && Marker == CurveMarker.None && MarkerSize == CurveStyleChoices.DefaultMarkerSize
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
        AxisChoice = AutoAxis;
        UseAutoColor();
    }

    private void OnLabelChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Unit));
        OnPropertyChanged(nameof(UnitLabel));
        OnPropertyChanged(nameof(LegendText));
        OnPropertyChanged(nameof(IsRenamed));
        _host.OnAppearanceChanged(this);
    }

    partial void OnIsPlottedChanged(bool value) => _host.OnPlottedChanged(this);

    partial void OnColorChanged(ScottPlot.Color value) => _host.OnAppearanceChanged(this);

    partial void OnLineChanged(CurveLine value) => _host.OnAppearanceChanged(this);

    partial void OnLineWidthChanged(double value) => _host.OnAppearanceChanged(this);

    partial void OnMarkerChanged(CurveMarker value) => _host.OnAppearanceChanged(this);

    partial void OnMarkerSizeChanged(double value) => _host.OnAppearanceChanged(this);

    partial void OnAxisChoiceChanged(string value) => _host.OnAppearanceChanged(this);
}
