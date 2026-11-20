using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace CsvAnalyser.App.Localization;

/// <summary>A language the interface can use: French (no table), or a JSON table read by <see cref="ReadJson"/>.</summary>
public sealed record LanguageInfo(string Code, string Name, Func<string>? ReadJson)
{
    public override string ToString() => Name;
}

/// <summary>
/// The interface is written in French; another language is a JSON table:
/// <code>{ "code": "en", "name": "English", "strings": { "Ouvrir…": "Open…", … } }</code>
/// The files of <c>Languages\</c> in the source are built into the program (nothing to copy beside it). A file put in
/// <c>%APPDATA%\CsvAnalyser\Languages</c> adds a language, or replaces the built-in one with the same code, without
/// rebuilding. To add a language, copy <c>en.json</c>, change code and name, and translate the values (a missing or
/// empty value shows the French text). The language is chosen in Paramètres and applies at the next start.
/// </summary>
public static class Languages
{
    public const string French = "fr";

    /// <summary>Extra or replacement languages of the user (beside the settings).</summary>
    public static string UserFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CsvAnalyser", "Languages");

    private const string ResourcePrefix = "Languages.";

    /// <summary>The language loaded at startup.</summary>
    public static string Current { get; private set; } = French;

    public static IReadOnlyList<LanguageInfo> Available()
    {
        var byCode = new Dictionary<string, LanguageInfo>(StringComparer.OrdinalIgnoreCase);

        // Built in (embedded resources "Languages.xx.json"), then the user's files, which win.
        var assembly = typeof(Languages).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(r => r.StartsWith(ResourcePrefix, StringComparison.Ordinal) && r.EndsWith(".json", StringComparison.Ordinal)).Order())
        {
            Add(byCode, () =>
            {
                using var stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            });
        }
        if (Directory.Exists(UserFolder))
        {
            foreach (var file in Directory.GetFiles(UserFolder, "*.json").Order())
                Add(byCode, () => File.ReadAllText(file));
        }

        var list = new List<LanguageInfo> { new(French, "Français", null) };
        list.AddRange(byCode.Values.OrderBy(l => l.Name, StringComparer.CurrentCulture));
        return list;
    }

    private static void Add(Dictionary<string, LanguageInfo> byCode, Func<string> read)
    {
        try
        {
            using var doc = JsonDocument.Parse(read());
            string code = doc.RootElement.GetProperty("code").GetString() ?? "";
            string name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? code : code;
            if (code.Length > 0 && code != French)
                byCode[code] = new LanguageInfo(code, name, read);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            // A broken language file is simply not offered.
        }
    }

    /// <summary>Loads a language (unknown or unreadable = French) and translates every window as it opens.</summary>
    public static void Apply(string? code)
    {
        Current = French;
        if (string.IsNullOrEmpty(code) || code == French)
            return;
        var info = Available().FirstOrDefault(l => l.Code == code);
        if (info?.ReadJson is not { } read)
            return;
        try
        {
            using var doc = JsonDocument.Parse(read());
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in doc.RootElement.GetProperty("strings").EnumerateObject())
            {
                if (entry.Value.GetString() is { Length: > 0 } text)
                    table[entry.Name] = text;
            }
            L.Use(table);
            Current = code;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
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
