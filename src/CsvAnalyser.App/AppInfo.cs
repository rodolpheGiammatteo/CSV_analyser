namespace CsvAnalyser.App;

public static class AppInfo
{
    public const string Name = "CSV Analyser";

    /// <summary>"1.0.12" — computed at build time by Nerdbank.GitVersioning from version.json and the git history.</summary>
    public static string Version { get; } = ThisAssembly.AssemblyInformationalVersion.Split('+')[0];

    public static string FullTitle { get; } = $"{Name} {Version}";
}
