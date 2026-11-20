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

    /// <summary>Column used as X axis; "" = row number; null = the default one.</summary>
    public string? XAxis { get; set; }

    /// <summary>Visible X range when saved (restored only on the same file).</summary>
    public RangeConfig? XRange { get; set; }

    public List<GraphConfig> Graphs { get; set; } = [];

    /// <summary>Plotted curves (in legend order) and other curves whose settings were changed.</summary>
    public List<CurveConfig> Curves { get; set; } = [];

    /// <summary>Measurement cursors A and B (positions restored only on the same file).</summary>
    public MeasureConfig? Measure { get; set; }

    /// <summary>Columns used by the configuration that are not in <paramref name="available"/>.</summary>
    public IReadOnlyList<string> MissingColumns(IEnumerable<string> available)
    {
        var known = new HashSet<string>(available, StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var name in Curves.Select(c => c.Column))
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
            throw new InvalidDataException("Ce fichier n'est pas une configuration valide : " + ex.Message, ex);
        }
        if (config is null || config.Format != FormatName)
            throw new InvalidDataException("Ce fichier n'est pas une configuration de CSV Analyser.");
        if (config.Version > CurrentVersion)
            throw new InvalidDataException("Cette configuration vient d'une version plus récente de CSV Analyser.");
        return config;
    }

    public void Save(string path) => File.WriteAllText(path, ToJson());

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

public sealed class MeasureConfig
{
    public bool Enabled { get; set; }

    public double? A { get; set; }

    public double? B { get; set; }
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
}

public sealed class CurveConfig
{
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
}
