using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CsvAnalyser.Core.Configuration;

/// <summary>
/// A saved layout ("recipe"): graphs, curve settings and computed curves. Curves are identified by the column
/// header found in the file (<see cref="Data.Channel.SourceName"/>), so a configuration can be applied to any
/// file with the same columns. Enum values are stored as their names, so the file stays readable.
/// </summary>
public sealed class ViewConfiguration
{
    public const string FormatName = "CsvAnalyser.Configuration";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FormatName;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Name of the CSV file the configuration was made from (information only).</summary>
    public string? SourceFile { get; set; }

    /// <summary>Full path of that CSV file: opening the project opens it.</summary>
    public string? SourcePath { get; set; }

    /// <summary>Column used as X axis; "" = row number; null = the default one.</summary>
    public string? XAxis { get; set; }

    /// <summary>Visible X range when saved (restored only on the same file).</summary>
    public RangeConfig? XRange { get; set; }

    public List<GraphConfig> Graphs { get; set; } = [];

    /// <summary>Number of columns of graphs (null = 1).</summary>
    public int? GraphColumns { get; set; }

    /// <summary>Plotted curves (in legend order) and other curves whose settings were changed.</summary>
    public List<CurveConfig> Curves { get; set; } = [];

    /// <summary>Measurement cursors A and B (positions restored only on the same file).</summary>
    public MeasureConfig? Measure { get; set; }

    /// <summary>Computed curves, in creation order (one may use the previous ones).</summary>
    public List<ComputedCurveConfig> Computed { get; set; } = [];

    /// <summary>Markers, highlighted zones and notes on a point (restored only on the same file).</summary>
    public List<AnnotationConfig>? Annotations { get; set; }

    /// <summary>X offset of the opened file (null = 0).</summary>
    public double? XOffset { get; set; }

    /// <summary>Files added to compare with the opened one, in order (curve <see cref="CurveConfig.File"/> 1, 2…).</summary>
    public List<ComparedFileConfig>? Files { get; set; }

