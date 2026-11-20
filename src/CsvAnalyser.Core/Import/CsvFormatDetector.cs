using System.Text;
using System.Text.RegularExpressions;

namespace CsvAnalyser.Core.Import;

public static partial class CsvFormatDetector
{
    private const int SampleLineCount = 400;
    private const int SampleByteLimit = 4 << 20;
    private static readonly char[] DelimiterCandidates = [';', '\t', ',', '|'];
    private static readonly string[] EmptyUnits = ["", "na", "n/a", "null", "-"];

    static CsvFormatDetector()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static CsvFormat Detect(string path)
    {
        var encoding = DetectEncoding(path);
        var lines = ReadSample(path, encoding);
        return Detect(lines, encoding);
    }

    public static CsvFormat Detect(IReadOnlyList<string> lines, Encoding encoding)
    {
        var nonEmpty = Enumerable.Range(0, lines.Count).Where(i => !string.IsNullOrWhiteSpace(lines[i])).ToList();
        if (nonEmpty.Count == 0)
            throw new InvalidDataException("Le fichier est vide.");

        var (delimiter, fieldCount) = DetectDelimiter(lines, nonEmpty);
        char decimalSeparator = DetectDecimalSeparator(lines, nonEmpty, delimiter, fieldCount);
        var dot = new NumberParser('.');
        var comma = new NumberParser(',');
        var numbers = decimalSeparator == ',' ? comma : dot;

        int firstStructured = nonEmpty.First(i => FieldSplitter.Count(lines[i], delimiter) == fieldCount);
        var firstFields = FieldSplitter.SplitToStrings(lines[firstStructured], delimiter);

        int headerLine = -1;
        int unitLine = -1;
        int firstDataLine = firstStructured;
        string[] headers;
        string[]? unitRow = null;

        if (LooksLikeTextRow(firstFields, numbers))
        {
            headerLine = firstStructured;
            headers = Pad(firstFields, fieldCount);
            firstDataLine = NextNonEmpty(nonEmpty, headerLine);

            if (firstDataLine >= 0)
            {
                var candidate = FieldSplitter.SplitToStrings(lines[firstDataLine], delimiter);
                if (candidate.Length == fieldCount && IsUnitRow(candidate, numbers))
                {
                    unitLine = firstDataLine;
                    unitRow = candidate;
                    firstDataLine = NextNonEmpty(nonEmpty, unitLine);
                }
            }
        }
        else
        {
            headers = Enumerable.Range(1, fieldCount).Select(i => $"Colonne {i}").ToArray();
        }

        if (firstDataLine < 0)
            firstDataLine = lines.Count;

        var samples = CollectSamples(lines, nonEmpty, firstDataLine, delimiter, fieldCount);
        var kinds = new ColumnKind[fieldCount];
        var dateParsers = new DateParser?[fieldCount];
        for (int c = 0; c < fieldCount; c++)
            (kinds[c], dateParsers[c]) = ClassifyColumn(headers[c], samples[c], numbers);

        var displayNames = new string[fieldCount];
        var units = new string[fieldCount];
        for (int c = 0; c < fieldCount; c++)
        {
            var (name, headerUnit) = SplitNameAndUnit(headers[c]);
            displayNames[c] = string.IsNullOrWhiteSpace(name) ? $"Colonne {c + 1}" : name;
            units[c] = NormalizeUnit(unitRow is not null ? unitRow[c] : headerUnit);
            if (string.IsNullOrWhiteSpace(headers[c]))
                headers[c] = displayNames[c];
        }
        MakeUnique(headers);

        return new CsvFormat
        {
            Encoding = encoding,
            Delimiter = delimiter,
            DecimalSeparator = decimalSeparator,
            HeaderLineIndex = headerLine,
            UnitLineIndex = unitLine,
            FirstDataLineIndex = firstDataLine,
            Headers = headers,
            Units = units,
            DisplayNames = displayNames,
            Kinds = kinds,
            DateParsers = dateParsers,
        };
    }

