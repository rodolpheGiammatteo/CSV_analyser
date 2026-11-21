using CsvAnalyser.Core.Analysis;

namespace CsvAnalyser.Tests;

public class AscendingRunsTests
{
    [Fact]
    public void Sorted_IsOneRun()
    {
        var runs = AscendingRuns.Find([0, 1, 1, 2, 3]);
        Assert.Equal([(0, 5)], runs!);
    }

    [Fact]
    public void ClockGoingBack_SplitsWhereItGoesBack()
    {
        var runs = AscendingRuns.Find([0, 1, 2, 3, 1.5, 2.5, 3.5]);
        Assert.Equal([(0, 4), (4, 7)], runs!);
    }

    [Fact]
    public void NaN_EndsARunAndIsSkipped()
    {
        var runs = AscendingRuns.Find([double.NaN, 0, 1, double.NaN, 2, 3, double.NaN]);
        Assert.Equal([(1, 3), (4, 6)], runs!);
    }

    [Fact]
    public void TooManyRuns_IsNull()
    {
        var zigzag = Enumerable.Range(0, 200).Select(i => (double)(i % 2)).ToArray();
        Assert.Null(AscendingRuns.Find(zigzag));
        Assert.Null(AscendingRuns.Find([double.NaN, double.NaN]));
    }

    [Fact]
    public void Stitch_ReplacesTheStepBackByTheUsualStep()
    {
        // 10 Hz, clock resynced 4.6 s back after 0.4 s.
        double[] t = [0, 0.1, 0.2, 0.3, 0.4, -4.2, -4.1, -4.0];
        var s = AscendingRuns.Stitch(t);
        double[] expected = [0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7];
        for (int i = 0; i < t.Length; i++)
            Assert.Equal(expected[i], s[i], 9);
    }

    [Fact]
    public void Stitch_MissingValues_AdvanceOneStepEachWithoutCountingTwice()
    {
        double[] t = [double.NaN, 1, 2, double.NaN, double.NaN, 5, 6];
        var s = AscendingRuns.Stitch(t);
        double[] expected = [0, 1, 2, 3, 4, 5, 6];
        for (int i = 0; i < t.Length; i++)
            Assert.Equal(expected[i], s[i], 9);
    }

    [Fact]
    public void Stitch_SortedTime_IsUnchanged()
    {
        double[] t = [3, 3.5, 4, 7, 7.25];
        Assert.Equal(t, AscendingRuns.Stitch(t));
    }

    [Fact]
    public void MostlyAscending_AllowsAFewStepsBack()
    {
        var once = Enumerable.Range(0, 300).Select(i => i * 0.1 - (i >= 150 ? 4.6 : 0)).ToArray();
        Assert.True(AscendingRuns.IsMostlyAscending(once));

        var shortOnce = new double[] { 0, 1, 2, 1, 2 };
        Assert.False(AscendingRuns.IsMostlyAscending(shortOnce)); // 1 step back in 5 values: not a clock glitch

        var random = new Random(1);
        var unordered = Enumerable.Range(0, 1000).Select(_ => random.NextDouble()).ToArray();
        Assert.False(AscendingRuns.IsMostlyAscending(unordered));

        var falling = Enumerable.Range(0, 300).Select(i => -(double)i).ToArray();
        Assert.False(AscendingRuns.IsMostlyAscending(falling));
    }
}
