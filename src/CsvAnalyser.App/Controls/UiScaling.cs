using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CsvAnalyser.App.Controls;

/// <summary>
/// The "Taille de l'interface" setting (Paramètres) outside the main window: dialogs that set
/// <c>UiScaling.ScaleWindow="True"</c> get their content (not the title bar) scaled and their width multiplied, elements that set
/// <c>UiScaling.Scaled="True"</c> (the panels of the spectrum window, not its plot: a scaled plot is blurry) are
/// scaled, and so are every context menu and the popups passed to <see cref="Apply(Popup)"/>.
/// </summary>
public static class UiScaling
{
    /// <summary>Follows the setting at once (menus, popups, marked elements).</summary>
    private static readonly ScaleTransform Live = new(1, 1);

    private static readonly List<WeakReference<Window>> Windows = [];
    private static readonly DispatcherTimer WindowTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public static double Scale => Live.ScaleX;

    /// <summary>The live scale, for the context menu style (App.xaml), so a menu is placed at its scaled size.</summary>
    public static ScaleTransform Transform => Live;

    static UiScaling()
    {
        WindowTimer.Tick += (_, _) =>
        {
            // Not while the slider of Paramètres is dragged: its window would resize under the mouse.
            if (Mouse.LeftButton == MouseButtonState.Pressed)
                return;
            WindowTimer.Stop();
            foreach (var window in OpenWindows())
                ApplyTo(window);
        };
    }

