using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CsvAnalyser.App.ViewModels;

namespace CsvAnalyser.App.Controls;

/// <summary>
/// A combo box for long lists (curves, X axes): the drop-down has a search box that filters the items
/// (every typed word must appear, accents and case ignored).
/// </summary>
public partial class SearchPicker : UserControl
{
    private ListCollectionView? _view;
    private string[] _words = [];

    public SearchPicker()
    {
        InitializeComponent();
        UpdateSelectedText();
    }

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(SearchPicker),
        new PropertyMetadata(null, (d, _) => ((SearchPicker)d).OnItemsSourceChanged()));

    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
        nameof(SelectedItem), typeof(object), typeof(SearchPicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((SearchPicker)d).UpdateSelectedText()));

    public static readonly DependencyProperty DisplayMemberPathProperty = DependencyProperty.Register(
        nameof(DisplayMemberPath), typeof(string), typeof(SearchPicker),
        new PropertyMetadata("", (d, _) => ((SearchPicker)d).OnItemsSourceChanged()));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(SearchPicker),
        new PropertyMetadata(L.T("Choisir…"), (d, _) => ((SearchPicker)d).UpdateSelectedText()));

    public static readonly DependencyProperty KeepSelectionProperty = DependencyProperty.Register(
        nameof(KeepSelection), typeof(bool), typeof(SearchPicker), new PropertyMetadata(true));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    /// <summary>Property shown for each item (and searched).</summary>
    public string DisplayMemberPath
    {
        get => (string)GetValue(DisplayMemberPathProperty);
        set => SetValue(DisplayMemberPathProperty, value);
    }

    /// <summary>Text shown while nothing is selected (or always, when <see cref="KeepSelection"/> is false).</summary>
    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>False for an action picker ("Insérer une courbe…"): <see cref="Picked"/> is raised, nothing stays selected.</summary>
    public bool KeepSelection
    {
        get => (bool)GetValue(KeepSelectionProperty);
        set => SetValue(KeepSelectionProperty, value);
    }

    /// <summary>Raised each time the user picks an item.</summary>
    public event EventHandler<object>? Picked;

    private void OnItemsSourceChanged()
    {
        List.DisplayMemberPath = DisplayMemberPath;
        if (ItemsSource is null)
        {
            _view = null;
            List.ItemsSource = null;
        }
        else
        {
            var list = ItemsSource as IList ?? ItemsSource.Cast<object>().ToList();
            _view = new ListCollectionView(list) { Filter = Matches };
            List.ItemsSource = _view;
            if (ItemsSource is INotifyCollectionChanged)
                _view.Refresh();
        }
        UpdateSelectedText();
    }

    private string TextOf(object? item)
    {
        if (item is null)
            return "";
        if (string.IsNullOrEmpty(DisplayMemberPath))
            return item.ToString() ?? "";
        return item.GetType().GetProperty(DisplayMemberPath)?.GetValue(item)?.ToString() ?? "";
    }

    private void UpdateSelectedText()
    {
        bool showItem = KeepSelection && SelectedItem is not null;
        SelectedText.Text = showItem ? TextOf(SelectedItem) : Placeholder;
        SelectedText.SetResourceReference(TextBlock.ForegroundProperty, showItem ? "TextFillColorPrimaryBrush" : "AppSecondaryTextBrush");
        SelectedText.ToolTip = showItem ? SelectedText.Text : null;
    }

    private bool Matches(object item)
    {
        if (_words.Length == 0)
            return true;
        string text = Normalize(TextOf(item));
        if (item is ChannelItemViewModel channel)
            text += " " + Normalize(channel.Channel.SourceName);
        return _words.All(text.Contains);
    }

    /// <summary>Lower case without accents: "Débit" matches "debit".</summary>
    private static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        return new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        DropDownBorder.MinWidth = Math.Max(ActualWidth, 280);
        SearchBox.Text = "";
        DropDown.IsOpen = true;
    }

    private void OnPopupOpened(object? sender, EventArgs e)
    {
        _view?.Refresh();
        if (KeepSelection && SelectedItem is not null)
        {
            List.SelectedItem = SelectedItem;
            List.ScrollIntoView(SelectedItem);
        }
        Dispatcher.BeginInvoke(() => Keyboard.Focus(SearchBox), DispatcherPriority.Input);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _words = Normalize(SearchBox.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _view?.Refresh();
        bool any = _view is { IsEmpty: false };
        NoMatch.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (any && _words.Length > 0)
            List.SelectedIndex = 0;
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (List.Items.Count > 0)
                {
                    if (List.SelectedIndex < 0)
                        List.SelectedIndex = 0;
                    (List.ItemContainerGenerator.ContainerFromIndex(List.SelectedIndex) as ListBoxItem)?.Focus();
                }
                e.Handled = true;
                break;
            case Key.Enter:
                Pick(List.SelectedItem ?? (List.Items.Count > 0 ? List.Items[0] : null));
                e.Handled = true;
                break;
            case Key.Escape:
                DropDown.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Pick(List.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DropDown.IsOpen = false;
            e.Handled = true;
        }
    }

    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not ListBoxItem)
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        if (element is ListBoxItem item)
            Pick(item.DataContext);
    }

    private void Pick(object? item)
    {
        if (item is null)
            return;
        DropDown.IsOpen = false;
        if (KeepSelection)
            SelectedItem = item;
        Picked?.Invoke(this, item);
        UpdateSelectedText();
    }
}
