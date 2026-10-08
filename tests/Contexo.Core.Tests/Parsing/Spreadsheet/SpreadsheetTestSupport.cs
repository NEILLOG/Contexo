using Contexo.Core.Abstractions;
using Contexo.Core.Parsing.Spreadsheet;
using Contexo.Core.Tests.Common;

namespace Contexo.Core.Tests.Parsing.SpreadsheetTests;

internal static class SpreadsheetTestSupport
{
    public static async Task<ParsedDocument> ParseAsync(byte[] bytes, string fileName = "test.xlsx", ParserOptions? options = null)
    {
        using var stream = new MemoryStream(bytes);
        var document = await new SpreadsheetParser().ParseAsync(new ParseContext(stream, fileName, options ?? new ParserOptions()), CancellationToken.None);
        Assert.True(stream.CanRead, "The parser must not dispose the stream it was given.");
        return document;
    }

    public static Task<ParsedDocument> ParseAsync(XlsxBuilder builder, string fileName = "test.xlsx", ParserOptions? options = null) =>
        ParseAsync(builder.Build(), fileName, options);

    public static Task<ParsedDocument> ParseCsvAsync(string text, string fileName = "data.csv", ParserOptions? options = null) =>
        ParseAsync(new System.Text.UTF8Encoding(false).GetBytes(text), fileName, options);

    /// <summary>Header text then 500 rows: 品名 / 類別 / 數量 / 單價 / 日期 / 備註.</summary>
    public static void FillList(SheetBuilder sheet, int firstRow, int dataRows)
    {
        sheet.SetRow(firstRow, 0, CellStyle.Bold, "品名", "類別", "數量", "單價", "日期", "備註");
        for (var i = 1; i <= dataRows; i++)
        {
            var row = firstRow + i;
            sheet.Set(row, 0, $"品名{i:000}");
            sheet.Set(row, 1, i % 2 == 0 ? "文具" : "耗材");
            sheet.Set(row, 2, i);
            sheet.Set(row, 3, i * 1.5);
            sheet.Set(row, 4, new DateTime(2025, 1, 1).AddDays(i), CellStyle.Date);
            sheet.Set(row, 5, i % 5 == 0 ? $"備註{i}" : null);
        }
    }

    public static string WriteTemp(TempDirectory files, string name, byte[] bytes)
    {
        var path = files.Combine(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
