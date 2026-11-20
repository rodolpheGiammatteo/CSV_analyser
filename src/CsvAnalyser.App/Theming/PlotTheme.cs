namespace CsvAnalyser.App.Theming;

public sealed record PlotTheme(
    string Name,
    bool IsDarkApp,
    string FigureBackground,
    string DataBackground,
    string AxisColor,
    string MajorGrid,
    string MinorGrid,
    string CursorColor,
    string[] Palette)
{
    /// <summary>The name shown (the French <see cref="Name"/> is the key saved in the settings).</summary>
    public override string ToString() => L.T(Name);
}

public static class PlotThemes
{
    public static readonly PlotTheme Light = new(
        "Clair", false, "#FFFFFF", "#FFFFFF", "#333333", "#E3E3E3", "#F3F3F3", "#555555",
        ["#1F77B4", "#FF7F0E", "#2CA02C", "#D62728", "#9467BD", "#8C564B", "#E377C2", "#7F7F7F", "#BCBD22", "#17BECF"]);

    public static readonly PlotTheme Dark = new(
        "Sombre", true, "#1E1E1E", "#252526", "#D4D4D4", "#3A3A3A", "#2E2E2E", "#E0E0E0",
        ["#4FC3F7", "#FFB74D", "#81C784", "#E57373", "#BA68C8", "#FFF176", "#4DD0E1", "#F06292", "#AED581", "#90A4AE"]);

    public static readonly PlotTheme Oscilloscope = new(
        "Oscilloscope", true, "#0A0F0A", "#000000", "#9BE29B", "#1C3A1C", "#0F220F", "#FFFFFF",
        ["#FFEB3B", "#00E5FF", "#FF4081", "#76FF03", "#FF9100", "#E040FB", "#18FFFF", "#FFFFFF"]);

    public static readonly PlotTheme Nord = new(
        "Bleu nuit", true, "#2E3440", "#3B4252", "#ECEFF4", "#4C566A", "#434C5E", "#ECEFF4",
        ["#88C0D0", "#EBCB8B", "#A3BE8C", "#BF616A", "#B48EAD", "#D08770", "#8FBCBB", "#5E81AC"]);

    public static readonly PlotTheme Paper = new(
        "Impression", false, "#FFFFFF", "#FFFFFF", "#000000", "#CCCCCC", "#EEEEEE", "#000000",
        ["#000000", "#C00000", "#0050B4", "#008000", "#7030A0", "#C65911", "#404040", "#00A0A0"]);

    public static IReadOnlyList<PlotTheme> All { get; } = [Light, Dark, Oscilloscope, Nord, Paper];

    public static PlotTheme ByName(string? name) => All.FirstOrDefault(t => t.Name == name) ?? Dark;
}
