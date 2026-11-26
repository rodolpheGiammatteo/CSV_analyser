using System.Windows.Input;

namespace CsvAnalyser.App.Services;

/// <summary>
/// A keyboard shortcut as text: stored as "Ctrl+Shift+Alt+M" (English, invariant), shown with the localized
/// modifier names ("Ctrl+Maj+M" in French).
/// </summary>
public static class ShortcutGesture
{
    /// <summary>The gesture of a key press, or null for a lone modifier or a key that cannot be a shortcut.</summary>
    public static string? FromKeyEvent(KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };
        var modifiers = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt);
        return IsAllowed(modifiers, key) ? Format(modifiers, key) : null;
    }

    /// <summary>
    /// F1–F12 may be used alone; any other key needs Ctrl or Alt (a letter alone would fire while typing).
    /// Modifiers, Échap, Entrée, Tab and the Windows keys are never shortcuts.
    /// </summary>
    public static bool IsAllowed(ModifierKeys modifiers, Key key)
    {
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.Apps or Key.Escape or Key.Enter or Key.Tab or Key.CapsLock or Key.NumLock
            or Key.Scroll or Key.DeadCharProcessed)
            return false;
        if (key is >= Key.F1 and <= Key.F12)
            return true;
        return (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0;
    }

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift))
            parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt))
            parts.Add("Alt");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>For the screen: "Ctrl+Shift+D1" → "Ctrl+Maj+1" (French), "Ctrl+OemPlus" → "Ctrl++".</summary>
    public static string Display(string gesture)
    {
        if (string.IsNullOrEmpty(gesture))
            return "";
        var parts = gesture.Split('+');
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = parts[i] switch
            {
                "Shift" when i < parts.Length - 1 => L.T("Maj"),
                "Ctrl" or "Alt" when i < parts.Length - 1 => parts[i],
                var k => KeyName(k),
            };
        }
        return string.Join("+", parts);
    }

    private static string KeyName(string key) => key switch
    {
        _ when key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) => key[1..],
        _ when key.StartsWith("NumPad", StringComparison.Ordinal) && key.Length == 7 => L.F("Pavé {0}", key[6..]),
        "OemPlus" or "Add" => "+",
        "OemMinus" or "Subtract" => "-",
        "Multiply" => "*",
        "Divide" => "/",
        "OemComma" => ",",
        "OemPeriod" or "Decimal" => ".",
        "Space" => L.T("Espace"),
        "Delete" => L.T("Suppr"),
        "Back" => L.T("Retour arrière"),
        "Insert" => L.T("Inser"),
        "Home" => L.T("Début"),
        "End" => L.T("Fin"),
        "PageUp" or "Prior" => L.T("Page préc."),
        "PageDown" or "Next" => L.T("Page suiv."),
        "Left" => "←",
        "Right" => "→",
        "Up" => "↑",
        "Down" => "↓",
        _ => key,
    };
}
