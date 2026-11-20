using System.IO;
using System.Text.Json;

namespace CsvAnalyser.App.Services;

/// <summary>Per-user preferences, not tied to a CSV file.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CsvAnalyser", "settings.json");

    public string? ThemeName { get; set; }

    public string? LastDirectory { get; set; }

    public double UiScale { get; set; } = 1;

    public double PlotFontScale { get; set; } = 1;

    public CsvAnalyser.App.ViewModels.CursorPanelPlacement CursorPanel { get; set; }

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
