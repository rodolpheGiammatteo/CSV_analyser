using System.Windows;
using System.Windows.Input;
using CsvAnalyser.App.Controls;
using CsvAnalyser.App.Services;
using CsvAnalyser.App.ViewModels;
using CsvAnalyser.Core.Shortcuts;

namespace CsvAnalyser.App;

/// <summary>One action that can get a keyboard shortcut (Paramètres → Raccourcis clavier).</summary>
/// <param name="Id">Stable key stored in the settings.</param>
/// <param name="Category">French group name (translated on screen).</param>
/// <param name="Name">French name (translated on screen).</param>
/// <param name="Default">Default gesture ("Ctrl+O"), "" = none.</param>
public sealed record ShortcutAction(string Id, string Category, string Name, string Default, Action Run);

/// <summary>
/// Keyboard shortcuts: every toolbar button and menu action has an id and a gesture the user can change. They are
/// dispatched on KeyDown (bubbling), so a text box keeps its own Ctrl+Z, Ctrl+A… while it has the focus.
/// Actions on "the active curve" use the curve under the mouse in a graph, else the last curve clicked in the list
/// or the cursor panel.
/// </summary>
public partial class MainWindow
{
    private IReadOnlyList<ShortcutAction> _shortcutActions = [];
    private Dictionary<string, string> _shortcutMap = [];
    private readonly Dictionary<string, ShortcutAction> _actionByGesture = new(StringComparer.OrdinalIgnoreCase);
    private ChannelItemViewModel? _lastClickedCurve;

    public IReadOnlyList<ShortcutAction> ShortcutActions => _shortcutActions;

    /// <summary>Gesture of every action (id → "Ctrl+O", "" = none).</summary>
    public IReadOnlyDictionary<string, string> CurrentShortcuts => _shortcutMap;

    private void SetupShortcuts()
    {
        _shortcutActions = BuildShortcutActions();
        ApplyShortcuts();
        ShortcutHint.Resolve = id => _shortcutMap.TryGetValue(id, out var g) ? ShortcutGesture.Display(g) : "";
    }

    /// <summary>Saves the gestures chosen in the shortcuts window (only those that differ from the defaults).</summary>
    public void SaveShortcuts(IReadOnlyDictionary<string, string> map)
    {
        _viewModel.Settings.Shortcuts = ShortcutMap.Overrides(map, Defaults());
        _viewModel.Settings.Save();
        ApplyShortcuts();
    }

    private IEnumerable<(string Id, string Default)> Defaults() => _shortcutActions.Select(a => (a.Id, a.Default));

