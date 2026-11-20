using System.Windows;
using System.Windows.Media;
using CsvAnalyser.App.Plotting;
using CsvAnalyser.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace CsvAnalyser.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly PlotController _plotController;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _plotController = new PlotController(PlotView, viewModel);

        ApplyAppTheme();
        viewModel.PlotChanged += (_, change) =>
        {
            if (change == PlotChange.Theme)
                ApplyAppTheme();
        };
    }

    private void ApplyAppTheme()
    {
        bool dark = _viewModel.SelectedTheme.IsDarkApp;
        ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.None);

        // Opaque equivalent of the Fluent secondary text: semi-transparent text renders far too light here.
        var secondary = new SolidColorBrush(dark ? Color.FromRgb(0xC8, 0xC8, 0xC8) : Color.FromRgb(0x5C, 0x5C, 0x5C));
        secondary.Freeze();
        Application.Current.Resources["AppSecondaryTextBrush"] = secondary;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            await _viewModel.LoadFileAsync(files[0]);
    }
}
