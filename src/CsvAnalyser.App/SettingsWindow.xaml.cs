using System.Windows;
using CsvAnalyser.App.ViewModels;
using Wpf.Ui.Controls;

namespace CsvAnalyser.App;

/// <summary>Display preferences. Bound directly to <see cref="MainViewModel"/>, so every change is applied live.</summary>
public partial class SettingsWindow : FluentWindow
{
    public SettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