    private void ApplyShortcuts()
    {
        _shortcutMap = ShortcutMap.Effective(Defaults(), _viewModel.Settings.Shortcuts);
        _actionByGesture.Clear();
        foreach (var action in _shortcutActions)
            if (_shortcutMap.TryGetValue(action.Id, out var g) && g.Length > 0)
                _actionByGesture[g] = action;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || ShortcutGesture.FromKeyEvent(e) is not { } gesture)
            return;
        if (_actionByGesture.TryGetValue(gesture, out var action))
        {
            e.Handled = true;
            action.Run();
        }
        else if (gesture == "Ctrl+Shift+Z" && _shortcutMap.GetValueOrDefault("edit.redo") == "Ctrl+Y")
        {
            // Second usual gesture of Rétablir, while Rétablir keeps its default and nothing else took it.
            e.Handled = true;
            _actionByGesture.GetValueOrDefault("Ctrl+Y")?.Run();
        }
    }

    /// <summary>The curve under the mouse in a graph, else the last one clicked in the list or the cursor panel.</summary>
    private ChannelItemViewModel? ActiveCurve()
    {
        if (_plotController.CurveUnderMouse() is { } underMouse)
            return underMouse;
        if (_lastClickedCurve is { } clicked && _viewModel.Channels.Contains(clicked))
            return clicked;
        return CurveListBox.SelectedItem as ChannelItemViewModel;
    }

    private IReadOnlyList<ShortcutAction> BuildShortcutActions()
    {
        var vm = _viewModel;
        const string file = "Fichier", edit = "Édition", view = "Affichage", curves = "Courbe active",
            calc = "Calculs", measure = "Mesures", annotations = "Annotations";

        Action Cmd(ICommand command, object? parameter = null) => () =>
        {
            if (command.CanExecute(parameter))
                command.Execute(parameter);
        };

        Action Curve(Action<ChannelItemViewModel> run, bool computedOnly = false) => () =>
        {
            if (ActiveCurve() is not { } item)
                vm.StatusText = L.T("Aucune courbe active : cliquez sur une courbe de la liste ou placez la souris sur une courbe.");
            else if (computedOnly && !item.IsComputed)
                vm.StatusText = L.F("« {0} » n'est pas une courbe calculée.", item.Name);
            else
                run(item);
        };

        return
        [
            new("file.open", file, "Ouvrir un fichier CSV ou un projet…", "Ctrl+O", Cmd(vm.OpenCommand)),
            new("file.recent", file, "Fichiers récents…", "", () => OnRecentMenu(RecentButton, new RoutedEventArgs())),
            new("file.save", file, "Enregistrer le projet", "Ctrl+S", Cmd(vm.SaveProjectCommand)),
            new("file.saveAs", file, "Enregistrer le projet sous…", "Ctrl+Shift+S", Cmd(vm.SaveProjectAsCommand)),
            new("file.openProject", file, "Ouvrir un projet…", "", Cmd(vm.OpenProjectCommand)),
            new("file.applyLayout", file, "Appliquer une mise en page…", "", Cmd(vm.OpenConfigurationCommand)),
            new("file.export", file, "Exporter en CSV…", "Ctrl+E", Cmd(vm.ExportCommand)),
            new("file.showFile", file, "Afficher le fichier CSV dans l'Explorateur", "", Cmd(vm.ShowFileInExplorerCommand)),

            new("edit.undo", edit, "Annuler la dernière action", "Ctrl+Z", Cmd(vm.UndoViewCommand)),
            new("edit.redo", edit, "Rétablir", "Ctrl+Y", Cmd(vm.RedoViewCommand)),

            new("view.fitAll", view, "Tout ajuster (X et Y)", "", Cmd(vm.AutoScaleCommand)),
            new("view.fitX", view, "Ajuster X", "", Cmd(vm.AutoScaleXCommand)),
            new("view.fitY", view, "Ajuster Y", "", Cmd(vm.AutoScaleYCommand)),
            new("view.autoAxes", view, "Tous les axes en automatique", "", Cmd(vm.AutoAllAxesCommand, "xy")),
            new("view.curveList", view, "Masquer ou afficher la liste des courbes", "Ctrl+L",
                () => SetCurveListHidden(LeftPanel.Visibility == Visibility.Visible)),
            new("view.search", view, "Rechercher une courbe", "", () =>
            {
                SetCurveListHidden(false);
                SearchBox.Focus();
                SearchBox.SelectAll();
            }),
            new("view.plottedOnly", view, "Afficher seulement les courbes cochées", "", () => vm.ShowPlottedOnly = !vm.ShowPlottedOnly),
            new("view.hideAll", view, "Masquer toutes les courbes", "", Cmd(vm.ClearSelectionCommand)),
            new("view.graphs", view, "Graphes : titres, légendes et axes…", "", Cmd(vm.EditAxesCommand)),
            new("view.settings", view, "Paramètres…", "", () => OnOpenSettings(this, new RoutedEventArgs())),

            new("curve.computedFrom", curves, "Nouvelle courbe calculée à partir de la courbe active…", "", Curve(c => vm.NewComputedCommand.Execute(c))),
            new("curve.settings", curves, "Réglages de la courbe active…", "", Curve(c => vm.EditChannelCommand.Execute(c))),
            new("curve.editComputed", curves, "Modifier le calcul de la courbe active…", "", Curve(c => vm.EditComputedCommand.Execute(c), computedOnly: true)),
            new("curve.duplicate", curves, "Dupliquer la courbe active", "", Curve(c => vm.DuplicateCurves([c]))),
            new("curve.toggle", curves, "Cocher ou décocher la courbe active", "", Curve(c => c.IsPlotted = !c.IsPlotted)),
            new("curve.remove", curves, "Retirer la courbe active du graphe", "", Curve(vm.RemoveFromGraph)),
            new("curve.delete", curves, "Supprimer la courbe calculée active", "", Curve(c => vm.DeleteComputedCommand.Execute(c), computedOnly: true)),

            new("calc.new", calc, "Nouvelle courbe calculée…", "", Cmd(vm.NewComputedCommand)),
            new("calc.spectrum", calc, "Spectre (FFT)…", "", Cmd(vm.ShowSpectrumCommand)),

            new("measure.x", measure, "Mesures X (curseurs X1 et X2)", "Ctrl+M", Cmd(vm.ToggleMeasureCommand)),
            new("measure.y", measure, "Mesures Y (curseurs Y1 et Y2)", "Ctrl+Shift+M", Cmd(vm.ToggleMeasureYCommand)),
            new("measure.zone", measure, "Zone X1–X2 (surligner entre les curseurs)", "", Cmd(vm.ZoneFromMeasureCommand)),

            new("annotations.show", annotations, "Afficher ou masquer les annotations", "", Cmd(vm.ToggleShowAnnotationsCommand)),
        ];
    }
}
