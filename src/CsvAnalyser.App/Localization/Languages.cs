using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace CsvAnalyser.App.Localization;

/// <summary>A language the interface can use: French (built in) or a file of the Languages folder.</summary>
public sealed record LanguageInfo(string Code, string Name, string? FilePath)
{
    public override string ToString() => Name;
}

/// <summary>
/// The interface is written in French; another language is a JSON file in <c>Languages\</c> next to the program:
/// <code>{ "code": "en", "name": "English", "strings": { "Ouvrir…": "Open…", … } }</code>
/// To add a language, copy <c>en.json</c>, change code and name, and translate the values (a missing or empty
/// value shows the French text). The language is chosen in Paramètres and applies at the next start.
/// </summary>
public static class Languages
{
    public const string French = "fr";

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Languages");

    /// <summary>The language loaded at startup.</summary>
    public static string Current { get; private set; } = French;

    public static IReadOnlyList<LanguageInfo> Available()
    {
        var list = new List<LanguageInfo> { new(French, "Français", null) };
        if (!Directory.Exists(Folder))
            return list;
        foreach (var file in Directory.GetFiles(Folder, "*.json").Order())
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                string code = doc.RootElement.GetProperty("code").GetString() ?? Path.GetFileNameWithoutExtension(file);
                string name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? code : code;
                if (code != French)
                    list.Add(new LanguageInfo(code, name, file));
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // A broken language file is simply not offered.
            }
        }
        return list;
    }

    /// <summary>Loads a language (unknown or unreadable = French) and translates every window as it opens.</summary>
    public static void Apply(string? code)
    {
        Current = French;
        if (string.IsNullOrEmpty(code) || code == French)
            return;
        var info = Available().FirstOrDefault(l => l.Code == code);
        if (info?.FilePath is not { } path)
            return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in doc.RootElement.GetProperty("strings").EnumerateObject())
            {
                if (entry.Value.GetString() is { Length: > 0 } text)
                    table[entry.Name] = text;
            }
            L.Use(table);
            Current = code;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return;
        }
        ScreenTranslator.Enable();
    }
}

/// <summary>
/// Translates the texts written in the XAML (French) the first time each element gets a size, i.e. when it is
/// first shown (windows, templates, menus and popups included): text blocks, button and check box contents,
/// menu headers, tooltips, placeholders, window titles. Only values typed in the XAML are translated, never bound
/// ones (those are translated in the code with <see cref="L"/>).
/// </summary>
/// <remarks>
/// Not on <c>Loaded</c>: WPF raises it only on elements that have their own Loaded handler, so a class handler
/// misses most of them. <c>SizeChanged</c> is raised on every element when it is first laid out.
/// </remarks>
public static class ScreenTranslator
{
    private static bool _enabled;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject, object> Done = [];

    public static void Enable()
    {
        if (_enabled)
            return;
        _enabled = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnSizeChanged), true);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is DependencyObject d && !Done.TryGetValue(d, out _))
        {
            Done.Add(d, d);
            Translate(d);
        }
    }

    private static void Translate(DependencyObject d)
    {
        switch (d)
        {
            case TextBlock block:
                var local = block.ReadLocalValue(TextBlock.TextProperty);
                if (local is string)
                    TranslateLocal(block, TextBlock.TextProperty);
                else if (local == DependencyProperty.UnsetValue)
                {
                    // <Run Text="…"/> pieces written in the XAML (bound text is left alone).
                    foreach (var run in block.Inlines.OfType<Run>().ToList()) // copy: a new text may rebuild the inlines
                        TranslateLocal(run, Run.TextProperty);
                }
                break;
            case HeaderedItemsControl headered:
                TranslateLocal(headered, HeaderedItemsControl.HeaderProperty);
                break;
            case HeaderedContentControl headeredContent:
                TranslateLocal(headeredContent, HeaderedContentControl.HeaderProperty);
                TranslateLocal(headeredContent, ContentControl.ContentProperty);
                break;
            case Window window:
                TranslateLocal(window, Window.TitleProperty);
                break;
            case Wpf.Ui.Controls.TitleBar titleBar:
                TranslateLocal(titleBar, Wpf.Ui.Controls.TitleBar.TitleProperty);
                break;
            case Wpf.Ui.Controls.TextBox textBox:
                TranslateLocal(textBox, Wpf.Ui.Controls.TextBox.PlaceholderTextProperty);
                break;
            case Controls.SearchPicker picker:
                TranslateLocal(picker, Controls.SearchPicker.PlaceholderProperty);
                break;
            case ContentControl content:
                TranslateLocal(content, ContentControl.ContentProperty);
                break;
        }
        TranslateLocal(d, FrameworkElement.ToolTipProperty);
    }

    /// <summary>Replaces a text typed in the XAML (a local string value, not a binding) by its translation.</summary>
    private static void TranslateLocal(DependencyObject d, DependencyProperty property)
    {
        if (d.ReadLocalValue(property) is string text && L.TryTranslate(text, out var translated))
            d.SetValue(property, translated);
    }
}
