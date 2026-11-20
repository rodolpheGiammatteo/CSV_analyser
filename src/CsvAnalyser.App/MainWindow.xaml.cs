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
        viewModel.EditChannelRequested += (_, item) =>
            new CurveSettingsWindow(item) { Owner = this }.ShowDialog();
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

        // Opaque equivalents of the Fluent secondary/placeholder text: semi-transparent text renders far too
        // light here. Top-level resources override the theme dictionaries.
        SetBrush("AppSecondaryTextBrush", dark ? 0xC8 : 0x5C);
        SetBrush("AppDisabledTextBrush", dark ? 0x6A : 0x90);
        SetBrush("TextControlPlaceholderForeground", dark ? 0xA8 : 0x70);
        SetBrush("TextPlaceholderColorBrush", dark ? 0xA8 : 0x70);
    }

    private static void SetBrush(string key, int gray)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)gray, (byte)gray, (byte)gray));
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) =>
        new SettingsWindow(_viewModel) { Owner = this }.ShowDialog();

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
