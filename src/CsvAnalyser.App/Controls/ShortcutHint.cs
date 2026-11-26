using System.Windows;

namespace CsvAnalyser.App.Controls;

/// <summary>
/// <c>controls:ShortcutHint.Action="file.open"</c> on a button: its tooltip gets a "Raccourci : Ctrl+O" line with the
/// gesture currently chosen in Paramètres, added each time it opens (the plain tooltip, already translated, is kept).
/// </summary>
public static class ShortcutHint
{
    /// <summary>Action id → gesture as shown ("" = none); set by the main window.</summary>
    public static Func<string, string>? Resolve { get; set; }

    public static readonly DependencyProperty ActionProperty = DependencyProperty.RegisterAttached(
        "Action", typeof(string), typeof(ShortcutHint), new PropertyMetadata(null, OnActionChanged));

    public static string? GetAction(DependencyObject d) => (string?)d.GetValue(ActionProperty);

    public static void SetAction(DependencyObject d, string? value) => d.SetValue(ActionProperty, value);

    private static readonly DependencyProperty BaseToolTipProperty = DependencyProperty.RegisterAttached(
        "BaseToolTip", typeof(string), typeof(ShortcutHint));

    private static void OnActionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is System.Windows.Controls.MenuItem item)
        {
            // A menu entry shows the gesture on its right, read each time the menu opens.
            item.Loaded -= OnMenuItemLoaded;
            if (e.NewValue is string)
                item.Loaded += OnMenuItemLoaded;
            return;
        }
        if (d is not FrameworkElement element)
            return;
        element.ToolTipOpening -= OnToolTipOpening;
        if (e.NewValue is string)
        {
            element.ToolTipOpening += OnToolTipOpening;
            // A button without its own tooltip still shows its shortcut.
            element.ToolTip ??= "";
        }
    }

    private static void OnMenuItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem item && GetAction(item) is { } id)
            item.InputGestureText = Resolve?.Invoke(id) ?? "";
    }

    private static void OnToolTipOpening(object sender, System.Windows.Controls.ToolTipEventArgs e)
    {
        if (sender is not FrameworkElement element || GetAction(element) is not { } id)
            return;
        if (element.GetValue(BaseToolTipProperty) is not string text)
        {
            if (element.ToolTip is not string plain)
                return;
            element.SetValue(BaseToolTipProperty, text = plain);
        }
        string gesture = Resolve?.Invoke(id) ?? "";
        string tip = gesture.Length == 0 ? text
            : text.Length == 0 ? L.F("Raccourci : {0}", gesture)
            : text + "\n" + L.F("Raccourci : {0}", gesture);
        if (tip.Length == 0)
            e.Handled = true; // nothing to show
        else
            element.ToolTip = tip;
    }
}
