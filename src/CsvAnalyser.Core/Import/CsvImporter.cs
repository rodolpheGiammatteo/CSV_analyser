using System.Diagnostics;
using CsvAnalyser.Core.Data;

namespace CsvAnalyser.Core.Import;

public static class CsvImporter
{
    public const int DefaultBatchSize = 32_768;
    private const int ParallelChunk = 2_048;

    public static Dataset Load(string path, IProgress<double>? progress = null, CancellationToken ct = default, int batchSize = DefaultBatchSize)
    {
        var stopwatch = Stopwatch.StartNew();
        var format = CsvFormatDetector.Detect(path);
        var columns = Enumerable.Range(0, format.ColumnCount)
            .Where(c => format.Kinds[c] is ColumnKind.Numeric or ColumnKind.Date)
            .ToArray();

        var batches = new List<double[][]>();
        var batchLengths = new List<int>();
        var numbers = new NumberParser(format.DecimalSeparator);

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan))
        using (var reader = new StreamReader(stream, format.Encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 20))
        {
            for (int i = 0; i < format.FirstDataLineIndex; i++)
            {
                if (reader.ReadLine() is null)
                    break;
            }

            double length = Math.Max(1, stream.Length);
            Task<double[][]>? pending = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var lines = ReadBatch(reader, batchSize);
                if (pending is not null)
                {
                    batches.Add(pending.GetAwaiter().GetResult());
                    pending = null;
                }
                if (lines.Length == 0)
                    break;

                batchLengths.Add(lines.Length);
                pending = Task.Run(() => ParseBatch(lines, format, columns, numbers, ct), ct);
                progress?.Report(stream.Position / length);
            }
        }

        int rowCount = batchLengths.Sum();
        var channels = new List<Channel>(columns.Length);
        for (int k = 0; k < columns.Length; k++)
        {
            var values = new double[rowCount];
            int offset = 0;
            for (int b = 0; b < batches.Count; b++)
            {
                Array.Copy(batches[b][k], 0, values, offset, batchLengths[b]);
                offset += batchLengths[b];
                batches[b][k] = null!;
            }

            int c = columns[k];
            channels.Add(new Channel(format.Headers[c], values)
            {
                DisplayName = format.DisplayNames[c],
                Unit = format.Units[c],
            });
        }

        var time = TimeDetector.Detect(channels, columns.Select(c => format.Kinds[c]).ToArray());
        progress?.Report(1);

        return new Dataset
        {
            FilePath = path,
            Format = format,
            Channels = channels,
            RowCount = rowCount,
            Time = time,
            IgnoredColumns = Enumerable.Range(0, format.ColumnCount)
                .Where(c => format.Kinds[c] == ColumnKind.Text)
                .Select(c => format.Headers[c])
                .ToList(),
            LoadDuration = stopwatch.Elapsed,
        };
    }

    private static string[] ReadBatch(StreamReader reader, int batchSize)
    {
        var lines = new List<string>(batchSize);
        while (lines.Count < batchSize && reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                lines.Add(line);
        }
        return lines.ToArray();
    }

    private static double[][] ParseBatch(string[] lines, CsvFormat format, int[] columns, NumberParser numbers, CancellationToken ct)
    {
        int n = lines.Length;
        var result = new double[columns.Length][];
        for (int k = 0; k < columns.Length; k++)
            result[k] = new double[n];

        int lastColumn = columns.Length == 0 ? 0 : columns[^1] + 1;
        int chunks = (n + ParallelChunk - 1) / ParallelChunk;
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct }, chunk =>
        {
            var ranges = new Range[Math.Max(1, lastColumn)];
            int end = Math.Min(n, (chunk + 1) * ParallelChunk);
            for (int i = chunk * ParallelChunk; i < end; i++)
            {
                ReadOnlySpan<char> line = lines[i];
                int count = FieldSplitter.Split(line, format.Delimiter, ranges);
                for (int k = 0; k < columns.Length; k++)
                {
                    int c = columns[k];
                    if (c >= count)
                    {
                        result[k][i] = double.NaN;
                        continue;
                    }
                    var field = line[ranges[c]];
                    result[k][i] = format.DateParsers[c] is { } dates ? dates.Parse(field) : numbers.Parse(field);
                }
            }
        });
        return result;
    }
}
