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

    /// <summary>The user asked to restart now to apply another language (done by the main window).</summary>
    public bool RestartRequested { get; private set; }

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        RestartRequested = true;
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnShortcuts(object sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow main)
            new ShortcutsWindow(main) { Owner = this }.ShowDialog();
    }
}
