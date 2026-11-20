namespace CsvAnalyser.Core.Analysis;

/// <summary>
/// Adjusts the ranges of several Y axes sharing one plot so that their zeros are drawn at the same height.
/// </summary>
public static class ZeroAlignment
{
    /// <param name="ranges">Desired range of each axis (data range plus margins, or the manual range).</param>
    /// <param name="isFixed">
    /// Axes the user set by hand: they are not changed. The first fixed axis whose range contains zero
    /// dictates where the zero goes; otherwise the zero goes as high as the most negative auto axis needs.
    /// </param>
    /// <returns>New ranges; every auto axis still contains its desired range.</returns>
    public static (double Min, double Max)[] Align(IReadOnlyList<(double Min, double Max)> ranges, IReadOnlyList<bool> isFixed)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(isFixed.Count, ranges.Count);
        var result = ranges.ToArray();
        if (ranges.Count < 2)
            return result;

        // f = fraction of the axis height below zero.
        double? f = null;
        for (int i = 0; i < ranges.Count; i++)
        {
            var (min, max) = ranges[i];
            if (isFixed[i] && min < 0 && max > 0)
            {
                f = -min / (max - min);
                break;
            }
        }

        if (f is null)
        {
            double best = 0;
            bool any = false;
            for (int i = 0; i < ranges.Count; i++)
            {
                if (isFixed[i])
                    continue;
                var (negative, positive) = Extents(ranges[i]);
                if (negative + positive <= 0)
                    continue;
                best = Math.Max(best, negative / (negative + positive));
                any = true;
            }
            if (!any)
                return result;
            f = best;
        }

        double fraction = f.Value;
        for (int i = 0; i < ranges.Count; i++)
        {
            if (isFixed[i])
                continue;
            var (negative, positive) = Extents(ranges[i]);
            double height = 0;
            if (fraction < 1)
                height = Math.Max(height, positive / (1 - fraction));
            if (fraction > 0)
                height = Math.Max(height, negative / fraction);
            if (height <= 0 || !double.IsFinite(height))
                height = 1;
            result[i] = (-fraction * height, (1 - fraction) * height);
        }
        return result;
    }

    private static (double Negative, double Positive) Extents((double Min, double Max) range) =>
        (Math.Max(-range.Min, 0), Math.Max(range.Max, 0));
}
