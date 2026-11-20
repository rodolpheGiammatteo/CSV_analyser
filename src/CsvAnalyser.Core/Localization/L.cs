using System.Globalization;

namespace CsvAnalyser.Core.Localization;

/// <summary>
/// Translation of the texts shown to the user. The French text is the key: <c>L.T("Ouvrir…")</c> gives the
/// translation of the chosen language, or the French text itself when there is none. A language is a JSON file
/// mapping French texts to translated ones (see <c>Languages/en.json</c> in the app), so adding a language is
/// adding a file.
/// </summary>
public static class L
{
    private static IReadOnlyDictionary<string, string> _table = new Dictionary<string, string>();

    /// <summary>The translations in use (empty = French).</summary>
    public static void Use(IReadOnlyDictionary<string, string> table) => _table = table;

    /// <summary>The translation of a French text.</summary>
    public static string T(string french) =>
        _table.TryGetValue(french, out var text) && !string.IsNullOrEmpty(text) ? text : french;

    /// <summary>The translation of a French format string ("{0} lignes"), then formatted.</summary>
    public static string F(string frenchFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(frenchFormat), args);

    /// <summary>True when the text has a translation (the screen translator leaves other texts alone).</summary>
    public static bool TryTranslate(string french, out string text)
    {
        if (_table.TryGetValue(french, out var t) && !string.IsNullOrEmpty(t))
        {
            text = t;
            return true;
        }
        text = french;
        return false;
    }
}
