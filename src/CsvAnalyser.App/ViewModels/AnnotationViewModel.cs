using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsvAnalyser.Core.Configuration;

namespace CsvAnalyser.App.ViewModels;

public enum AnnotationKind
{
    /// <summary>A vertical line at one row, across the graphs.</summary>
    Marker,

    /// <summary>A highlighted band between two rows.</summary>
    Zone,

    /// <summary>A bubble pointing at a curve at one row.</summary>
    Note,
}

/// <summary>What an annotation needs from the main view model.</summary>
public interface IAnnotationHost
{
    void OnAnnotationChanged(AnnotationViewModel annotation);

    /// <summary>"t = 12,5 s" for a row (X axis value of that row).</summary>
    string DescribeRow(int row);
}

/// <summary>
/// A marker, zone or note, anchored to rows of the file (the same samples whatever the X axis). Every setting is
/// live: the settings window binds to it, and each change redraws the plot and becomes an undo step.
/// </summary>
public partial class AnnotationViewModel : ObservableObject
{
    public const double DefaultFontSize = 12;
    public const double DefaultLineWidth = 1.5;
    public const double DefaultLabelOffsetX = 9, DefaultLabelOffsetY = -9;
    public const double DefaultOpacity = 0.25; // lighter fills are barely visible on some screens

    /// <summary>Colors offered for annotations (the hex box takes any other).</summary>
    public static IReadOnlyList<string> Swatches { get; } =
    [
        "#D85A30", "#E8590C", "#BA7517", "#1D9E75", "#2CA02C", "#0C8599", "#378ADD", "#534AB7", "#9467BD", "#D4537E",
        "#D62728", "#7F7F7F", "#444441", "#000000",
    ];

    public static IReadOnlyList<StyleChoice<CurveLine>> Lines { get; } = CurveStyleChoices.Lines.Where(l => l.Value != CurveLine.None).ToList();

    private readonly IAnnotationHost _host;
    private bool _quiet; // while loading from a configuration: no change reported

