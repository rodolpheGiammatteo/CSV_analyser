using System.Windows.Media;

namespace CsvAnalyser.App.ViewModels;

public enum CurveLine
{
    Solid,
    Dashed,
    DenselyDashed,
    Dotted,
    None,
}

public enum CurveMarker
{
    None,
    Circle,
    Square,
    Triangle,
    Diamond,
    Cross,
}

/// <summary>An entry of a style combo box. <see cref="Dash"/> draws the line preview (null = no line).</summary>
public sealed record StyleChoice<T>(T Value, string Label, DoubleCollection? Dash = null);

public static class CurveStyleChoices
{
    public const double DefaultLineWidth = 1.5;
    public const double DefaultMarkerSize = 5;

    public static IReadOnlyList<StyleChoice<CurveLine>> Lines { get; } =
    [
        new(CurveLine.Solid, "Continu", Freeze([])),
        new(CurveLine.Dashed, "Tirets", Freeze([4, 2])),
        new(CurveLine.DenselyDashed, "Tirets courts", Freeze([2, 1.5])),
        new(CurveLine.Dotted, "Pointillés", Freeze([1, 1.5])),
        new(CurveLine.None, "Aucun trait (points seuls)"),
    ];

    public static IReadOnlyList<StyleChoice<CurveMarker>> Markers { get; } =
    [
        new(CurveMarker.None, "Aucun"),
        new(CurveMarker.Circle, "● Rond"),
        new(CurveMarker.Square, "■ Carré"),
        new(CurveMarker.Triangle, "▲ Triangle"),
        new(CurveMarker.Diamond, "◆ Losange"),
        new(CurveMarker.Cross, "✕ Croix"),
    ];

    /// <summary>Colors offered in the curve settings, in addition to the automatic theme color.</summary>
    public static IReadOnlyList<string> Swatches { get; } =
    [
        "#1F77B4", "#FF7F0E", "#2CA02C", "#D62728", "#9467BD", "#8C564B", "#E377C2", "#7F7F7F", "#BCBD22", "#17BECF",
        "#4FC3F7", "#FFB74D", "#81C784", "#E57373", "#BA68C8", "#FFF176", "#00E5FF", "#FF4081", "#000000", "#FFFFFF",
    ];

    private static DoubleCollection Freeze(double[] values)
    {
        var collection = new DoubleCollection(values);
        collection.Freeze();
        return collection;
    }
}
