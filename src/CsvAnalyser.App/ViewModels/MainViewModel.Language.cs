using CommunityToolkit.Mvvm.ComponentModel;
using CsvAnalyser.App.Localization;

namespace CsvAnalyser.App.ViewModels;

/// <summary>Interface language (Paramètres). It applies at the next start.</summary>
public partial class MainViewModel
{
    public IReadOnlyList<LanguageInfo> AvailableLanguages { get; } = Languages.Available();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LanguageRestartNeeded))]
    private string _languageCode = Languages.Current;

    /// <summary>Another language was chosen: shown until the app restarts.</summary>
    public bool LanguageRestartNeeded => LanguageCode != Languages.Current;

    partial void OnLanguageCodeChanged(string value)
    {
        _settings.Language = value;
        _settings.Save();
    }
}
