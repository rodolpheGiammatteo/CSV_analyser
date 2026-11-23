using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CsvAnalyser.App.Services;

/// <summary>Per-user preferences, not tied to a CSV file.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CsvAnalyser", "settings.json");

    public string? ThemeName { get; set; }

    /// <summary>Interface language code ("fr", "en"…), applied at startup.</summary>
    public string? Language { get; set; }

    public string? LastDirectory { get; set; }

    public double UiScale { get; set; } = 1;

    public double PlotFontScale { get; set; } = 1;

    public CsvAnalyser.App.ViewModels.CursorPanelPlacement CursorPanel { get; set; }

    /// <summary>X axis under the bottom graph only, or under every graph (with or without its title).</summary>
    public CsvAnalyser.App.ViewModels.XAxisPlacement XAxisPlacement { get; set; }

    public bool ShowCursorLabels { get; set; }

    /// <summary>Reopening a file restores the layout it had when it was left.</summary>
    public bool RestoreLayout { get; set; } = true;

    public string? LastConfigDirectory { get; set; }

    public string? ExportDelimiter { get; set; }

    public string? ExportDecimal { get; set; }

    /// <summary>Main window position and size (device-independent units) and state, restored at startup.</summary>
    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    public bool WindowMaximized { get; set; }

    /// <summary>Width of the curve list column.</summary>
    public double? CurveListWidth { get; set; }

    /// <summary>The curve list is hidden (more room for the graphs).</summary>
    public bool CurveListHidden { get; set; }

    /// <summary>Copied and saved graph images: size on screen × this factor.</summary>
    public double ImageScale { get; set; } = 1;

    /// <summary>Background grid of the graphs.</summary>
    public bool GridVertical { get; set; } = true;

    public bool GridHorizontal { get; set; } = true;

    public bool GridMinor { get; set; } = true;

    /// <summary>Solid, Dashed or Dotted.</summary>
    public string GridPattern { get; set; } = "Solid";

    /// <summary>Grid color between the plot background (0) and the axis color (1); 0.15 is about the themes' own grid.</summary>
    public double GridIntensity { get; set; } = 0.25;

    /// <summary>Line width multiplier (1 = thin).</summary>
    public double GridWidth { get; set; } = 1;

    /// <summary>Alternating horizontal bands between the major lines.</summary>
    public bool GridBands { get; set; }

    /// <summary>Last opened CSV files and projects, most recent first.</summary>
    public List<string> RecentFiles { get; set; } = [];

    /// <summary>Left-button drag zooms on a rectangle (and the wheel button pans); false = the reverse.</summary>
    public bool LeftDragZooms { get; set; } = true;

    /// <summary>Look for a newer version on GitHub at startup (off by default: no network access unless asked).</summary>
    public bool CheckUpdatesAtStartup { get; set; }

    private const int MaxRecentFiles = 12;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Each setting as last read from or written to the file: what this window changed since is what differs.</summary>
    private Dictionary<string, string> _baseline = [];

    public static AppSettings Load()
    {
        AppSettings settings = new();
        try
        {
            if (File.Exists(FilePath))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        settings._baseline = settings.Snapshot();
        return settings;
    }

    /// <summary>
    /// Writes only what this window changed into the file as it is now, so several windows of the program do not
    /// overwrite each other's settings; the recent files of both are kept (this window's first).
    /// </summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            JsonObject disk;
            try
            {
                disk = File.Exists(FilePath) ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? [] : [];
            }
            catch (JsonException)
            {
                disk = [];
            }
            var mine = JsonSerializer.SerializeToNode(this, Options)!.AsObject();
            foreach (var (key, value) in mine.ToList())
            {
                string text = value?.ToJsonString() ?? "null";
                bool changedHere = !_baseline.TryGetValue(key, out var before) || before != text;
                if (!changedHere && disk.ContainsKey(key))
                    continue; // another window may have changed it: keep the file's value
                if (key == nameof(RecentFiles) && disk[key] is JsonArray others)
                {
                    // The other window's files are kept, except those this window removed (cleared list, missing file).
                    var removedHere = (before is null ? [] : JsonSerializer.Deserialize<List<string>>(before) ?? [])
                        .Where(p => !RecentFiles.Contains(p, StringComparer.OrdinalIgnoreCase))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var merged = RecentFiles.ToList();
                    foreach (var path in others.Select(n => n?.GetValue<string>()).OfType<string>())
                    {
                        if (!merged.Contains(path, StringComparer.OrdinalIgnoreCase) && !removedHere.Contains(path))
                            merged.Add(path);
                    }
                    disk[key] = JsonSerializer.SerializeToNode(merged.Take(MaxRecentFiles).ToList());
                }
                else
                {
                    disk[key] = value?.DeepClone();
                }
            }
            CsvAnalyser.Core.Configuration.ViewConfiguration.WriteAtomically(FilePath, disk.ToJsonString(Options));
            _baseline = Snapshot();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
    }

    private Dictionary<string, string> Snapshot() =>
        JsonSerializer.SerializeToNode(this, Options)!.AsObject().ToDictionary(p => p.Key, p => p.Value?.ToJsonString() ?? "null");
}
