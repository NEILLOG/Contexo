using System.Text;
using Contexo.Core.Abstractions;
using Contexo.Core.Parsing.Spreadsheet;
using Contexo.Core.Tests.Common;
using static Contexo.Core.Tests.Parsing.SpreadsheetTests.SpreadsheetTestSupport;

namespace Contexo.Core.Tests.Parsing.SpreadsheetTests;

public sealed class SpreadsheetRegionReaderTests
{
    private static readonly SpreadsheetRegionReader Reader = new();

    private static XlsxBuilder ListWorkbook()
    {
        var builder = new XlsxBuilder();
        FillList(builder.AddSheet("清單"), firstRow: 0, dataRows: 500);
        return builder;
    }

    private static XlsxBuilder TwoLevelWorkbook()
    {
        var builder = new XlsxBuilder();
        var sheet = builder.AddSheet("季報");
        sheet.Set("A1", "2025 年季報", CellStyle.Bold).Merge("A1:G1");
        sheet.Set("A2", "部門", CellStyle.Bold).Merge("A2:A3");
        sheet.Set("B2", "第一季", CellStyle.Bold).Merge("B2:D2");
        sheet.Set("E2", "第二季", CellStyle.Bold).Merge("E2:G2");
        sheet.SetRow(2, 1, CellStyle.Bold, "一月", "二月", "三月", "四月", "五月", "六月");
        for (var i = 0; i < 200; i++)
        {
            sheet.Set(3 + i, 0, $"部門{i}");
            for (var c = 1; c <= 6; c++)
            {
                sheet.Set(3 + i, c, (i * 10) + c);
            }
        }

        return builder;
    }

    [Fact]
    public async Task Reads_back_the_list_table_the_parser_registered()
    {
        using var dir = new TempDirectory();
        var path = WriteTemp(dir, "清單.xlsx", ListWorkbook().Build());
        var table = Assert.Single((await ParseAsync(File.ReadAllBytes(path))).Tables);

        var region = await Reader.ReadAsync(path, table.Sheet, table.CellRange, table.HeaderRowCount, CancellationToken.None);

        Assert.Equal(table.Columns, region.Columns);
        Assert.Equal(table.DataRowCount, region.Rows.Count);
        Assert.All(region.Rows, row => Assert.Equal(table.Columns.Count, row.Count));
        Assert.Equal(["品名001", "耗材", "1", "1.5", "2025/01/02", null], region.Rows[0]);
        Assert.Equal(["品名500", "文具", "500", "750", "2026/05/16", "備註500"], region.Rows[^1]);
        for (var i = 0; i < table.SampleRows.Count; i++)
        {
            Assert.Equal(table.SampleRows[i], region.Rows[i].Select(v => v ?? string.Empty));
        }
    }

    [Fact]
    public async Task Reads_back_the_two_level_header_table_with_the_same_column_names()
    {
        using var dir = new TempDirectory();
        var path = WriteTemp(dir, "季報.xlsx", TwoLevelWorkbook().Build());
        var table = Assert.Single((await ParseAsync(File.ReadAllBytes(path))).Tables);
        Assert.Equal("季報!A2:G203", table.TableKey);
        Assert.Equal(2, table.HeaderRowCount);

        var region = await Reader.ReadAsync(path, table.Sheet, table.CellRange, table.HeaderRowCount, CancellationToken.None);

        Assert.Equal(
            ["部門", "第一季_一月", "第一季_二月", "第一季_三月", "第二季_四月", "第二季_五月", "第二季_六月"],
            region.Columns);
        Assert.Equal(table.Columns, region.Columns);
        Assert.Equal(200, region.Rows.Count);
        Assert.Equal(["部門0", "1", "2", "3", "4", "5", "6"], region.Rows[0]);
        Assert.Equal(["部門199", "1991", "1992", "1993", "1994", "1995", "1996"], region.Rows[^1]);
    }

