using System.Windows.Media;

namespace CsvAnalyser.App.ViewModels;

/// <summary>A Y axis as shown in the axes window.</summary>
public sealed class AxisRowViewModel(AxisSpec spec)
{
    public string Label => string.IsNullOrEmpty(spec.Label) ? "(sans unité)" : spec.Label;

    public string Curves => string.Join(", ", spec.Curves.Select(c => c.Name));

    public string Side => spec.IsRight ? "droite" : "gauche";

    /// <summary>"à gauche — courbe A, courbe B".</summary>
    public string Description => $"à {Side} — {Curves}";

    public Brush Brush { get; } = MakeBrush(spec.Color);

    public AxisRangeSettings Settings => spec.Settings;

    private static Brush MakeBrush(ScottPlot.Color? color)
    {
        if (color is not { } c)
            return Brushes.Transparent;
        var brush = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }
}

/// <summary>A graph and its Y axes, as shown in the axes window.</summary>
public sealed class GraphAxesViewModel(GraphViewModel graph, IReadOnlyList<AxisRowViewModel> axes)
{
    public GraphViewModel Graph { get; } = graph;

    public IReadOnlyList<AxisRowViewModel> Axes { get; } = axes;

    public bool CanAlignZeros => Axes.Count > 1;

    public bool IsEmpty => Axes.Count == 0;
}
