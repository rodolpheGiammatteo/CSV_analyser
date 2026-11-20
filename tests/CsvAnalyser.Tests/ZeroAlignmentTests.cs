using CsvAnalyser.Core.Analysis;

namespace CsvAnalyser.Tests;

public class ZeroAlignmentTests
{
    private static double ZeroFraction((double Min, double Max) r) => -r.Min / (r.Max - r.Min);

    private static void AssertContains((double Min, double Max) outer, (double Min, double Max) inner)
    {
        Assert.True(outer.Min <= inner.Min + 1e-9, $"{outer} should contain {inner}");
        Assert.True(outer.Max >= inner.Max - 1e-9, $"{outer} should contain {inner}");
    }

    [Fact]
    public void TwoAutoAxes_ZerosAtSameHeight_DataStillVisible()
    {
        (double, double)[] input = [(-10, 30), (-1, 9)];
        var result = ZeroAlignment.Align(input, [false, false]);

        Assert.Equal(ZeroFraction(result[0]), ZeroFraction(result[1]), 9);
        AssertContains(result[0], input[0]);
        AssertContains(result[1], input[1]);
        // The most negative axis (25 % below zero) sets the zero height and keeps its own range.
        Assert.Equal(0.25, ZeroFraction(result[0]), 9);
        Assert.Equal((-10.0, 30.0), result[0]);
    }

    [Fact]
    public void AllPositive_EveryAxisStartsAtZero()
    {
        var result = ZeroAlignment.Align([(5, 10), (100, 200)], [false, false]);

        Assert.Equal((0.0, 10.0), result[0]);
        Assert.Equal((0.0, 200.0), result[1]);
    }

    [Fact]
    public void AllNegative_EveryAxisEndsAtZero()
    {
        var result = ZeroAlignment.Align([(-10, -5), (-4, -1)], [false, false]);

        Assert.Equal((-10.0, 0.0), result[0]);
        Assert.Equal((-4.0, 0.0), result[1]);
    }

    [Fact]
    public void FixedAxis_IsKept_AndDictatesZeroHeight()
    {
        (double, double)[] input = [(-50, 50), (2, 8)];
        var result = ZeroAlignment.Align(input, [true, false]);

        Assert.Equal((-50.0, 50.0), result[0]);
        Assert.Equal(0.5, ZeroFraction(result[1]), 9);
        AssertContains(result[1], input[1]);
    }

    [Fact]
    public void FixedAxisWithoutZero_IsIgnoredForTheHeight()
    {
        var result = ZeroAlignment.Align([(10, 20), (-1, 3), (-3, 1)], [true, false, false]);

        Assert.Equal((10.0, 20.0), result[0]);
        Assert.Equal(ZeroFraction(result[1]), ZeroFraction(result[2]), 9);
        Assert.Equal(0.75, ZeroFraction(result[2]), 9);
    }

    [Fact]
    public void SingleAxis_Unchanged()
    {
        var result = ZeroAlignment.Align([(3, 7)], [false]);
        Assert.Equal((3.0, 7.0), result[0]);
    }
}
