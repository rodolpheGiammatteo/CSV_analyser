using System.Windows;
using System.Windows.Input;
using CsvAnalyser.App.ViewModels;
using Wpf.Ui.Controls;

namespace CsvAnalyser.App;

/// <summary>Auto/manual range of every Y axis, and zero alignment per graph. Changes apply live.</summary>
public partial class AxesWindow : FluentWindow
{
    public AxesWindow(MainViewModel viewModel, GraphViewModel? focus)
    {
        InitializeComponent();

        var groups = viewModel.BuildLayout()
            .Select(g => new GraphAxesViewModel(g.Graph, g.Axes.Select(a =>
            {
                // Switching to manual starts from what is on screen.
                if (viewModel.CurrentAxisRange?.Invoke(g.Graph, a.Key) is { } range)
                    a.Settings.PrefillIfEmpty(range.Min, range.Max);
                return new AxisRowViewModel(a);
            }).ToList()))
            .ToList();
        GraphList.ItemsSource = groups;

        if (focus is not null && groups.FindIndex(g => g.Graph == focus) is > 0 and var index)
        {
            Loaded += (_, _) =>
            {
                if (GraphList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement item)
                    item.BringIntoView();
            };
        }
    }

    private void OnBoundKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is System.Windows.Controls.TextBox box)
            box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