    [Fact]
    public async Task Can_read_while_another_stream_has_the_file_open_for_reading_and_writing()
    {
        using var dir = new TempDirectory();
        var path = WriteTemp(dir, "清單.xlsx", ListWorkbook().Build());
        var table = Assert.Single((await ParseAsync(File.ReadAllBytes(path))).Tables);

        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var region = await Reader.ReadAsync(path, table.Sheet, table.CellRange, table.HeaderRowCount, CancellationToken.None);

        Assert.Equal(500, region.Rows.Count);
    }

    [Fact]
    public async Task Reads_back_a_csv_table()
    {
        using var dir = new TempDirectory();
        var csv = new StringBuilder("編號,品名,數量\n");
        for (var i = 1; i <= 300; i++)
        {
            csv.Append(i).Append(",\"品名, ").Append(i).Append("\",").Append(i % 7 == 0 ? string.Empty : i.ToString()).Append('\n');
        }

        var path = WriteTemp(dir, "訂單.csv", Encoding.UTF8.GetBytes(csv.ToString()));
        var table = Assert.Single((await ParseAsync(File.ReadAllBytes(path), "訂單.csv")).Tables);

        var region = await Reader.ReadAsync(path, table.Sheet, table.CellRange, table.HeaderRowCount, CancellationToken.None);

        Assert.Equal(["編號", "品名", "數量"], region.Columns);
        Assert.Equal(300, region.Rows.Count);
        Assert.Equal(["1", "品名, 1", "1"], region.Rows[0]);
        Assert.Equal(["7", "品名, 7", null], region.Rows[6]);
    }

    [Fact]
    public async Task Header_row_count_zero_names_columns_by_letter()
    {
        using var dir = new TempDirectory();
        var path = WriteTemp(dir, "清單.xlsx", ListWorkbook().Build());

        var region = await Reader.ReadAsync(path, "清單", "B2:D4", 0, CancellationToken.None);

        Assert.Equal(["欄B", "欄C", "欄D"], region.Columns);
        Assert.Equal(3, region.Rows.Count);
        Assert.Equal(["耗材", "1", "1.5"], region.Rows[0]);
    }

    [Fact]
    public async Task Missing_file_is_a_file_not_found_error()
    {
        using var dir = new TempDirectory();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            Reader.ReadAsync(dir.Combine("不存在.xlsx"), "清單", "A1:B2", 0, CancellationToken.None));
    }

    [Fact]
    public async Task Missing_sheet_or_bad_range_is_an_invalid_operation_with_the_reason()
    {
        using var dir = new TempDirectory();
        var path = WriteTemp(dir, "清單.xlsx", ListWorkbook().Build());

        var noSheet = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Reader.ReadAsync(path, "沒有這張", "A1:B2", 0, CancellationToken.None));
        var badRange = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Reader.ReadAsync(path, "清單", "ABC", 0, CancellationToken.None));
        var beyond = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Reader.ReadAsync(path, "清單", "A900:F950", 1, CancellationToken.None));
        var partlyBeyond = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Reader.ReadAsync(path, "清單", "A1:F600", 1, CancellationToken.None));
        var tooManyHeaders = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Reader.ReadAsync(path, "清單", "A1:F3", 5, CancellationToken.None));

        Assert.Contains("沒有這張", noSheet.Message);
        Assert.Contains("ABC", badRange.Message);
        Assert.Contains("A900:F950", beyond.Message);
        Assert.Contains("A1:F600", partlyBeyond.Message);
        Assert.Contains("5", tooManyHeaders.Message);
    }

    [Fact]
    public async Task Broken_file_raises_a_parse_exception()
    {
        using var dir = new TempDirectory();
        var path = WriteTemp(dir, "壞.xlsx", Encoding.UTF8.GetBytes("not a workbook"));

        var ex = await Assert.ThrowsAsync<DocumentParseException>(() =>
            Reader.ReadAsync(path, "清單", "A1:B2", 0, CancellationToken.None));

        Assert.Equal(DocumentErrorCode.Corrupted, ex.Code);
    }
}
