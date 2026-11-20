using System.Globalization;
using System.Text;
namespace CsvAnalyser.Core.Export;

/// <summary>One exported column: its header and one value per row of the dataset.</summary>
public sealed record ExportColumn(string Header, double[] Values);

/// <summary>A first column with the absolute date and time of each row: <c>Origin + Seconds[row]</c>.</summary>
public sealed record ExportDateColumn(string Header, DateTime Origin, double[] Seconds);

/// <summary>A text column written after the numbers (e.g. the annotation zone of each row); null or "" = empty field.</summary>
public sealed record ExportTextColumn(string Header, Func<int, string?> Value);

/// <summary>The rows to write: <c>[Start, End)</c>, optionally filtered (unsorted X axis).</summary>
public sealed record ExportRows(int Start, int End, Func<int, bool>? Filter = null)
{
    public static ExportRows All(int rowCount) => new(0, rowCount);
}

public sealed record CsvExportOptions
{
    public char Delimiter { get; init; } = ';';

    public char DecimalSeparator { get; init; } = ',';

    public ExportDateColumn? DateColumn { get; init; }

    public IReadOnlyList<ExportTextColumn> TextColumns { get; init; } = [];
}

/// <summary>Writes columns of numbers as CSV (empty field for a missing value, quoted headers when needed).</summary>
public static class CsvExporter
{
    public static void Write(
        TextWriter writer,
        IReadOnlyList<ExportColumn> columns,
        ExportRows rows,
        CsvExportOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (options.Delimiter == options.DecimalSeparator)
            throw new ArgumentException(L.T("Le séparateur de colonnes et la décimale doivent être différents."));

        var number = new NumberFormatInfo
        {
            NumberDecimalSeparator = options.DecimalSeparator.ToString(),
            NegativeSign = "-",
        };
        char delimiter = options.Delimiter;
        var date = options.DateColumn;

        // Header
        var header = new List<string>();
        if (date is not null)
            header.Add(date.Header);
        header.AddRange(columns.Select(c => c.Header));
        header.AddRange(options.TextColumns.Select(c => c.Header));
        writer.Write(string.Join(delimiter, header.Select(h => Quote(h, delimiter))));
        writer.Write("\r\n");

        var line = new StringBuilder(columns.Count * 16);
        Span<char> buffer = stackalloc char[64];
        int start = Math.Max(0, rows.Start);
        int end = rows.End;
        int total = Math.Max(1, end - start);
        int nextReport = start;

        for (int row = start; row < end; row++)
        {
            if (rows.Filter is { } filter && !filter(row))
                continue;

            line.Clear();
            bool first = true;
            if (date is not null)
            {
                double s = date.Seconds[row];
                if (double.IsFinite(s))
                    line.Append(date.Origin.AddSeconds(s).ToString("dd/MM/yyyy HH:mm:ss.fff", CultureInfo.InvariantCulture));
                first = false;
            }
            foreach (var column in columns)
            {
                if (!first)
                    line.Append(delimiter);
                first = false;
                double v = row < column.Values.Length ? column.Values[row] : double.NaN;
                if (double.IsFinite(v) && v.TryFormat(buffer, out int written, "R", number))
                    line.Append(buffer[..written]);
            }
            foreach (var text in options.TextColumns)
            {
                if (!first)
                    line.Append(delimiter);
                first = false;
                if (text.Value(row) is { Length: > 0 } value)
                    line.Append(Quote(value, delimiter));
            }
            line.Append("\r\n");
            writer.Write(line);

            if (row >= nextReport)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((double)(row - start) / total);
                nextReport = row + 65536;
            }
        }
        progress?.Report(1);
    }

    /// <summary>Quotes a header or text that contains the delimiter, a quote or a line break.</summary>
    private static string Quote(string text, char delimiter) =>
        text.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
}
