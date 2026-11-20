using CsvAnalyser.Core.Export;

namespace CsvAnalyser.Tests;

public class CsvExporterTests
{
    private static string Export(IReadOnlyList<ExportColumn> columns, ExportRows rows, CsvExportOptions? options = null)
    {
        using var writer = new StringWriter();
        CsvExporter.Write(writer, columns, rows, options ?? new CsvExportOptions());
        return writer.ToString();
    }

    [Fact]
    public void FrenchDefaults_SemicolonAndDecimalComma_NaNIsEmpty()
    {
        var text = Export(
            [new("t [s]", [0, 0.5, 1]), new("U [V]", [1.25, double.NaN, -3])],
            ExportRows.All(3));

        Assert.Equal("t [s];U [V]\r\n0;1,25\r\n0,5;\r\n1;-3\r\n", text);
    }

    [Fact]
    public void CommaDelimiter_DotDecimal_QuotesHeaderWithComma()
    {
        var text = Export(
            [new("a, b", [1.5]), new("say \"hi\"", [2])],
            ExportRows.All(1),
            new CsvExportOptions { Delimiter = ',', DecimalSeparator = '.' });

        Assert.Equal("\"a, b\",\"say \"\"hi\"\"\"\r\n1.5,2\r\n", text);
    }

    [Fact]
    public void RowRangeAndFilter_OnlySelectedRowsAreWritten()
    {
        double[] x = [0, 1, 2, 3, 4, 5];
        var text = Export([new("x", x)], new ExportRows(1, 5, row => row % 2 == 1));

        Assert.Equal("x\r\n1\r\n3\r\n", text);
    }

    [Fact]
    public void DateColumn_AbsoluteTimeFromOrigin()
    {
        var origin = new DateTime(2024, 3, 1, 12, 0, 0);
        var text = Export(
            [new("v", [7, 8])],
            ExportRows.All(2),
            new CsvExportOptions { DateColumn = new ExportDateColumn("Date", origin, [0, 1.5]) });

        Assert.Equal("Date;v\r\n01/03/2024 12:00:00.000;7\r\n01/03/2024 12:00:01.500;8\r\n", text);
    }

    [Fact]
    public void SameDelimiterAndDecimal_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Export([new("v", [1])], ExportRows.All(1), new CsvExportOptions { Delimiter = ',', DecimalSeparator = ',' }));
    }
}
