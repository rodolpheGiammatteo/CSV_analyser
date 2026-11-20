using System.IO;
using System.Text.Json;

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

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