    public static Encoding DetectEncoding(string path)
    {
        var buffer = new byte[64 * 1024];
        int read;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            read = fs.Read(buffer, 0, buffer.Length);

        var span = buffer.AsSpan(0, read);
        if (span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        if (span.StartsWith(new byte[] { 0xFF, 0xFE }))
            return Encoding.Unicode;
        if (span.StartsWith(new byte[] { 0xFE, 0xFF }))
            return Encoding.BigEndianUnicode;

        // A multi-byte UTF-8 sequence may be cut at the end of the buffer: ignore the last few bytes.
        var probe = span[..Math.Max(0, read - 4)];
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(probe);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252);
        }
    }

    private static List<string> ReadSample(string path, Encoding encoding)
    {
        var lines = new List<string>(SampleLineCount);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true);
        long chars = 0;
        while (lines.Count < SampleLineCount && chars < SampleByteLimit && reader.ReadLine() is { } line)
        {
            lines.Add(line);
            chars += line.Length;
        }
        return lines;
    }

    private static (char Delimiter, int FieldCount) DetectDelimiter(IReadOnlyList<string> lines, List<int> nonEmpty)
    {
        // The end of the sample is the most reliable part: files may start with a free-form preamble.
        var dataRegion = nonEmpty.Skip(nonEmpty.Count / 3).ToList();
        if (dataRegion.Count == 0)
            dataRegion = nonEmpty;

        var dot = new NumberParser('.');
        var comma = new NumberParser(',');
        char best = ';';
        int bestCount = 1;
        double bestScore = double.MinValue;

        foreach (char d in DelimiterCandidates)
        {
            var counts = dataRegion.Select(i => FieldSplitter.Count(lines[i], d)).ToList();
            int mode = counts.GroupBy(c => c).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
            if (mode < 2)
                continue;

            double consistency = counts.Count(c => c == mode) / (double)counts.Count;
            int numericFields = 0, totalFields = 0;
            foreach (int i in dataRegion.Take(50))
            {
                foreach (var field in FieldSplitter.SplitToStrings(lines[i], d))
                {
                    if (field.Length == 0)
                        continue;
                    totalFields++;
                    bool isNumber = dot.TryParse(field, out _) || (d != ',' && comma.TryParse(field, out _));
                    if (isNumber)
                        numericFields++;
                }
            }
            double numericRatio = totalFields == 0 ? 0 : numericFields / (double)totalFields;
            double score = consistency * (0.5 + numericRatio);
            if (score > bestScore + 1e-9)
            {
                bestScore = score;
                best = d;
                bestCount = mode;
            }
        }
        return (best, bestCount);
    }

    private static char DetectDecimalSeparator(IReadOnlyList<string> lines, List<int> nonEmpty, char delimiter, int fieldCount)
    {
        if (delimiter == ',')
            return '.';

        int commaVotes = 0, dotVotes = 0;
        foreach (int i in nonEmpty.TakeLast(100))
        {
            foreach (var field in FieldSplitter.SplitToStrings(lines[i], delimiter, fieldCount))
            {
                if (CommaDecimal().IsMatch(field)) commaVotes++;
                else if (DotDecimal().IsMatch(field)) dotVotes++;
            }
        }
        return commaVotes > dotVotes ? ',' : '.';
    }

    private static bool LooksLikeTextRow(string[] fields, NumberParser numbers)
    {
        int nonEmpty = fields.Count(f => f.Length > 0);
        if (nonEmpty == 0)
            return false;
        int text = fields.Count(f => f.Length > 0 && !numbers.TryParse(f, out _));
        return text * 2 >= nonEmpty;
    }

    private static bool IsUnitRow(string[] fields, NumberParser numbers)
    {
        if (fields.Any(f => f.Length > 0 && numbers.TryParse(f, out _)))
            return false;
        var nonEmpty = fields.Where(f => f.Length > 0).ToList();
        return nonEmpty.Count > 0 && DateParser.TryCreate(nonEmpty) is null;
    }

    private static List<string>[] CollectSamples(IReadOnlyList<string> lines, List<int> nonEmpty, int firstDataLine, char delimiter, int fieldCount)
    {
        var samples = Enumerable.Range(0, fieldCount).Select(_ => new List<string>()).ToArray();
        foreach (int i in nonEmpty.Where(i => i >= firstDataLine).Take(200))
        {
            var fields = FieldSplitter.SplitToStrings(lines[i], delimiter, fieldCount);
            for (int c = 0; c < fieldCount; c++)
                samples[c].Add(c < fields.Length ? fields[c] : "");
        }
        return samples;
    }

    private static (ColumnKind, DateParser?) ClassifyColumn(string header, List<string> samples, NumberParser numbers)
    {
        var nonEmpty = samples.Where(s => s.Length > 0).ToList();
        if (nonEmpty.Count == 0)
            return (string.IsNullOrWhiteSpace(header) ? ColumnKind.Empty : ColumnKind.Numeric, null);

        int numeric = nonEmpty.Count(s => numbers.TryParse(s, out _));
        if (numeric * 2 >= nonEmpty.Count)
            return (ColumnKind.Numeric, null);

        var dateParser = DateParser.TryCreate(nonEmpty);
        return dateParser is not null ? (ColumnKind.Date, dateParser) : (ColumnKind.Text, null);
    }

    /// <summary>"Vitesse [km/h]" → ("Vitesse", "km/h").</summary>
    public static (string Name, string Unit) SplitNameAndUnit(string header)
    {
        var match = UnitInBrackets().Match(header);
        return match.Success
            ? (match.Groups["name"].Value.Trim(), match.Groups["unit"].Value.Trim())
            : (header.Trim(), "");
    }

    private static string NormalizeUnit(string unit)
    {
        unit = unit.Trim();
        return EmptyUnits.Contains(unit.ToLowerInvariant()) ? "" : unit;
    }

    private static string[] Pad(string[] fields, int count)
    {
        var result = new string[count];
        for (int i = 0; i < count; i++)
            result[i] = i < fields.Length ? fields[i] : "";
        return result;
    }

    private static int NextNonEmpty(List<int> nonEmpty, int after)
    {
        foreach (int i in nonEmpty)
            if (i > after)
                return i;
        return -1;
    }

    private static void MakeUnique(string[] names)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++)
        {
            if (seen.TryGetValue(names[i], out int n))
            {
                seen[names[i]] = n + 1;
                names[i] = $"{names[i]} ({n + 1})";
            }
            else
            {
                seen[names[i]] = 1;
            }
        }
    }

    [GeneratedRegex(@"^[+-]?\d+,\d+([eE][+-]?\d+)?$")]
    private static partial Regex CommaDecimal();

    [GeneratedRegex(@"^[+-]?\d+\.\d+([eE][+-]?\d+)?$")]
    private static partial Regex DotDecimal();

    [GeneratedRegex(@"^(?<name>.*?)\s*\[(?<unit>[^\[\]]*)\]\s*$")]
    private static partial Regex UnitInBrackets();
}
