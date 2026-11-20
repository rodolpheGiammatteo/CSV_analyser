namespace CsvAnalyser.Core.Analysis;

/// <summary>Binary searches in an ascending array.</summary>
public static class SortedSearch
{
    /// <summary>First index whose value is ≥ <paramref name="value"/> (length if none).</summary>
    public static int LowerBound(double[] sorted, double value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (sorted[mid] < value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    /// <summary>First index whose value is &gt; <paramref name="value"/> (length if none).</summary>
    public static int UpperBound(double[] sorted, double value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (sorted[mid] <= value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    /// <summary>Index of the value closest to <paramref name="value"/>, or -1 for an empty array.</summary>
    public static int Nearest(double[] sorted, double value)
    {
        if (sorted.Length == 0)
            return -1;
        int i = LowerBound(sorted, value);
        if (i >= sorted.Length)
            return sorted.Length - 1;
        if (i > 0 && value - sorted[i - 1] < sorted[i] - value)
            return i - 1;
        return i;
    }
}
