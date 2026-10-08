using System.Text;
using Contexo.Core.Abstractions;
using Contexo.Core.Parsing.Spreadsheet;
using static Contexo.Core.Tests.Parsing.SpreadsheetTests.SpreadsheetTestSupport;

namespace Contexo.Core.Tests.Parsing.SpreadsheetTests;

public sealed class CsvParserTests
{
    [Theory]
    [InlineData(",")]
    [InlineData(";")]
    [InlineData("\t")]
    public async Task Delimiter_is_detected(string delimiter)
    {
        var csv = string.Join('\n', new[]
        {
            string.Join(delimiter, "品名", "數量", "備註"),
            string.Join(delimiter, "螺絲", "100", "急件"),
            string.Join(delimiter, "螺母", "200", "一般"),
        });

        var document = await ParseCsvAsync(csv);

        var section = Assert.Single(document.Sections);
        Assert.Equal(SectionKind.Table, section.Kind);
        Assert.Equal("csv", section.Location.Sheet);
        Assert.Equal("A1:C3", section.Location.CellRange);
        Assert.Contains("<tr><td>螺絲</td><td>100</td><td>急件</td></tr>", section.Text);
    }

    [Fact]
    public async Task Quoted_fields_may_hold_the_delimiter_quotes_and_line_breaks()
    {
        const string csv = "名稱,說明,金額\r\n\"甲, 乙\",\"他說：\"\"好\"\"\",100\r\n\"第一行\r\n第二行\",普通,200\r\n";

        var document = await ParseCsvAsync(csv);

        var section = Assert.Single(document.Sections);
        Assert.Equal("A1:C3", section.Location.CellRange);
        Assert.Contains("<td>甲, 乙</td>", section.Text);
        Assert.Contains("<td>他說：&quot;好&quot;</td>", section.Text);
        Assert.Contains("<td>第一行<br>第二行</td>", section.Text);
    }

    [Fact]
    public async Task Big5_files_are_decoded()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var big5 = Encoding.GetEncoding(950);
        var csv = "品名,數量,廠商\n螺絲,100,台灣五金股份有限公司\n螺母,200,高雄螺絲工廠\n墊圈,300,新竹精密零件廠\n";

        var document = await ParseAsync(big5.GetBytes(csv), "舊檔.csv");

        var text = Assert.Single(document.Sections).Text;
        Assert.Contains("<td>台灣五金股份有限公司</td>", text);
        Assert.Contains("<td>新竹精密零件廠</td>", text);
    }

    [Fact]
    public async Task Utf8_byte_order_mark_is_not_part_of_the_first_header()
    {
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("品名,數量\n螺絲,1\n")).ToArray();

        var document = await ParseAsync(bytes, "bom.csv");

        Assert.Contains("<td>品名</td>", Assert.Single(document.Sections).Text);
    }

    [Fact]
    public async Task Large_csv_is_registered_as_one_table()
    {
        var csv = new StringBuilder("編號,品名,數量,日期\n");
        for (var i = 1; i <= 500; i++)
        {
            csv.Append(i).Append(",品名").Append(i).Append(',').Append(i * 2).Append(",2025-01-").Append((i % 28) + 1).Append('\n');
        }

        var document = await ParseCsvAsync(csv.ToString(), "訂單.csv");

        var section = Assert.Single(document.Sections);
        Assert.Equal(SectionKind.TableSummary, section.Kind);
        var table = Assert.Single(document.Tables);
        Assert.Equal("csv!A1:D501", table.TableKey);
        Assert.Equal("csv", table.Sheet);
        Assert.Equal(1, table.HeaderRowCount);
        Assert.Equal(["編號", "品名", "數量", "日期"], table.Columns);
        Assert.Equal(500, table.DataRowCount);
        Assert.Equal(["1", "品名1", "2", "2025-01-2"], table.SampleRows[0]);
        Assert.StartsWith("檔案：訂單.csv\n工作表：csv（範圍 A1:D501，共 500 筆資料）\n欄位：編號、品名、數量、日期\n", table.Description);
    }

    [Fact]
    public async Task Large_csv_with_a_title_line_and_blank_line_keeps_the_title_out_of_the_columns()
    {
        var csv = new StringBuilder("\"2025 年度報表\"\n\n品名,數量,單價\n");
        for (var i = 1; i <= 200; i++)
        {
            csv.Append("品名").Append(i).Append(',').Append(i).Append(',').Append(i * 1.5).Append('\n');
        }

        var document = await ParseCsvAsync(csv.ToString());

        var table = Assert.Single(document.Tables);
        Assert.Equal("csv!A3:C203", table.TableKey);
        Assert.Equal(["品名", "數量", "單價"], table.Columns);
        Assert.Equal(1, table.HeaderRowCount);
        Assert.Equal(200, table.DataRowCount);
        Assert.Contains("表格標題：2025 年度報表", table.Description.Split('\n'));
    }

    [Fact]
    public async Task Empty_csv_gives_nothing()
    {
        var document = await ParseCsvAsync("\n\n");

        Assert.Empty(document.Sections);
        Assert.Empty(document.Tables);
    }

    [Fact]
    public void Delimiter_detection_prefers_the_consistent_candidate()
    {
        Assert.Equal(';', CsvSheetCells.DetectDelimiter("a;b;c\n1,5;2,5;3\n4;5;6\n"));
        Assert.Equal(',', CsvSheetCells.DetectDelimiter("a,b,c\n1,2,3\n"));
        Assert.Equal('\t', CsvSheetCells.DetectDelimiter("a\tb\n1\t2\n"));
        Assert.Equal(',', CsvSheetCells.DetectDelimiter("only one column\nanother\n"));
    }

    [Fact]
    public void Record_parser_follows_RFC_4180()
    {
        var records = CsvSheetCells.Parse("a,\"b,c\",\"d\"\"e\"\n\"x\ny\",,z", ',', int.MaxValue).Select(r => r.ToArray()).ToArray();

        Assert.Equal(2, records.Length);
        Assert.Equal(["a", "b,c", "d\"e"], records[0]);
        Assert.Equal(["x\ny", "", "z"], records[1]);
    }
}
