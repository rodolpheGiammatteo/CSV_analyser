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
    /// A strictly usable time base for the filters, from a time that goes back now and then: each step back (or
    /// missing value) is replaced by the usual sampling step (median of the forward steps), every other step is kept.
    /// The result never decreases and has no NaN; durations inside each ascending run are unchanged.
    /// </summary>
    public static double[] Stitch(double[] t)
    {
        var result = new double[t.Length];
        if (t.Length == 0)
            return result;

        var forward = new List<double>();
        double last = double.NaN;
        foreach (double v in t)
        {
            if (!double.IsFinite(v))
                continue;
            if (double.IsFinite(last) && v > last)
                forward.Add(v - last);
            last = v;
            if (forward.Count >= 10_000)
                break;
        }
        forward.Sort();
        double step = forward.Count > 0 ? forward[forward.Count / 2] : 1;

        // anchor = stitched value of the last finite sample; missing samples in between advance by one step each.
        double previousRaw = double.NaN, anchor = 0;
        int missing = 0;
        bool started = false;
        for (int i = 0; i < t.Length; i++)
        {
            double v = t[i];
            if (!double.IsFinite(v))
            {
                if (started)
                    missing++;
                result[i] = anchor + step * missing;
                continue;
            }
            double value;
            if (!started)
                value = v;
            else if (double.IsFinite(previousRaw) && v >= previousRaw)
                value = Math.Max(anchor + (v - previousRaw), anchor + step * missing);
            else
                value = anchor + step * (missing + 1);
            if (!started)
            {
                // Leading missing samples: placed before the first time, one step apart.
                for (int k = 0; k < i; k++)
                    result[k] = v - step * (i - k);
                started = true;
            }
            result[i] = anchor = value;
            previousRaw = v;
            missing = 0;
        }
        return result;
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
