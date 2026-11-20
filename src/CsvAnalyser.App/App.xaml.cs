using System.IO;
using System.Windows;
using System.Windows.Threading;
using CsvAnalyser.App.Services;
using CsvAnalyser.App.ViewModels;

namespace CsvAnalyser.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var viewModel = new MainViewModel(AppSettings.Load());
        var window = new MainWindow(viewModel);
        window.Show();

        if (e.Args.Length > 0 && File.Exists(e.Args[0]))
            _ = viewModel.LoadFileAsync(e.Args[0]);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Erreur inattendue", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
