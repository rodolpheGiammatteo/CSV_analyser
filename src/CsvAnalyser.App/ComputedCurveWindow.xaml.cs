using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CsvAnalyser.App.ViewModels;
using Wpf.Ui.Controls;

namespace CsvAnalyser.App;

public partial class ComputedCurveWindow : FluentWindow
{
    private readonly ComputedCurveViewModel _viewModel;
    private readonly MainViewModel _main;

    public ComputedCurveWindow(MainViewModel main, ComputedCurveViewModel viewModel)
    {
        InitializeComponent();
        _main = main;
        DataContext = _viewModel = viewModel;
        Loaded += (_, _) =>
        {
            if (viewModel.IsFormula)
                Keyboard.Focus(ExpressionBox);
        };
    }

    /// <summary>Inserts "[curve]" at the caret of the formula, then clears the picker.</summary>
    private void OnInsertCurve(object? sender, object picked)
    {
        if (picked is not ChannelItemViewModel item)
            return;
        string reference = ComputedCurveViewModel.Reference(item);
        int caret = ExpressionBox.CaretIndex;
        ExpressionBox.Text = ExpressionBox.Text.Insert(caret, reference);
        ExpressionBox.CaretIndex = caret + reference.Length;
        Dispatcher.BeginInvoke(() => Keyboard.Focus(ExpressionBox), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_viewModel.BuildDefinition() is not { } def)
            return;
        Cursor = Cursors.Wait;
        try
        {
            _viewModel.Error = _main.AddOrReplaceComputed(def, _viewModel.Editing);
        }
        finally
        {
            Cursor = null;
        }
        if (_viewModel.Error is null)
            DialogResult = true;
    }
}
