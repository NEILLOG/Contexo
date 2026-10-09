using Contexo.Core.Abstractions;
using Contexo.Core.Parsing.Spreadsheet;
using Contexo.Core.Tables;
using Contexo.Core.Tests.Common;
using Contexo.Core.Tests.Parsing.SpreadsheetTests;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contexo.Core.Tests.Tables;

/// <summary>End to end with the real region reader: a generated .xlsx and .csv, the table the parser registered, then SQL against it.</summary>
public sealed class TableQueryFromXlsxTests
{
    private static IKnowledgeStore StoreWith(ExcelTableRecord record)
    {
        var store = System.Reflection.DispatchProxy.Create<IKnowledgeStore, FakeStoreProxy>();
        ((FakeStoreProxy)(object)store).Tables[record.TableId] = record;
        return store;
    }

    [Fact]
    public async Task Queries_a_large_sheet_registered_by_the_parser()
    {
        var builder = new XlsxBuilder();
        var sheet = builder.AddSheet("訂單");
        sheet.SetRow(0, 0, CellStyle.Bold, "客戶", "金額", "毛利率", "日期");
        for (var i = 1; i <= 300; i++)
        {
            sheet.Set(i, 0, i % 3 == 0 ? "甲" : i % 3 == 1 ? "乙" : "丙");
            sheet.Set(i, 1, i * 1000);
            sheet.Set(i, 2, i / 1000.0, CellStyle.Percent2);
            sheet.Set(i, 3, new DateTime(2025, 1, 1).AddDays(i), CellStyle.Date);
        }

        using var files = new TempDirectory();
        var bytes = builder.Build();
        var path = SpreadsheetTestSupport.WriteTemp(files, "訂單.xlsx", bytes);
        var document = await SpreadsheetTestSupport.ParseAsync(bytes, "訂單.xlsx");
        var table = Assert.Single(document.Tables);
        var service = new TableQueryService(StoreWith(new ExcelTableRecord("t7", 1, path, table)), new SpreadsheetRegionReader(), NullLogger<TableQueryService>.Instance);

        var description = await service.DescribeAsync("t7", 3, CancellationToken.None);
        Assert.Equal(300, description.RowCount);
        Assert.Equal(["text", "number", "number", "date"], description.Columns.Select(c => c.InferredType));
        Assert.Equal("2025-01-02 00:00:00", description.SampleRows[0][3]);

        var result = await service.QueryAsync("t7", "SELECT \"客戶\", SUM(\"金額\") FROM t GROUP BY \"客戶\" ORDER BY 2 DESC", 10, CancellationToken.None);
        var expected = Enumerable.Range(1, 300)
            .GroupBy(i => i % 3 == 0 ? "甲" : i % 3 == 1 ? "乙" : "丙")
            .Select(g => (Name: g.Key, Sum: g.Sum(i => (long)i * 1000)))
            .OrderByDescending(x => x.Sum)
            .Select(x => new[] { x.Name, x.Sum.ToString() })
            .ToList();
        Assert.Equal(expected, result.Rows.Select(r => r.ToArray()));
        service.Dispose();
    }

    [Fact]
    public async Task Queries_a_csv_file()
    {
        var csv = new System.Text.StringBuilder("品名,數量,單價\n");
        for (var i = 1; i <= 600; i++)
        {
            csv.Append($"品{i},{i},\"{i * 10:N0}\"\n");
        }

        using var files = new TempDirectory();
        var path = SpreadsheetTestSupport.WriteTemp(files, "清單.csv", new System.Text.UTF8Encoding(false).GetBytes(csv.ToString()));
        var document = await SpreadsheetTestSupport.ParseCsvAsync(csv.ToString(), "清單.csv");
        var table = Assert.Single(document.Tables);
        var service = new TableQueryService(StoreWith(new ExcelTableRecord("t1", 1, path, table)), new SpreadsheetRegionReader(), NullLogger<TableQueryService>.Instance);

        var result = await service.QueryAsync("t1", "SELECT SUM(\"單價\"), COUNT(*) FROM t WHERE \"數量\" > 500", 5, CancellationToken.None);

        Assert.Equal([Enumerable.Range(501, 100).Sum(i => (long)i * 10).ToString(), "100"], result.Rows.Single());
        service.Dispose();
    }
}
