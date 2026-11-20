namespace CsvAnalyser.Core.Analysis;

/// <summary>Statistics of the samples of one curve between two cursors (missing values ignored).</summary>
public readonly record struct RangeStatistics(int Count, double Min, double Max, double Mean, double Rms)
{
    public double PeakToPeak => Max - Min;

    public static readonly RangeStatistics Empty = new(0, double.NaN, double.NaN, double.NaN, double.NaN);

    /// <summary>Over rows <c>[start, end)</c>, keeping only those accepted by <paramref name="filter"/> when given.</summary>
    public static RangeStatistics Compute(double[] values, int start, int end, Func<int, bool>? filter = null)
    {
        start = Math.Max(0, start);
        end = Math.Min(end, values.Length);
        int count = 0;
        double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0, sumSquares = 0;
        for (int i = start; i < end; i++)
        {
            double v = values[i];
            if (!double.IsFinite(v) || (filter is not null && !filter(i)))
                continue;
            count++;
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
            sumSquares += v * v;
        }
        return count == 0
            ? Empty
            : new RangeStatistics(count, min, max, sum / count, Math.Sqrt(sumSquares / count));
    }
}
