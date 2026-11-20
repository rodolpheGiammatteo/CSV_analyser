using System.Windows;
using System.Windows.Input;
using CsvAnalyser.App.ViewModels;
using Wpf.Ui.Controls;

namespace CsvAnalyser.App;

/// <summary>Name, unit, color and style of one curve. Bound directly to the curve, so changes apply live.</summary>
public partial class CurveSettingsWindow : FluentWindow
{
    private readonly ChannelItemViewModel _item;

    public CurveSettingsWindow(ChannelItemViewModel item)
    {
        InitializeComponent();
        DataContext = _item = item;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _item.CommitEdits();
        base.OnClosed(e);
    }

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is System.Windows.Controls.TextBox box)
            box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
