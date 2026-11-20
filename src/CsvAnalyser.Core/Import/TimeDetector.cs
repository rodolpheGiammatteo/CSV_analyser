using CsvAnalyser.Core.Data;

namespace CsvAnalyser.Core.Import;

internal static class TimeDetector
{
    private static readonly string[] TimeWords = ["time", "temps", "timestamp", "horodatage", "epoch", "unix", "date", "heure"];

    /// <summary>
    /// Picks the default X axis. Date and epoch columns are converted in place to seconds relative to their first sample.
    /// Preference: date column, then a column named like a time, then the first increasing numeric column.
    /// </summary>
    public static TimeInfo? Detect(IReadOnlyList<Channel> channels, ColumnKind[] kinds)
    {
        for (int i = 0; i < channels.Count; i++)
        {
            var ch = channels[i];
            if (kinds[i] == ColumnKind.Date && ch.IsSortedAscending)
            {
                double first = ch.Values[0];
                MakeRelative(ch, first, 1);
                return new TimeInfo(ch, TimeKind.DateTime, DateParser.Reference.AddSeconds(first));
            }
        }

        for (int i = 0; i < channels.Count; i++)
        {
            var ch = channels[i];
            if (kinds[i] != ColumnKind.Numeric || !IsTimeName(ch) || !ch.IsSortedAscending)
                continue;

            double first = ch.Values[0];
            bool namedEpoch = ch.DisplayName.Contains("epoch", StringComparison.OrdinalIgnoreCase)
                || ch.DisplayName.Contains("unix", StringComparison.OrdinalIgnoreCase);

            if (first >= 1e11 && first < 1e13)
            {
                MakeRelative(ch, first, 1e-3);
                return new TimeInfo(ch, TimeKind.EpochMilliseconds, DateTimeOffset.FromUnixTimeMilliseconds((long)first).LocalDateTime);
            }
            if ((first >= 1e8 && first < 1e10) || (namedEpoch && first > 0))
            {
                MakeRelative(ch, first, 1);
                return new TimeInfo(ch, TimeKind.EpochSeconds, DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(first * 1000)).LocalDateTime);
            }
            return new TimeInfo(ch, TimeKind.Numeric, null);
        }

        if (channels.Count > 0 && kinds[0] == ColumnKind.Numeric && channels[0].IsSortedAscending && channels[0].Values[^1] > channels[0].Values[0])
            return new TimeInfo(channels[0], TimeKind.Numeric, null);

        return null;
    }

    private static bool IsTimeName(Channel ch)
    {
        string name = ch.DisplayName.Trim().ToLowerInvariant();
        return name == "t" || TimeWords.Any(name.Contains);
    }

    private static void MakeRelative(Channel ch, double origin, double scale)
    {
        var values = ch.Values;
        for (int i = 0; i < values.Length; i++)
            values[i] = (values[i] - origin) * scale;
        ch.Unit = "s";
        ch.InvalidateCache();
    }
}
