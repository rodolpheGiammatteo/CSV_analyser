using System.Text;

namespace CsvAnalyser.Core.Import;

public enum ColumnKind
{
    Numeric,
    Date,
    Text,
    /// <summary>No header and no value (typically created by a trailing delimiter).</summary>
    Empty,
}

public sealed class CsvFormat
{
    public required Encoding Encoding { get; init; }

    public required char Delimiter { get; init; }

    public required char DecimalSeparator { get; init; }

    /// <summary>Raw line index of the header, or -1 when the file has none.</summary>
    public required int HeaderLineIndex { get; init; }

    /// <summary>Raw line index of a units row placed under the header, or -1.</summary>
    public required int UnitLineIndex { get; init; }

    public required int FirstDataLineIndex { get; init; }

    public required string[] Headers { get; init; }

    /// <summary>Unit per column (from a units row or from "[unit]" in the header), empty when unknown.</summary>
    public required string[] Units { get; init; }

    public required string[] DisplayNames { get; init; }

    public required ColumnKind[] Kinds { get; init; }

    public required DateParser?[] DateParsers { get; init; }

    public int ColumnCount => Headers.Length;
}
