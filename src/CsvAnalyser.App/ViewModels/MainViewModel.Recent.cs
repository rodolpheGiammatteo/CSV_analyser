using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;

namespace CsvAnalyser.App.ViewModels;

/// <summary>A recently opened CSV file or project.</summary>
public sealed record RecentFile(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";

    public bool IsProject => Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Recent files (toolbar clock button, start screen) and the resolution of exported images.</summary>
public partial class MainViewModel
{
    private const int MaxRecent = 12;
    private ObservableCollection<RecentFile>? _recentFiles;

    /// <summary>Most recent first.</summary>
    public ObservableCollection<RecentFile> RecentFiles =>
        _recentFiles ??= new(_settings.RecentFiles.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => new RecentFile(p)));

    public bool HasRecentFiles => RecentFiles.Count > 0;

    /// <summary>The few shown on the start screen.</summary>
    public IReadOnlyList<RecentFile> StartRecentFiles => RecentFiles.Take(6).ToList();

    /// <summary>Puts a file at the top of the recent list (after it was opened or saved).</summary>
    public void AddRecent(string path)
    {
        string full = Path.GetFullPath(path);
        var existing = RecentFiles.FirstOrDefault(r => string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            RecentFiles.Remove(existing);
        RecentFiles.Insert(0, new RecentFile(full));
        while (RecentFiles.Count > MaxRecent)
            RecentFiles.RemoveAt(RecentFiles.Count - 1);
        SaveRecent();
    }

    [RelayCommand]
    private async Task OpenRecent(string? path)
    {
        if (path is null)
            return;
        if (!File.Exists(path))
        {
            MessageBox.Show(L.F("« {0} » est introuvable (déplacé ou renommé ?).", path), L.T("Fichier introuvable"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            if (RecentFiles.FirstOrDefault(r => r.Path == path) is { } gone)
                RecentFiles.Remove(gone);
            SaveRecent();
            return;
        }
        await LoadFileAsync(path);
    }

    [RelayCommand]
    private void ClearRecent()
    {
        RecentFiles.Clear();
        SaveRecent();
    }

    private void SaveRecent()
    {
        _settings.RecentFiles = RecentFiles.Select(r => r.Path).ToList();
        _settings.Save();
        OnPropertyChanged(nameof(HasRecentFiles));
        OnPropertyChanged(nameof(StartRecentFiles));
    }

    // ---------------------------------------------------------------- image resolution

    /// <summary>Copied and saved graph images: their size on screen × this factor.</summary>
    public double ImageScale
    {
        get => _settings.ImageScale is >= 1 and <= 8 ? _settings.ImageScale : 1;
        set
        {
            _settings.ImageScale = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public static IReadOnlyList<StyleChoice<double>> ImageScaleChoices { get; } =
    [
        new(1, "Comme à l'écran"),
        new(2, "× 2 (2 fois plus de pixels en largeur et en hauteur)"),
        new(3, "× 3"),
        new(4, "× 4"),
    ];
}
