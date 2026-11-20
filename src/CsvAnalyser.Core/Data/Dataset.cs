using CsvAnalyser.Core.Import;

namespace CsvAnalyser.Core.Data;

public enum TimeKind
{
    /// <summary>Numeric column used as is (usually seconds).</summary>
    Numeric,
    EpochSeconds,
    EpochMilliseconds,
    DateTime,
}

/// <summary>Describes the column used as default X axis. For epoch/date columns the values were converted to seconds relative to <see cref="Origin"/>.</summary>
public sealed record TimeInfo(Channel Channel, TimeKind Kind, DateTime? Origin);

public sealed class Dataset
{
    public required string FilePath { get; init; }

    public required CsvFormat Format { get; init; }

    public required IReadOnlyList<Channel> Channels { get; init; }

    public required int RowCount { get; init; }

    public TimeInfo? Time { get; init; }

    /// <summary>Text columns that cannot be plotted.</summary>
    public IReadOnlyList<string> IgnoredColumns { get; init; } = [];

    public TimeSpan LoadDuration { get; init; }
}
