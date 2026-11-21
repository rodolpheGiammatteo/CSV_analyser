using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsvAnalyser.App.Services;

namespace CsvAnalyser.App.ViewModels;

/// <summary>Version shown in Paramètres and the search for a newer version on GitHub (see <see cref="UpdateChecker"/>).</summary>
public partial class MainViewModel
{
    public string AppVersionText => L.F("Version {0}", AppInfo.Version);

    /// <summary>Look for a newer version when the program starts (quietly: only a newer version is reported).</summary>
    [ObservableProperty]
    private bool _checkUpdatesAtStartup;

    partial void OnCheckUpdatesAtStartupChanged(bool value)
    {
        _settings.CheckUpdatesAtStartup = value;
        _settings.Save();
    }

    /// <summary>Paramètres button: the result in a message (not supported yet, up to date, newer version, error).</summary>
    [RelayCommand]
    private async Task CheckForUpdates()
    {
        var result = await UpdateChecker.CheckAsync(AppInfo.Version);
        string title = L.T("Mise à jour");
        switch (result.Status)
        {
            case UpdateStatus.NotSupported:
                MessageBox.Show(L.T("La recherche de mise à jour n'est pas encore prise en charge.\n\nElle consultera bientôt les versions publiées sur GitHub ; en attendant, une nouvelle version s'installe en remplaçant simplement le fichier .exe."),
                    title, MessageBoxButton.OK, MessageBoxImage.Information);
                break;
            case UpdateStatus.UpToDate:
                MessageBox.Show(L.F("Vous avez la dernière version ({0}).", AppInfo.Version), title, MessageBoxButton.OK, MessageBoxImage.Information);
                break;
            case UpdateStatus.Available:
                if (MessageBox.Show(L.F("La version {0} est disponible (vous avez la {1}).\n\nOuvrir sa page de téléchargement ?", result.LatestVersion, AppInfo.Version),
                        title, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    OpenReleasePage(result.PageUrl);
                break;
            default:
                MessageBox.Show(L.F("La recherche de mise à jour a échoué : {0}", result.Error), title, MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
    }

    /// <summary>At startup, when the option is on: a newer version is announced in the status bar, nothing else is shown.</summary>
    public async Task CheckUpdatesQuietlyAsync()
    {
        if (!CheckUpdatesAtStartup)
            return;
        UpdateCheckResult result;
        try
        {
            result = await UpdateChecker.CheckAsync(AppInfo.Version);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return; // quiet at startup: the button reports errors
        }
        if (result.Status == UpdateStatus.Available)
            StatusText = L.F("Nouvelle version disponible : {0} (Paramètres → Rechercher une mise à jour)", result.LatestVersion);
    }

    private static void OpenReleasePage(string? url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url ?? UpdateChecker.ReleasesPage) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(ex.Message, L.T("Mise à jour"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
