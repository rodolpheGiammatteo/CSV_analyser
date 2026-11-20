namespace CsvAnalyser.Core.Import;

internal static class FieldSplitter
{
    /// <summary>
    /// Splits a CSV line into at most <paramref name="ranges"/>.Length fields without allocating.
    /// Quoted fields are supported (quotes excluded from the range, doubled quotes left in place).
    /// </summary>
    public static int Split(ReadOnlySpan<char> line, char delimiter, Span<Range> ranges)
    {
        int count = 0;
        int pos = 0;
        while (count < ranges.Length)
        {
            if (pos < line.Length && line[pos] == '"')
            {
                int contentStart = pos + 1;
                int p = contentStart;
                while (p < line.Length)
                {
                    if (line[p] == '"')
                    {
                        if (p + 1 < line.Length && line[p + 1] == '"')
                        {
                            p += 2;
                            continue;
                        }
                        break;
                    }
                    p++;
                }
                ranges[count++] = new Range(contentStart, p);
                if (p >= line.Length)
                    return count;

                int afterQuote = line[p..].IndexOf(delimiter);
                if (afterQuote < 0)
                    return count;
                pos = p + afterQuote + 1;
                continue;
            }

            int rel = line[pos..].IndexOf(delimiter);
            if (rel < 0)
            {
                ranges[count++] = new Range(pos, line.Length);
                return count;
            }
            ranges[count++] = new Range(pos, pos + rel);
            pos += rel + 1;
        }
        return count;
    }

    public static int Count(ReadOnlySpan<char> line, char delimiter)
    {
        Span<Range> ranges = stackalloc Range[1024];
        return Split(line, delimiter, ranges);
    }

    public static string[] SplitToStrings(string line, char delimiter, int maxFields = 4096)
    {
        var ranges = new Range[maxFields];
        int count = Split(line, delimiter, ranges);
        var result = new string[count];
        for (int i = 0; i < count; i++)
            result[i] = line[ranges[i]].Replace("\"\"", "\"").Trim();
        return result;
    }
}
