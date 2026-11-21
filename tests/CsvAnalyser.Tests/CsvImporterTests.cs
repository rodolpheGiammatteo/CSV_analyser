using System.Text;
using CsvAnalyser.Core.Data;
using CsvAnalyser.Core.Import;

namespace CsvAnalyser.Tests;

public sealed class CsvImporterTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var f in _files)
            File.Delete(f);
    }

    private Dataset Load(string content, Encoding? encoding = null, int batchSize = CsvImporter.DefaultBatchSize)
    {
        var path = Path.Combine(Path.GetTempPath(), $"csvanalyser-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(true));
        _files.Add(path);
        return CsvImporter.Load(path, batchSize: batchSize);
    }

    private static Channel Get(Dataset ds, string displayName) => ds.Channels.Single(c => c.DisplayName == displayName);

    [Fact]
    public void FrenchFormat_WithBracketUnitsAndTrailingDelimiter()
    {
        var ds = Load("""
            time;Pression [mbara];Etat [NA];Temp [°C];
            0,1;989;1;12,5;
            0,2;990;0;-1,2E-05;
            """);

        Assert.Equal(';', ds.Format.Delimiter);
        Assert.Equal(',', ds.Format.DecimalSeparator);
        Assert.Equal(4, ds.Channels.Count);
        Assert.Equal(2, ds.RowCount);
        Assert.Empty(ds.IgnoredColumns);

        var p = Get(ds, "Pression");
        Assert.Equal("mbara", p.Unit);
        Assert.Equal("Pression [mbara]", p.SourceName);
        Assert.Equal([989, 990], p.Values);
        Assert.Equal("", Get(ds, "Etat").Unit);
        Assert.Equal("°C", Get(ds, "Temp").Unit);
        Assert.Equal(-1.2e-5, Get(ds, "Temp").Values[1], 12);

        Assert.NotNull(ds.Time);
        Assert.Equal("time", ds.Time.Channel.DisplayName);
        Assert.Equal(TimeKind.Numeric, ds.Time.Kind);
    }

    [Fact]
    public void CommaDelimiter_DotDecimal()
    {
        var ds = Load("t,a,b\n0,1.5,2\n1,2.5,3\n2,3.5,4\n");
        Assert.Equal(',', ds.Format.Delimiter);
        Assert.Equal('.', ds.Format.DecimalSeparator);
        Assert.Equal([1.5, 2.5, 3.5], Get(ds, "a").Values);
    }

    [Fact]
    public void TabDelimiter()
    {
        var ds = Load("t\ta\n0\t1,5\n1\t2,5\n");
        Assert.Equal('\t', ds.Format.Delimiter);
        Assert.Equal([1.5, 2.5], Get(ds, "a").Values);
    }

    [Fact]
    public void Preamble_And_UnitsRow()
    {
        var ds = Load("""
            Acquisition banc 3
            Opérateur : X
            Temps;Vitesse;Couple
            s;km/h;N.m
            0;10;5
            0,5;20;6
            """);

        Assert.Equal(2, ds.Format.HeaderLineIndex);
        Assert.Equal(3, ds.Format.UnitLineIndex);
        Assert.Equal(2, ds.RowCount);
        Assert.Equal("km/h", Get(ds, "Vitesse").Unit);
        Assert.Equal([0, 0.5], Get(ds, "Temps").Values);
    }

    [Fact]
    public void InfinityAndInvalidValues_BecomeNaN_ColumnKept()
    {
        var ds = Load("t;a\n0;∞\n1;-42,5\n2;∞\n3;abc\n4;\n");
        var a = Get(ds, "a");
        Assert.True(double.IsNaN(a.Values[0]));
        Assert.Equal(-42.5, a.Values[1]);
        Assert.True(double.IsNaN(a.Values[3]));
        Assert.True(double.IsNaN(a.Values[4]));
    }

    [Fact]
    public void EpochSeconds_ConvertedToRelativeSeconds()
    {
        var ds = Load("time epoch;v\n1795084099,374;1\n1795084099,472;2\n1795084100,374;3\n");
        Assert.NotNull(ds.Time);
        Assert.Equal(TimeKind.EpochSeconds, ds.Time.Kind);
        Assert.Equal("s", ds.Time.Channel.Unit);
        Assert.Equal(0, ds.Time.Channel.Values[0]);
        Assert.Equal(0.098, ds.Time.Channel.Values[1], 6);
        Assert.Equal(1.0, ds.Time.Channel.Values[2], 6);
        var expected = DateTimeOffset.FromUnixTimeMilliseconds(1795084099374).LocalDateTime;
        Assert.Equal(expected, ds.Time.Origin);
    }

    [Fact]
    public void EpochSeconds_ClockGoingBackOnce_IsStillTheTime()
    {
        // A recorder whose clock was resynced 4.6 s back in the middle of the recording (real case, 10 Hz).
        var sb = new StringBuilder("time epoch;v\n");
        for (int i = 0; i < 300; i++)
        {
            double t = 1779789117.2 + i * 0.1 - (i >= 150 ? 4.6 : 0);
            sb.Append(t.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',')).Append(';').Append(i).Append('\n');
        }
        var ds = Load(sb.ToString());

        Assert.NotNull(ds.Time);
        Assert.Equal(TimeKind.EpochSeconds, ds.Time.Kind);
        Assert.False(ds.Time.Channel.IsSortedAscending);
        Assert.Equal(0, ds.Time.Channel.Values[0], 6);
        Assert.Equal(14.9, ds.Time.Channel.Values[149], 6);
        Assert.Equal(15.0 - 4.6, ds.Time.Channel.Values[150], 6);
    }

    [Fact]
    public void EpochMilliseconds_ConvertedToRelativeSeconds()
    {
        var ds = Load("timestamp;v\n1700000000000;1\n1700000000250;2\n");
        Assert.Equal(TimeKind.EpochMilliseconds, ds.Time!.Kind);
        Assert.Equal(0.25, ds.Time.Channel.Values[1], 9);
    }

    [Fact]
    public void FrenchDateColumn_WithMilliseconds()
    {
        var ds = Load("Date;v\n20/11/2026 14:03:05,000;1\n20/11/2026 14:03:06,500;2\n20/11/2026 14:04:05,000;3\n");
        Assert.Equal(ColumnKind.Date, ds.Format.Kinds[0]);
        Assert.Equal(TimeKind.DateTime, ds.Time!.Kind);
        Assert.Equal(new DateTime(2026, 11, 20, 14, 3, 5), ds.Time.Origin);
        Assert.Equal([0, 1.5, 60], ds.Time.Channel.Values);
    }

    [Fact]
    public void IsoDateColumn()
    {
        var ds = Load("date;v\n2026-11-20T14:03:05.250;1\n2026-11-20T14:03:06.250;2\n");
        Assert.Equal(TimeKind.DateTime, ds.Time!.Kind);
        Assert.Equal(1.0, ds.Time.Channel.Values[1], 9);
    }

    [Fact]
    public void NoHeader_GeneratesNames_AndUsesFirstIncreasingColumn()
    {
        var ds = Load("0;5\n1;6\n2;7\n");
        Assert.Equal(-1, ds.Format.HeaderLineIndex);
        Assert.Equal(["Colonne 1", "Colonne 2"], ds.Channels.Select(c => c.DisplayName));
        Assert.Equal("Colonne 1", ds.Time!.Channel.DisplayName);
    }

    [Fact]
    public void QuotedFields()
    {
        var ds = Load("\"t\";\"a;b\"\n\"0\";\"1,5\"\n\"1\";\"2,5\"\n");
        Assert.Equal(2, ds.Channels.Count);
        Assert.Equal("a;b", ds.Channels[1].DisplayName);
        Assert.Equal([1.5, 2.5], ds.Channels[1].Values);
    }

    [Fact]
    public void TextColumn_IsIgnored()
    {
        var ds = Load("t;mode;v\n0;auto;1\n1;manu;2\n");
        Assert.Equal(["mode"], ds.IgnoredColumns);
        Assert.Equal(2, ds.Channels.Count);
    }

    [Fact]
    public void Windows1252Encoding_WithoutBom()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var ds = Load("t;Temp [°C]\n0;1\n1;2\n", Encoding.GetEncoding(1252));
        Assert.Equal("°C", ds.Channels[1].Unit);
    }

    [Fact]
    public void ManyBatches_KeepRowOrder()
    {
        var sb = new StringBuilder("t;v\n");
        for (int i = 0; i < 1000; i++)
            sb.Append($"{i};{i * 2},5\n");

        var ds = Load(sb.ToString(), batchSize: 7);
        Assert.Equal(1000, ds.RowCount);
        var v = Get(ds, "v").Values;
        for (int i = 0; i < 1000; i++)
            Assert.Equal(i * 2 + 0.5, v[i]);
    }

    [Fact]
    public void ShortRows_FillWithNaN()
    {
        var ds = Load("t;a;b\n0;1;2\n1;3\n");
        Assert.True(double.IsNaN(Get(ds, "b").Values[1]));
    }

    [Fact]
    public void RealSampleFile_IfPresent()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CsvAnalyser.sln")))
            dir = dir.Parent;
        var sampleDir = dir is null ? null : Path.Combine(dir.FullName, "données de test");
        var sample = sampleDir is not null && Directory.Exists(sampleDir)
            ? Directory.EnumerateFiles(sampleDir, "*.csv").FirstOrDefault()
            : null;
        if (sample is null)
            return;

        var ds = CsvImporter.Load(sample);
        Assert.Equal(';', ds.Format.Delimiter);
        Assert.Equal(',', ds.Format.DecimalSeparator);
        Assert.Equal(TimeKind.EpochSeconds, ds.Time!.Kind);
        Assert.Empty(ds.IgnoredColumns);
        Assert.Contains(ds.Channels, c => c.DisplayName == "AMS_Sr");
        Assert.Equal("g/s", ds.Channels.Single(c => c.DisplayName == "AMS_CFLOW").Unit);
        Assert.True(ds.RowCount > 1000);
    }
}

public sealed class FieldSplitterTests
{
    [Theory]
    [InlineData("a;b;c", 3)]
    [InlineData("a;b;c;", 4)]
    [InlineData("", 1)]
    [InlineData("\"x;y\";z", 2)]
    public void CountsFields(string line, int expected) =>
        Assert.Equal(expected, FieldSplitter.Count(line, ';'));

    [Fact]
    public void QuotedFieldExcludesQuotes()
    {
        var fields = FieldSplitter.SplitToStrings("\"he said \"\"hi\"\"\";2", ';');
        Assert.Equal(["he said \"hi\"", "2"], fields);
    }
}
