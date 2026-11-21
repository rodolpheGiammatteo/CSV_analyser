namespace CsvAnalyser.Core.Analysis;

/// <summary>
/// Splits an X column into ascending runs: a time column whose clock went back once or twice (a resync during the
/// recording) is still drawn as lines, one per run, instead of a cloud of points.
/// </summary>
public static class AscendingRuns
{
    /// <summary>At most this many runs: beyond, X is not a time but an unordered value (drawn as points).</summary>
    public const int MaxRuns = 64;

    /// <summary>
    /// The runs <c>[Start, End)</c> of ascending finite values, in row order; a NaN ends a run and is skipped.
    /// Null when there are more than <paramref name="maxRuns"/> runs or no finite value.
    /// </summary>
    public static IReadOnlyList<(int Start, int End)>? Find(double[] xs, int maxRuns = MaxRuns)
    {
        var runs = new List<(int Start, int End)>();
        int start = -1;
        double previous = double.NaN;
        for (int i = 0; i < xs.Length; i++)
        {
            double v = xs[i];
            if (!double.IsFinite(v))
            {
                if (start >= 0)
                    runs.Add((start, i));
                start = -1;
                continue;
            }
            if (start >= 0 && v < previous)
            {
                runs.Add((start, i));
                start = -1;
            }
            if (start < 0)
            {
                start = i;
                if (runs.Count >= maxRuns)
                    return null;
            }
            previous = v;
        }
        if (start >= 0)
            runs.Add((start, xs.Length));
        return runs.Count > 0 ? runs : null;
    }

    /// <summary>
    /// A time column that goes back a few times (at most <paramref name="maxBackSteps"/>) but mostly increases:
    /// at least 99 % of its finite values are in order and it ends later than it starts.
    /// </summary>
    public static bool IsMostlyAscending(double[] xs, int maxBackSteps = 10)
    {
        int back = 0, finite = 0;
        double first = double.NaN, previous = double.NaN;
        foreach (double v in xs)
        {
            if (!double.IsFinite(v))
                continue;
            if (finite == 0)
                first = v;
            else if (v < previous && ++back > maxBackSteps)
                return false;
            previous = v;
            finite++;
        }
        return finite > 1 && previous > first && back <= finite / 100;
    }
}
