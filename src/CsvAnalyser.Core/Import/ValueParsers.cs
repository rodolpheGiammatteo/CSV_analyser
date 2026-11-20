using System.Globalization;

namespace CsvAnalyser.Core.Import;

public sealed class NumberParser
{
    private readonly NumberFormatInfo _format;

    public NumberParser(char decimalSeparator)
    {
        DecimalSeparator = decimalSeparator;
        _format = new NumberFormatInfo
        {
            NumberDecimalSeparator = decimalSeparator.ToString(),
            NumberGroupSeparator = " ",
            PositiveInfinitySymbol = "∞",
            NegativeInfinitySymbol = "-∞",
        };
    }

    public char DecimalSeparator { get; }

    public bool TryParse(ReadOnlySpan<char> text, out double value) =>
        double.TryParse(text, NumberStyles.Float, _format, out value);

    /// <summary>Returns NaN for empty, invalid or infinite values so they appear as gaps in plots.</summary>
    public double Parse(ReadOnlySpan<char> text) =>
        TryParse(text, out double v) && double.IsFinite(v) ? v : double.NaN;
}

/// <summary>Parses date/time text into seconds since <see cref="Reference"/> (kept small so a double keeps sub-microsecond precision).</summary>
public sealed class DateParser
{
    public static readonly DateTime Reference = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    private static readonly (CultureInfo Culture, bool CommaToDot)[] Candidates =
    [
        (CultureInfo.GetCultureInfo("fr-FR"), false),
        (CultureInfo.InvariantCulture, false),
        (CultureInfo.GetCultureInfo("fr-FR"), true),
        (CultureInfo.InvariantCulture, true),
        (CultureInfo.GetCultureInfo("en-US"), false),
    ];

    private readonly CultureInfo _culture;
    private readonly bool _commaToDot;

    private DateParser(CultureInfo culture, bool commaToDot)
    {
        _culture = culture;
        _commaToDot = commaToDot;
    }

    /// <summary>Returns a parser able to read every non-empty sample, or null if the samples are not dates.</summary>
    public static DateParser? TryCreate(IReadOnlyCollection<string> samples)
    {
        var nonEmpty = samples.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (nonEmpty.Count == 0)
            return null;

        foreach (var (culture, commaToDot) in Candidates)
        {
            var parser = new DateParser(culture, commaToDot);
            if (nonEmpty.All(s => !double.IsNaN(parser.Parse(s))))
                return parser;
        }
        return null;
    }

    public double Parse(ReadOnlySpan<char> text)
    {
        if (text.IsWhiteSpace())
            return double.NaN;

        DateTime dt;
        bool ok;
        if (_commaToDot && text.Contains(','))
        {
            Span<char> buffer = text.Length <= 128 ? stackalloc char[text.Length] : new char[text.Length];
            text.CopyTo(buffer);
            buffer.Replace(',', '.');
            ok = DateTime.TryParse(buffer, _culture, DateTimeStyles.AllowWhiteSpaces, out dt);
        }
        else
        {
            ok = DateTime.TryParse(text, _culture, DateTimeStyles.AllowWhiteSpaces, out dt);
        }

        return ok ? (dt - Reference).TotalSeconds : double.NaN;
    }
}