    public AnnotationViewModel(IAnnotationHost host, AnnotationKind kind, int row)
    {
        _host = host;
        _kind = kind;
        _row = row;
        _line = kind == AnnotationKind.Marker ? CurveLine.Dashed : CurveLine.Solid;
        _color = kind switch
        {
            AnnotationKind.Zone => "#1D9E75",
            AnnotationKind.Note => "#BA7517",
            _ => "#D85A30",
        };
        _text = kind switch
        {
            AnnotationKind.Zone => L.T("Zone"),
            AnnotationKind.Note => L.T("Note"),
            _ => L.T("Repère"),
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindLabel), nameof(IsZone), nameof(IsNote), nameof(HasLine), nameof(CanChooseGraph))]
    private AnnotationKind _kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListText))]
    private string _text;

    /// <summary>Row of a marker or note; first row of a zone.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    private int _row;

    /// <summary>Last row of a zone.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    private int _endRow;

    /// <summary>The curve a note points at (null when that column is gone).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    private ChannelItemViewModel? _curve;

    /// <summary>The only graph showing a marker or zone (null = every graph).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GraphChoice))]
    private GraphViewModel? _graph;

    /// <summary>"#RRGGBB".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Brush), nameof(ColorHex))]
    private string _color;

    [ObservableProperty]
    private double _fontSize = DefaultFontSize;

    [ObservableProperty]
    private double _lineWidth = DefaultLineWidth;

    [ObservableProperty]
    private CurveLine _line;

    /// <summary>Fill opacity of a zone.</summary>
    [ObservableProperty]
    private double _opacity = DefaultOpacity;

    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>Lower-left corner of a note's bubble from its point (device-independent units, Y down).</summary>
    [ObservableProperty]
    private double _labelOffsetX = DefaultLabelOffsetX;

    [ObservableProperty]
    private double _labelOffsetY = DefaultLabelOffsetY;

    /// <summary>The scope choice of the settings window: null = every graph.</summary>
    public IReadOnlyList<StyleChoice<GraphViewModel?>> GraphChoices { get; set; } = [];

    /// <summary>The selected entry of <see cref="GraphChoices"/> (a combo box cannot select a null value).</summary>
    public StyleChoice<GraphViewModel?>? GraphChoice
    {
        get => GraphChoices.FirstOrDefault(c => c.Value == Graph) ?? GraphChoices.FirstOrDefault();
        set
        {
            if (value is not null)
                Graph = value.Value;
        }
    }

    public bool IsZone => Kind == AnnotationKind.Zone;

    public bool IsNote => Kind == AnnotationKind.Note;

    public bool HasLine => Kind != AnnotationKind.Note;

    public bool CanChooseGraph => Kind != AnnotationKind.Note;

    public string KindLabel => Kind switch
    {
        AnnotationKind.Zone => L.T("Zone"),
        AnnotationKind.Note => L.T("Note"),
        _ => L.T("Repère"),
    };

    public string ListText => string.IsNullOrWhiteSpace(Text) ? KindLabel : Text;

    /// <summary>"repère · t = 12,5 s", "zone · 10 s → 25 s", "note · Pression · t = 4 s".</summary>
    public string PositionText => Kind switch
    {
        AnnotationKind.Zone => $"{KindLabel} · {_host.DescribeRow(Math.Min(Row, EndRow))} → {_host.DescribeRow(Math.Max(Row, EndRow))}",
        AnnotationKind.Note => $"{KindLabel} · {Curve?.Name ?? "?"} · {_host.DescribeRow(Row)}",
        _ => $"{KindLabel} · {_host.DescribeRow(Row)}",
    };

    public SolidColorBrush Brush
    {
        get
        {
            var c = ScottPlot.Color.FromHex(Color);
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>Hex box of the settings window: a valid "#RRGGBB" picks that color.</summary>
    public string ColorHex
    {
        get => Color;
        set
        {
            string hex = value.Trim().TrimStart('#');
            if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                Color = "#" + hex.ToUpperInvariant();
            else
                OnPropertyChanged(); // puts the current value back in the box
        }
    }

    [RelayCommand]
    private void SetColor(string hex) => Color = hex;

    /// <summary>The X positions changed (other X axis): the texts showing them are refreshed.</summary>
    public void RefreshPosition() => OnPropertyChanged(nameof(PositionText));

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Text) or nameof(Row) or nameof(EndRow) or nameof(Curve) or nameof(Graph) or nameof(Color)
            or nameof(FontSize) or nameof(LineWidth) or nameof(Line) or nameof(Opacity) or nameof(IsVisible) or nameof(Kind)
            or nameof(LabelOffsetX) or nameof(LabelOffsetY))
        {
            if (!_quiet)
                _host.OnAnnotationChanged(this);
        }
    }

    // ---------------------------------------------------------------- configuration

    public AnnotationConfig ToConfig(IList<GraphViewModel> graphs)
    {
        int graphIndex = Graph is { } g ? graphs.IndexOf(g) : -1;
        return new AnnotationConfig
        {
            Kind = Kind.ToString(),
            Text = Text,
            Row = Row,
            EndRow = Kind == AnnotationKind.Zone ? EndRow : null,
            Curve = Kind == AnnotationKind.Note ? Curve?.Channel.SourceName : null,
            Graph = graphIndex >= 0 && Kind != AnnotationKind.Note ? graphIndex : null,
            Color = Color,
            FontSize = FontSize != DefaultFontSize ? FontSize : null,
            LineWidth = LineWidth != DefaultLineWidth ? LineWidth : null,
            Line = Line != (Kind == AnnotationKind.Marker ? CurveLine.Dashed : CurveLine.Solid) ? Line.ToString() : null,
            Opacity = Kind == AnnotationKind.Zone && Opacity != DefaultOpacity ? Opacity : null,
            Hidden = !IsVisible,
            LabelOffsetX = Kind == AnnotationKind.Note && LabelOffsetX != DefaultLabelOffsetX ? LabelOffsetX : null,
            LabelOffsetY = Kind == AnnotationKind.Note && LabelOffsetY != DefaultLabelOffsetY ? LabelOffsetY : null,
        };
    }

    /// <summary>An annotation from a configuration; null when it does not fit this file (row out of range, unknown kind).</summary>
    public static AnnotationViewModel? FromConfig(IAnnotationHost host, AnnotationConfig c, int rowCount,
        IList<GraphViewModel> graphs, Func<string, ChannelItemViewModel?> findCurve)
    {
        if (!Enum.TryParse<AnnotationKind>(c.Kind, out var kind) || c.Row < 0 || c.Row >= rowCount)
            return null;
        var a = new AnnotationViewModel(host, kind, c.Row) { _quiet = true };
        a.Text = c.Text;
        if (kind == AnnotationKind.Zone)
            a.EndRow = Math.Clamp(c.EndRow ?? c.Row, 0, rowCount - 1);
        if (kind == AnnotationKind.Note)
            a.Curve = c.Curve is { } column ? findCurve(column) : null;
        if (c.Graph is { } gi && gi >= 0 && gi < graphs.Count && kind != AnnotationKind.Note)
            a.Graph = graphs[gi];
        if (c.Color is { } hex && hex.TrimStart('#').Length == 6)
            a.Color = "#" + hex.TrimStart('#').ToUpperInvariant();
        if (c.FontSize is { } fs && double.IsFinite(fs))
            a.FontSize = Math.Clamp(fs, 6, 40);
        if (c.LineWidth is { } lw && double.IsFinite(lw))
            a.LineWidth = Math.Clamp(lw, 0.5, 8);
        if (Enum.TryParse<CurveLine>(c.Line, out var line) && line != CurveLine.None)
            a.Line = line;
        if (c.Opacity is { } op && double.IsFinite(op))
            a.Opacity = Math.Clamp(op, 0.02, 0.9);
        a.IsVisible = !c.Hidden;
        if (c.LabelOffsetX is { } ox && double.IsFinite(ox) && c.LabelOffsetY is { } oy && double.IsFinite(oy))
            (a.LabelOffsetX, a.LabelOffsetY) = (Math.Clamp(ox, -5000, 5000), Math.Clamp(oy, -5000, 5000));
        a._quiet = false;
        return a;
    }
}
