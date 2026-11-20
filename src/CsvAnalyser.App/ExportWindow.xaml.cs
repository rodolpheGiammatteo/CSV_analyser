using System.IO;
using System.Windows;
using CsvAnalyser.App.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace CsvAnalyser.App;

public partial class ExportWindow : FluentWindow
{
    private readonly ExportViewModel _viewModel;

    public ExportWindow(ExportViewModel viewModel, string? initialFolder)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        InitialFolder = initialFolder;
    }

    private string? InitialFolder { get; }

    /// <summary>The file chosen by the user (set when the window closes with OK).</summary>
    public string? ChosenPath { get; private set; }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Exporter en CSV",
            Filter = "Fichier CSV (*.csv)|*.csv|Fichier texte (*.txt)|*.txt",
            FileName = _viewModel.SuggestedFileName,
        };
        if (InitialFolder is { } folder && Directory.Exists(folder))
            dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(this) != true)
            return;
        ChosenPath = dialog.FileName;
        _viewModel.RememberChoices();
        DialogResult = true;
    }
}