    /// <summary>Columns used by the configuration's curves that are neither in <paramref name="available"/> nor computed.</summary>
    public IReadOnlyList<string> MissingColumns(IEnumerable<string> available)
    {
        var known = new HashSet<string>(available, StringComparer.Ordinal);
        foreach (var c in Computed)
            known.Add(c.Name);
        var missing = new List<string>();
        foreach (var name in Curves.Where(c => c.File is null or 0).Select(c => c.Column))
        {
            if (!known.Contains(name) && !missing.Contains(name))
                missing.Add(name);
        }
        return missing;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // accents readable in the file
        // A NaN or infinite value (an empty range, a bound typed as text) must not make the save fail.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Reads a configuration; throws <see cref="InvalidDataException"/> when the text is not one.</summary>
    public static ViewConfiguration FromJson(string json)
    {
        ViewConfiguration? config;
        try
        {
            config = JsonSerializer.Deserialize<ViewConfiguration>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(L.T("Ce fichier n'est pas une configuration valide : ") + ex.Message, ex);
        }
        if (config is null || config.Format != FormatName)
            throw new InvalidDataException(L.T("Ce fichier n'est pas une configuration de CSV Analyser."));
        if (config.Version > CurrentVersion)
            throw new InvalidDataException(L.T("Cette configuration vient d'une version plus récente de CSV Analyser."));
        return config;
    }

    /// <summary>Written to a temporary file first, then swapped in: a crash during the save never leaves a truncated file.</summary>
    public void Save(string path)
    {
        WriteAtomically(path, ToJson());
    }

    /// <summary>Writes <paramref name="text"/> beside <paramref name="path"/> (unique name: two windows may save at once), then replaces it.</summary>
    public static void WriteAtomically(string path, string text)
    {
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public static ViewConfiguration Load(string path) => FromJson(File.ReadAllText(path));
}

public sealed class RangeConfig
{
    public double Min { get; set; }

    public double Max { get; set; }

    public RangeConfig()
    {
    }

    public RangeConfig(double min, double max)
    {
        Min = min;
        Max = max;
    }

    [JsonIgnore]
    public bool IsValid => double.IsFinite(Min) && double.IsFinite(Max) && Min < Max;
}

/// <summary>Definition of a computed curve: a formula, or a filter applied to one curve.</summary>
public sealed class ComputedCurveConfig
{
    public const string Formula = "Formula", MovingAverage = "MovingAverage", LowPass = "LowPass",
        HighPass = "HighPass", Derivative = "Derivative", Integral = "Integral";

    /// <summary>Name of the new curve (also its key in <see cref="ViewConfiguration.Curves"/>).</summary>
    public string Name { get; set; } = "";

    public string Unit { get; set; } = "";

    /// <summary>Formula, MovingAverage, LowPass, HighPass, Derivative or Integral.</summary>
    public string Kind { get; set; } = Formula;

    /// <summary>The formula text (kind Formula).</summary>
    public string? Expression { get; set; }

    /// <summary>The filtered curve (other kinds).</summary>
    public string? Source { get; set; }

    /// <summary>Window of a moving average (unit of the time base), or cut-off frequency of a filter (Hz).</summary>
    public double? Parameter { get; set; }

    /// <summary>Filters: run forward then backward (no delay).</summary>
    public bool ZeroPhase { get; set; } = true;

    public ComputedCurveConfig Clone() => (ComputedCurveConfig)MemberwiseClone();
}

public sealed class MeasureConfig
{
    public bool Enabled { get; set; }

    public double? A { get; set; }

    public double? B { get; set; }

    /// <summary>Horizontal cursors Y1 and Y2 shown.</summary>
    public bool YEnabled { get; set; }

    /// <summary>Index of the graph measured by Y1/Y2, and the key of its axis.</summary>
    public int? YGraph { get; set; }

    public string? YAxis { get; set; }

    public double? Y1 { get; set; }

    public double? Y2 { get; set; }
}

public sealed class GraphConfig
{
    public string? Title { get; set; }

    public bool AlignZeros { get; set; }

    /// <summary>UpperRight, UpperLeft, LowerRight, LowerLeft or Hidden.</summary>
    public string? Legend { get; set; }

    /// <summary>Distance of a dragged legend from its corner (device-independent units).</summary>
    public double? LegendOffsetX { get; set; }

    public double? LegendOffsetY { get; set; }

    /// <summary>Relative height in its column (null = 1).</summary>
    public double? HeightFactor { get; set; }

    public List<AxisConfig> Axes { get; set; } = [];
}

public sealed class AxisConfig
{
    /// <summary>"unit:&lt;unit&gt;" or "curve:&lt;column&gt;".</summary>
    public string Key { get; set; } = "";

    public bool Manual { get; set; }

    public double? Min { get; set; }

    public double? Max { get; set; }

    /// <summary>Visible range when saved (restored only on the same file).</summary>
    public RangeConfig? View { get; set; }

    /// <summary>Value drawn at the same height as the other axes when the graph aligns them (null = 0).</summary>
    public double? AlignValue { get; set; }

    /// <summary>Auto, Left or Right (null = Auto).</summary>
    public string? Side { get; set; }

    /// <summary>Axis title typed by the user (null = automatic).</summary>
    public string? Title { get; set; }
}

/// <summary>A file added to compare: its path and its X offset.</summary>
public sealed class ComparedFileConfig
{
    public string Path { get; set; } = "";

    /// <summary>Added to its X values (null = 0).</summary>
    public double? Offset { get; set; }
}

public sealed class CurveConfig
{
    /// <summary>0 or null = the opened file; 1, 2… = <see cref="ViewConfiguration.Files"/>[n - 1].</summary>
    public int? File { get; set; }

    /// <summary>Column header in the file (or name of a computed curve).</summary>
    public string Column { get; set; } = "";

    public string? Name { get; set; }

    public string? Unit { get; set; }

    public bool Plotted { get; set; }

    /// <summary>Index of the graph in <see cref="ViewConfiguration.Graphs"/>.</summary>
    public int? Graph { get; set; }

    /// <summary>"" = automatic (by unit), "own" = its own axis, else the key of the axis it shares.</summary>
    public string? Axis { get; set; }

    /// <summary>"#RRGGBB" when chosen by hand; null = follows the theme.</summary>
    public string? Color { get; set; }

    public string? Line { get; set; }

    public double? LineWidth { get; set; }

    public string? Marker { get; set; }

    public double? MarkerSize { get; set; }

    /// <summary>Markers about every this many pixels on screen; null or 0 = one per sample.</summary>
    public int? MarkerSpacing { get; set; }

    /// <summary>0.1 to 1; null = opaque.</summary>
    public double? Opacity { get; set; }
}

/// <summary>
/// An annotation, anchored to rows of the file (so it stays at the same samples whatever the X axis):
/// a marker at one row, a zone between two rows, or a note on a curve at one row.
/// </summary>
public sealed class AnnotationConfig
{
    /// <summary>A note on a curve of a compared file: that file's number (1, 2…); its row is a row of that file.</summary>
    public int? CurveFile { get; set; }

    public const string Marker = "Marker", Zone = "Zone", Note = "Note";

    /// <summary>Marker, Zone or Note.</summary>
    public string Kind { get; set; } = Marker;

    public string Text { get; set; } = "";

    /// <summary>Row of a marker or a note; first row of a zone.</summary>
    public int Row { get; set; }

    /// <summary>Last row of a zone.</summary>
    public int? EndRow { get; set; }

    /// <summary>Column of the curve a note points at.</summary>
    public string? Curve { get; set; }

    /// <summary>Index of the only graph showing a marker or zone (null = every graph).</summary>
    public int? Graph { get; set; }

    /// <summary>"#RRGGBB".</summary>
    public string? Color { get; set; }

    public double? FontSize { get; set; }

    public double? LineWidth { get; set; }

    /// <summary>Solid, Dashed, DenselyDashed or Dotted.</summary>
    public string? Line { get; set; }

    /// <summary>Fill opacity of a zone.</summary>
    public double? Opacity { get; set; }

    /// <summary>Position of a note's bubble from its point (device-independent units, Y down).</summary>
    public double? LabelOffsetX { get; set; }

    public double? LabelOffsetY { get; set; }

    public bool Hidden { get; set; }

    /// <summary>
    /// For each row, the text of the zones that contain it (" / " between overlapping zones; "" outside):
    /// the "zone" column of the CSV export.
    /// </summary>
    public static Func<int, string?> ZoneNames(IEnumerable<AnnotationConfig> annotations)
    {
        var zones = annotations
            .Where(a => a.Kind == Zone && a.EndRow is not null)
            .Select(a => (Start: Math.Min(a.Row, a.EndRow!.Value), End: Math.Max(a.Row, a.EndRow!.Value), a.Text))
            .OrderBy(z => z.Start)
            .ToList();
        return row =>
        {
            string? result = null;
            foreach (var (start, end, text) in zones)
            {
                if (start > row)
                    break;
                if (row <= end)
                    result = result is null ? text : result + " / " + text;
            }
            return result;
        };
    }
}
