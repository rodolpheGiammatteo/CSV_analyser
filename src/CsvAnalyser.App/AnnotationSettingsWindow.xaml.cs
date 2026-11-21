using System.Windows;
using System.Windows.Input;
using CsvAnalyser.App.ViewModels;
using Wpf.Ui.Controls;

namespace CsvAnalyser.App;

/// <summary>Text, color, sizes, line, opacity and graph of one annotation. Bound directly to it, so changes apply live.</summary>
public partial class AnnotationSettingsWindow : FluentWindow
{
    private readonly AnnotationViewModel _annotation;
    private readonly MainViewModel _vm;

    public AnnotationSettingsWindow(AnnotationViewModel annotation, MainViewModel vm)
    {
        InitializeComponent();
        _annotation = annotation;
        _vm = vm;
        annotation.GraphChoices = vm.AnnotationGraphChoices();
        DataContext = annotation;
        Loaded += (_, _) =>
        {
            TextField.Focus();
            TextField.SelectAll();
        };
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // The text box updates the annotation 300 ms after typing: what was typed just before closing is kept.
        TextField.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        base.OnClosing(e);
    }

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is System.Windows.Controls.TextBox box)
            box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        _vm.DeleteAnnotation(_annotation);
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
