using System.Windows;
using System.Windows.Input;
using CsvAnalyser.App.ViewModels;
using Wpf.Ui.Controls;

namespace CsvAnalyser.App;

/// <summary>Name, unit, color and style of one curve. Bound directly to the curve, so changes apply live.</summary>
public partial class CurveSettingsWindow : FluentWindow
{
    private readonly ChannelItemViewModel _item;

    /// <param name="pointCloud">The X axis is unordered: the curve is drawn as points only, so the line settings are hidden.</param>
    public CurveSettingsWindow(ChannelItemViewModel item, IReadOnlyList<StyleChoice<string>> axisChoices, bool pointCloud)
    {
        InitializeComponent();
        AxisCombo.ItemsSource = axisChoices;
        DataContext = _item = item;
        if (pointCloud)
        {
            PointCloudNote.Visibility = Visibility.Visible;
            LineSection.Visibility = Visibility.Collapsed;
            SpacingRow.Visibility = Visibility.Collapsed;
            SpacingHelp.Visibility = Visibility.Collapsed;
            LinePreview.Visibility = Visibility.Collapsed;
            DotPreview.Visibility = Visibility.Visible;
        }
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
