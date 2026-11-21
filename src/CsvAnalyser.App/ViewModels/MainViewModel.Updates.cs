using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsvAnalyser.App.Services;

namespace CsvAnalyser.App.ViewModels;

/// <summary>Version shown in Paramètres and the search for a newer version on GitHub (see <see cref="UpdateChecker"/>).</summary>
public partial class MainViewModel
{
    public string AppVersionText => L.F("Version {0}", AppInfo.Version);

    /// <summary>Tag of a newer version found on GitHub ("v5.4.30"): shows the toolbar button until the program closes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvailableUpdate), nameof(AvailableUpdateText))]
    private string? _availableUpdate;

    private string? _availableUpdatePage;

    public bool HasAvailableUpdate => AvailableUpdate is not null;

    public string AvailableUpdateText => AvailableUpdate is null ? "" : L.F("Version {0} disponible", AvailableUpdate.TrimStart('v', 'V'));

    /// <summary>The toolbar button: opens the download page of the newer version.</summary>
    [RelayCommand]
    private void OpenAvailableUpdate() => OpenReleasePage(_availableUpdatePage);

    private void NoteUpdate(UpdateCheckResult result)
    {
        if (result.Status == UpdateStatus.Available)
        {
            _availableUpdatePage = result.PageUrl;
            AvailableUpdate = result.LatestVersion;
        }
        else if (result.Status == UpdateStatus.UpToDate)
            AvailableUpdate = null;
    }

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
        NoteUpdate(result);
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

    /// <summary>
    /// At startup, when the option is on: a newer version shows a button in the toolbar (and a line in the status bar,
    /// which the next message replaces); nothing is shown when up to date or offline.
    /// </summary>
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
        NoteUpdate(result);
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