    /// <summary>Once at startup: context menus follow the setting.</summary>
    public static void Register(double scale)
    {
        Live.ScaleX = Live.ScaleY = scale;
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is ContextMenu menu && !ReferenceEquals(menu.LayoutTransform, Live))
                menu.LayoutTransform = Live;
        }));
    }

    public static void SetScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0 || scale == Live.ScaleX)
            return;
        Live.ScaleX = Live.ScaleY = scale;
        WindowTimer.Stop();
        WindowTimer.Start();
    }

    /// <summary>A popup built in code takes the interface size.</summary>
    public static void Apply(Popup popup)
    {
        if (popup.Child is FrameworkElement child)
            child.LayoutTransform = Live;
    }

    public static readonly DependencyProperty ScaledProperty = DependencyProperty.RegisterAttached(
        "Scaled", typeof(bool), typeof(UiScaling), new PropertyMetadata(false, (d, e) =>
        {
            if (d is FrameworkElement element)
                element.LayoutTransform = (bool)e.NewValue ? Live : System.Windows.Media.Transform.Identity;
        }));

    public static bool GetScaled(DependencyObject d) => (bool)d.GetValue(ScaledProperty);

    public static void SetScaled(DependencyObject d, bool value) => d.SetValue(ScaledProperty, value);

    public static readonly DependencyProperty ScaleWindowProperty = DependencyProperty.RegisterAttached(
        "ScaleWindow", typeof(bool), typeof(UiScaling), new PropertyMetadata(false, (d, e) =>
        {
            if (d is Window window && (bool)e.NewValue)
                window.Initialized += OnWindowInitialized;
        }));

    public static bool GetScaleWindow(DependencyObject d) => (bool)d.GetValue(ScaleWindowProperty);

    public static void SetScaleWindow(DependencyObject d, bool value) => d.SetValue(ScaleWindowProperty, value);

    /// <summary>Width, MaxHeight as written in the XAML (the scaled ones are computed from them).</summary>
    private static readonly DependencyProperty BaseSizeProperty = DependencyProperty.RegisterAttached(
        "BaseSize", typeof(Size), typeof(UiScaling), new PropertyMetadata(Size.Empty));

    private static readonly DependencyProperty WindowTransformProperty = DependencyProperty.RegisterAttached(
        "WindowTransform", typeof(ScaleTransform), typeof(UiScaling), new PropertyMetadata(null));

    private static void OnWindowInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;
        window.SetValue(BaseSizeProperty, new Size(window.Width, window.MaxHeight));
        // Everything but the title bar: WPF-UI places its buttons' hit areas without the transform.
        var transform = new ScaleTransform(1, 1);
        window.SetValue(WindowTransformProperty, transform);
        if (window.Content is Panel root)
        {
            foreach (var child in root.Children.OfType<FrameworkElement>().Where(c => c is not Wpf.Ui.Controls.TitleBar))
                child.LayoutTransform = transform;
        }
        else if (window.Content is FrameworkElement content)
        {
            content.LayoutTransform = transform;
        }
        Windows.Add(new WeakReference<Window>(window));
        window.SizeChanged += (_, _) => KeepOnScreen(window);
        window.Loaded += (_, _) => FitScreen(window);
        ApplyTo(window);
    }

    /// <summary>
    /// A dialog without its own scrolling would be cut at the bottom of the screen: its scale is lowered just
    /// enough for all of it to show (never below 100 % unless the setting is lower).
    /// </summary>
    private static void FitScreen(Window window)
    {
        if (window.Content is not Panel root || window.GetValue(WindowTransformProperty) is not ScaleTransform transform
            || window.GetValue(BaseSizeProperty) is not Size { IsEmpty: false } size || HasScrolling(root))
            return;
        double scale = transform.ScaleX;
        double fixedPart = root.Children.OfType<Wpf.Ui.Controls.TitleBar>().Sum(t => t.ActualHeight);
        root.Measure(new Size(root.ActualWidth, double.PositiveInfinity));
        double needed = root.DesiredSize.Height;
        root.InvalidateMeasure();
        double available = SystemParameters.WorkArea.Height - 20 - Math.Max(0, window.ActualHeight - root.ActualHeight);
        if (needed <= available || needed <= fixedPart)
            return;
        double fitted = Math.Max(Math.Min(1, Scale), scale * (available - fixedPart) / (needed - fixedPart));
        if (fitted >= scale)
            return;
        transform.ScaleX = transform.ScaleY = fitted;
        if (double.IsFinite(size.Width))
            window.Width = Math.Min(size.Width * fitted, SystemParameters.WorkArea.Width - 20);
    }

    /// <summary>A ScrollViewer written in the window (templates, such as a text box's, do not count).</summary>
    private static bool HasScrolling(DependencyObject element) =>
        LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>().Any(c => c is ScrollViewer || HasScrolling(c));

    /// <summary>A window that grew with the setting is moved back inside the screen.</summary>
    private static void KeepOnScreen(Window window)
    {
        if (!window.IsLoaded || window.WindowState != WindowState.Normal)
            return;
        var area = SystemParameters.WorkArea; // the main screen: a window on another screen is left alone
        if (window.Left < area.Left || window.Left >= area.Right || window.Top < area.Top || window.Top >= area.Bottom)
            return;
        if (window.Top + window.ActualHeight > area.Bottom)
            window.Top = Math.Max(area.Top, area.Bottom - window.ActualHeight);
        if (window.Left + window.ActualWidth > area.Right)
            window.Left = Math.Max(area.Left, area.Right - window.ActualWidth);
    }

    private static void ApplyTo(Window window)
    {
        if (window.GetValue(WindowTransformProperty) is not ScaleTransform transform
            || window.GetValue(BaseSizeProperty) is not Size { IsEmpty: false } size)
            return;
        double scale = Scale;
        transform.ScaleX = transform.ScaleY = scale;
        var area = SystemParameters.WorkArea;
        if (double.IsFinite(size.Width))
            window.Width = Math.Min(size.Width * scale, area.Width - 20);
        if (double.IsFinite(size.Height))
            window.MaxHeight = Math.Min(size.Height * scale, area.Height - 20);
        else
            window.MaxHeight = area.Height - 20;
        if (window.IsLoaded)
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FitScreen(window));
    }

    private static IEnumerable<Window> OpenWindows()
    {
        Windows.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var reference in Windows.ToList())
        {
            if (reference.TryGetTarget(out var window))
                yield return window;
        }
    }
}
