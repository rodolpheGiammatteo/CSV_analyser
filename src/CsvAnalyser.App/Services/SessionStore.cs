using System.IO;
using System.Security.Cryptography;
using System.Text;
using CsvAnalyser.Core.Configuration;

namespace CsvAnalyser.App.Services;

/// <summary>
/// The last layout of each CSV file, so reopening a file shows it as it was left
/// (<c>%APPDATA%\CsvAnalyser\sessions</c>, one file per CSV path, the oldest ones pruned).
/// </summary>
public static class SessionStore
{
    private const int MaxSessions = 300;

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CsvAnalyser", "sessions");

    private static string PathFor(string csvPath)
    {
        string key = Path.GetFullPath(csvPath).ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        return Path.Combine(Folder, hash + ".json");
    }

    public static void Save(string csvPath, ViewConfiguration config)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            config.Save(PathFor(csvPath));
            Prune();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static ViewConfiguration? TryLoad(string csvPath)
    {
        try
        {
            string path = PathFor(csvPath);
            return File.Exists(path) ? ViewConfiguration.Load(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    private static void Prune()
    {
        var files = new DirectoryInfo(Folder).GetFiles("*.json");
        if (files.Length <= MaxSessions)
            return;
        foreach (var old in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxSessions))
            old.Delete();
    }
}
