using CsvAnalyser.Core.Analysis;

namespace CsvAnalyser.Tests;

public class RangeStatisticsTests
{
    [Fact]
    public void Basic_MinMaxMeanRms()
    {
        var s = RangeStatistics.Compute([1, -1, 1, -1], 0, 4);
        Assert.Equal(4, s.Count);
        Assert.Equal(-1, s.Min);
        Assert.Equal(1, s.Max);
        Assert.Equal(0, s.Mean, 12);
        Assert.Equal(1, s.Rms, 12);
        Assert.Equal(2, s.PeakToPeak);
    }

    [Fact]
    public void SineRms_IsAmplitudeOverRoot2()
    {
        const int n = 10000;
        var ys = new double[n];
        for (int i = 0; i < n; i++)
            ys[i] = 3 * Math.Sin(2 * Math.PI * 5 * i / n); // whole periods
        var s = RangeStatistics.Compute(ys, 0, n);
        Assert.Equal(3 / Math.Sqrt(2), s.Rms, 6);
        Assert.Equal(0, s.Mean, 9);
    }

    [Fact]
    public void RangeAndNaN_OnlyValidSamplesInsideTheRange()
    {
        double[] ys = [100, 2, double.NaN, 4, 100];
        var s = RangeStatistics.Compute(ys, 1, 4);
        Assert.Equal(2, s.Count);
        Assert.Equal(3, s.Mean);
    }

    [Fact]
    public void Filter_SkipsRejectedRows()
    {
        double[] ys = [1, 2, 3, 4];
        var s = RangeStatistics.Compute(ys, 0, 4, i => i % 2 == 0);
        Assert.Equal(2, s.Mean);
    }

    [Fact]
    public void NoSample_IsEmpty()
    {
        var s = RangeStatistics.Compute([double.NaN], 0, 1);
        Assert.Equal(0, s.Count);
        Assert.True(double.IsNaN(s.Mean));
    }
}
