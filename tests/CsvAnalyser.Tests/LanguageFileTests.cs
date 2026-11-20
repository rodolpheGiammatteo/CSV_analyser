using System.Text.Json;
using System.Text.RegularExpressions;

namespace CsvAnalyser.Tests;

/// <summary>Checks every language file of the app: valid JSON, and the same {0} placeholders as the French text.</summary>
public partial class LanguageFileTests
{
    private static string LanguagesFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CsvAnalyser.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "CsvAnalyser.App", "Languages");
    }

    public static IEnumerable<object[]> Files() =>
        Directory.GetFiles(LanguagesFolder(), "*.json").Select(f => new object[] { Path.GetFileName(f) });

    [GeneratedRegex(@"\{(\d+)(:[^}]*)?\}")]
    private static partial Regex Placeholder();

    [Theory]
    [MemberData(nameof(Files))]
    public void LanguageFile_IsValid_AndKeepsPlaceholders(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(LanguagesFolder(), file)));
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("code").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("name").GetString()));
        foreach (var entry in doc.RootElement.GetProperty("strings").EnumerateObject())
        {
            string translated = entry.Value.GetString() ?? "";
            if (translated.Length == 0)
                continue;
            var expected = Placeholder().Matches(entry.Name).Select(m => m.Groups[1].Value).Distinct().Order();
            var actual = Placeholder().Matches(translated).Select(m => m.Groups[1].Value).Distinct().Order();
            Assert.True(expected.SequenceEqual(actual), $"{file}: « {entry.Name} » → « {translated} » placeholders differ");
        }
    }
}
