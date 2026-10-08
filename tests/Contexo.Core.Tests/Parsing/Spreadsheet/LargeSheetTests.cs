using System.Diagnostics;
using Contexo.Core.Abstractions;
using Contexo.Core.Parsing.Spreadsheet;
using Contexo.Core.Tests.Common;
using static Contexo.Core.Tests.Parsing.SpreadsheetTests.SpreadsheetTestSupport;

namespace Contexo.Core.Tests.Parsing.SpreadsheetTests;

public sealed class LargeSheetTests
{
    private const int Rows = 100_000;
    private const int Columns = 10;

    [Fact]
    public async Task Hundred_thousand_rows_by_ten_columns_parse_in_under_ten_seconds()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("大表.xlsx");
        WriteBigWorkbook(path);

        // Warm up so JIT time is not counted against the table itself.
        await ParseAsync(new XlsxBuilder().AddSheetAndBuild("暖身", "A1", "x"));

        var stopwatch = Stopwatch.StartNew();
        ParsedDocument document;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            document = await new SpreadsheetParser().ParseAsync(new ParseContext(stream, "大表.xlsx", new ParserOptions()), CancellationToken.None);
        }

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Parsing took {stopwatch.Elapsed.TotalSeconds:F1} s.");

        var table = Assert.Single(document.Tables);
        Assert.Equal($"資料!A1:J{Rows + 1}", table.TableKey);
        Assert.Equal(Rows, table.DataRowCount);
        Assert.Equal(1, table.HeaderRowCount);
        Assert.Equal(Enumerable.Range(1, Columns).Select(c => $"欄位{c}"), table.Columns);
        Assert.Equal(5, table.SampleRows.Count);
        Assert.Equal("0", table.SampleRows[0][0]);
        Assert.Single(document.Sections);

        var read = Stopwatch.StartNew();
        var region = await new SpreadsheetRegionReader().ReadAsync(path, table.Sheet, table.CellRange, table.HeaderRowCount, CancellationToken.None);
        read.Stop();
        Assert.Equal(Rows, region.Rows.Count);
        Assert.Equal(table.Columns, region.Columns);
        Assert.Equal((Rows - 1).ToString(), region.Rows[^1][0]);
        Assert.True(read.Elapsed < TimeSpan.FromSeconds(10), $"Reading took {read.Elapsed.TotalSeconds:F1} s.");
    }

    private static void WriteBigWorkbook(string path)
    {
        var builder = new XlsxBuilder();
        var sheet = builder.AddSheet("資料");
        for (var c = 0; c < Columns; c++)
        {
            sheet.Set(0, c, $"欄位{c + 1}", CellStyle.Bold);
        }

        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Columns; c++)
            {
                sheet.Set(r + 1, c, c == 1 ? (r % 100).ToString() + "號" : (r + c) % 1000 == 0 ? r : r * 1.0 + c);
            }
        }

        using var stream = File.Create(path);
        builder.Build(stream);
    }
}

internal static class XlsxBuilderExtensions
{
    public static byte[] AddSheetAndBuild(this XlsxBuilder builder, string name, string reference, object value)
    {
        builder.AddSheet(name).Set(reference, value);
        return builder.Build();
    }
}
