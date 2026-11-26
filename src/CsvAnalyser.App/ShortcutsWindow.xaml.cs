using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CsvAnalyser.App.Services;
using Wpf.Ui.Controls;

namespace CsvAnalyser.App;

/// <summary>One line of the shortcuts window: an action and the gesture being edited.</summary>
public sealed partial class ShortcutRow(ShortcutAction action, string gesture) : ObservableObject
{
    public ShortcutAction Action { get; } = action;

    /// <summary>French group name, the grouping key.</summary>
    public string Category => Action.Category;

    public string CategoryText => L.T(Action.Category);

    public string NameText => L.T(Action.Name);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private string _gesture = gesture;

    /// <summary>True while the box waits for a key combination.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private bool _isRecording;

    public string DisplayText => IsRecording ? L.T("Appuyez sur les touches…")
        : Gesture.Length == 0 ? "—"
        : ShortcutGesture.Display(Gesture);
}

/// <summary>
/// Paramètres → Raccourcis clavier: every action with its gesture; click a box and press the keys. Nothing is saved
/// until "Enregistrer" (the main window then applies the new gestures at once).
/// </summary>
public partial class ShortcutsWindow : FluentWindow
{
    private readonly MainWindow _main;
    private readonly List<ShortcutRow> _rows;
    private readonly ICollectionView _view;

    public ShortcutsWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        _rows = main.ShortcutActions
            .Select(a => new ShortcutRow(a, main.CurrentShortcuts.GetValueOrDefault(a.Id, "")))
            .ToList();
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ShortcutRow.Category)));
        _view.Filter = Matches;
        ActionList.ItemsSource = _view;
    }

    // ---- Search: every word must appear in the name, the group or the gesture (accents ignored) ----

    private bool Matches(object o)
    {
        if (o is not ShortcutRow row || string.IsNullOrWhiteSpace(SearchBox.Text))
            return true;
        string text = Fold($"{row.NameText} {row.CategoryText} {row.DisplayText}");
        return SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => text.Contains(Fold(w), StringComparison.Ordinal));
    }

    private static string Fold(string s)
    {
        var chars = s.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark);
        return new string(chars.ToArray()).ToLowerInvariant();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => _view.Refresh();

    // ---- Recording a gesture ----

    private static ShortcutRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as ShortcutRow;

    /// <summary>A click in the box that already has the focus: waits for a new combination again.</summary>
    private void OnGestureClick(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(sender) is { } row)
            row.IsRecording = true;
    }

    private void OnGestureFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            row.IsRecording = true;
        ShowMessage(null);
    }

    private void OnGestureBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            row.IsRecording = false;
    }

    private void OnGestureKeyDown(object sender, KeyEventArgs e)
    {
        if (RowOf(sender) is not { } row)
            return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab)
            return; // moves to the next box
        e.Handled = true;
        if (key == Key.Escape)
        {
            Keyboard.Focus(SearchBox); // stop recording, the window stays open
            return;
        }
        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            row.Gesture = "";
            row.IsRecording = false; // shown at once; another combination still replaces it
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt)
            return; // waiting for the key itself
        if (ShortcutGesture.FromKeyEvent(e) is not { } gesture)
        {
            ShowMessage(L.T("Ajoutez Ctrl ou Alt : seules les touches F1 à F12 peuvent servir seules."));
            return;
        }
        SetGesture(row, gesture);
    }

    private void SetGesture(ShortcutRow row, string gesture)
    {
        if (gesture.Length > 0 && _rows.FirstOrDefault(r => r != row && string.Equals(r.Gesture, gesture, StringComparison.OrdinalIgnoreCase)) is { } other)
        {
            var answer = System.Windows.MessageBox.Show(this,
                L.F("{0} est déjà le raccourci de « {1} ».\n\nLe donner à « {2} » à la place ?", ShortcutGesture.Display(gesture), other.NameText, row.NameText),
                L.T("Raccourci déjà utilisé"), System.Windows.MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes)
                return;
            other.Gesture = "";
        }
        row.Gesture = gesture;
        row.IsRecording = false; // the combination is shown at once, no need to click elsewhere
        ShowMessage(null);
    }

    private void ShowMessage(string? text)
    {
        Message.Text = text ?? "";
        Message.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnClearGesture(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            row.Gesture = "";
    }

    private void OnDefaultGesture(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            SetGesture(row, row.Action.Default);
    }

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
            row.Gesture = row.Action.Default;
        ShowMessage(null);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _main.SaveShortcuts(_rows.ToDictionary(r => r.Action.Id, r => r.Gesture));
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
