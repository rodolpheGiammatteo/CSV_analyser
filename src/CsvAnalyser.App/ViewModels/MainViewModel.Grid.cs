using System.Runtime.CompilerServices;
using CsvAnalyser.App.Plotting;

namespace CsvAnalyser.App.ViewModels;

/// <summary>Background grid of the graphs (Paramètres). Applied live, saved in the settings, not part of projects.</summary>
public partial class MainViewModel
{
    public bool GridVertical
    {
        get => _settings.GridVertical;
        set => SetGrid(() => _settings.GridVertical = value);
    }

    public bool GridHorizontal
    {
        get => _settings.GridHorizontal;
        set => SetGrid(() => _settings.GridHorizontal = value);
    }

    public bool GridMinor
    {
        get => _settings.GridMinor;
        set => SetGrid(() => _settings.GridMinor = value);
    }

    public string GridPattern
    {
        get => _settings.GridPattern;
        set => SetGrid(() => _settings.GridPattern = value);
    }

    /// <summary>From faint (the background color) to strong (the axis color).</summary>
    public double GridIntensity
    {
        get => Math.Clamp(_settings.GridIntensity, 0.03, 0.8);
        set => SetGrid(() => _settings.GridIntensity = Math.Round(value, 2));
    }

    public double GridWidth
    {
        get => Math.Clamp(_settings.GridWidth, 0.5, 4);
        set => SetGrid(() => _settings.GridWidth = Math.Round(value, 2));
    }

    public bool GridBands
    {
        get => _settings.GridBands;
        set => SetGrid(() => _settings.GridBands = value);
    }

    public static IReadOnlyList<StyleChoice<string>> GridPatternChoices { get; } =
    [
        new("Solid", "Continu"),
        new("Dashed", "Tirets"),
        new("Dotted", "Pointillés"),
    ];

    /// <summary>The grid as the plots draw it.</summary>
    public GridOptions Grid => new(GridVertical, GridHorizontal, GridMinor, GridPattern, GridIntensity, GridWidth, GridBands);

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ResetGrid()
    {
        var defaults = new Services.AppSettings();
        GridVertical = defaults.GridVertical;
        GridHorizontal = defaults.GridHorizontal;
        GridMinor = defaults.GridMinor;
        GridPattern = defaults.GridPattern;
        GridIntensity = defaults.GridIntensity;
        GridWidth = defaults.GridWidth;
        GridBands = defaults.GridBands;
    }

    private void SetGrid(Action set, [CallerMemberName] string? name = null)
    {
        set();
        _settings.Save();
        OnPropertyChanged(name);
        RaisePlotChanged(PlotChange.Grid);
    }
}
