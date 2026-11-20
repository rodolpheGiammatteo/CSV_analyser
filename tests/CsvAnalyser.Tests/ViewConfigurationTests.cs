using CsvAnalyser.Core.Configuration;

namespace CsvAnalyser.Tests;

public class ViewConfigurationTests
{
    private static ViewConfiguration Sample() => new()
    {
        SourceFile = "essai.csv",
        XAxis = "Temps",
        XRange = new RangeConfig(0, 10),
        Graphs =
        [
            new GraphConfig
            {
                Title = "Moteur", AlignZeros = true, Legend = "LowerLeft", LegendOffsetX = 12, LegendOffsetY = 4,
                Axes = [new AxisConfig { Key = "unit:A", Manual = true, Min = -5, Max = 5 }],
            },
        ],
        Curves =
        [
            new CurveConfig { Column = "Courant", Name = "I moteur", Unit = "A", Plotted = true, Graph = 0, Axis = "", Color = "#FF0000", Line = "Dashed", LineWidth = 2 },
            new CurveConfig { Column = "Température", Plotted = false },
        ],
        Measure = new MeasureConfig { Enabled = true, A = 1.5, B = 4 },
    };

    [Fact]
    public void RoundTrip_KeepsEverything()
    {
        var json = Sample().ToJson();
        var back = ViewConfiguration.FromJson(json);

        Assert.Equal("essai.csv", back.SourceFile);
        Assert.Equal("Temps", back.XAxis);
        Assert.Equal(10, back.XRange!.Max);
        var graph = Assert.Single(back.Graphs);
        Assert.Equal("Moteur", graph.Title);
        Assert.True(graph.AlignZeros);
        Assert.Equal("LowerLeft", graph.Legend);
        Assert.Equal(12, graph.LegendOffsetX);
        var axis = Assert.Single(graph.Axes);
        Assert.True(axis.Manual);
        Assert.Equal(-5, axis.Min);
        Assert.Equal(2, back.Curves.Count);
        Assert.Equal("I moteur", back.Curves[0].Name);
        Assert.Equal("#FF0000", back.Curves[0].Color);
        Assert.Equal("Dashed", back.Curves[0].Line);
        Assert.Null(back.Curves[1].Color);
        Assert.True(back.Measure!.Enabled);
        Assert.Equal(4, back.Measure.B);
    }

    [Fact]
    public void Json_IsReadable_AccentsNotEscaped()
    {
        var json = Sample().ToJson();
        Assert.Contains("\"Température\"", json);
        Assert.Contains("\"curves\"", json);
    }

    [Fact]
    public void MissingColumns_ListsColumnsAbsentFromTheFile()
    {
        var missing = Sample().MissingColumns(["Temps", "Courant"]);
        Assert.Equal(["Température"], missing);
    }

    [Fact]
    public void NotAConfiguration_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ViewConfiguration.FromJson("{\"format\":\"autre\"}"));
        Assert.Throws<InvalidDataException>(() => ViewConfiguration.FromJson("pas du json"));
    }

    [Fact]
    public void NewerVersion_Throws()
    {
        Assert.Throws<InvalidDataException>(() =>
            ViewConfiguration.FromJson($"{{\"format\":\"{ViewConfiguration.FormatName}\",\"version\":99}}"));
    }

    [Fact]
    public void Annotations_RoundTrip_AndOmittedWhenNone()
    {
        var config = Sample();
        Assert.DoesNotContain("annotations", config.ToJson());

        config.Annotations =
        [
            new AnnotationConfig { Kind = AnnotationConfig.Zone, Text = "Essai pleine charge", Row = 120, EndRow = 410, Color = "#1D9E75", Opacity = 0.2 },
            new AnnotationConfig { Kind = AnnotationConfig.Note, Text = "Pic", Row = 215, Curve = "Courant", FontSize = 14 },
            new AnnotationConfig { Kind = AnnotationConfig.Marker, Text = "Arrêt", Row = 585, Graph = 0, Line = "Dotted", LineWidth = 2, Hidden = true },
        ];
        var back = ViewConfiguration.FromJson(config.ToJson());

        Assert.Equal(3, back.Annotations!.Count);
        var zone = back.Annotations[0];
        Assert.Equal((AnnotationConfig.Zone, "Essai pleine charge", 120, 410, 0.2), (zone.Kind, zone.Text, zone.Row, zone.EndRow!.Value, zone.Opacity!.Value));
        Assert.Equal(("Courant", 14.0), (back.Annotations[1].Curve, back.Annotations[1].FontSize!.Value));
        var marker = back.Annotations[2];
        Assert.Equal((0, "Dotted", 2.0, true), (marker.Graph!.Value, marker.Line, marker.LineWidth!.Value, marker.Hidden));
    }
}
