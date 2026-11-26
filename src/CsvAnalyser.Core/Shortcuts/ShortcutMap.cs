namespace CsvAnalyser.Core.Shortcuts;

/// <summary>
/// Keyboard shortcuts chosen by the user: each action has a default gesture ("Ctrl+Shift+M", "" = none) and the
/// settings keep only the actions whose gesture differs from it. Gestures are compared ignoring case.
/// </summary>
public static class ShortcutMap
{
    /// <summary>Gesture of every action (id → gesture, "" = none): the user's choice, else the default.</summary>
    public static Dictionary<string, string> Effective(
        IEnumerable<(string Id, string Default)> actions, IReadOnlyDictionary<string, string>? overrides)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, def) in actions)
            map[id] = overrides is not null && overrides.TryGetValue(id, out var chosen) ? chosen ?? "" : def;

        // A gesture given to two actions (an old setting against a newer default): the user's choice wins.
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in map.Keys.OrderBy(id => overrides?.ContainsKey(id) == true ? 0 : 1).ToList())
        {
            string g = map[id];
            if (g.Length == 0)
                continue;
            if (seen.ContainsKey(g))
                map[id] = "";
            else
                seen[g] = id;
        }
        return map;
    }

    /// <summary>The action that already uses <paramref name="gesture"/>, other than <paramref name="exceptId"/>.</summary>
    public static string? Owner(IReadOnlyDictionary<string, string> effective, string gesture, string? exceptId = null)
    {
        if (string.IsNullOrEmpty(gesture))
            return null;
        foreach (var (id, g) in effective)
            if (id != exceptId && string.Equals(g, gesture, StringComparison.OrdinalIgnoreCase))
                return id;
        return null;
    }

    /// <summary>What the settings keep: the actions whose gesture is not their default ("" = removed).</summary>
    public static Dictionary<string, string> Overrides(
        IReadOnlyDictionary<string, string> effective, IEnumerable<(string Id, string Default)> actions)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, def) in actions)
            if (effective.TryGetValue(id, out var g) && !string.Equals(g, def, StringComparison.OrdinalIgnoreCase))
                result[id] = g;
        return result;
    }
}
