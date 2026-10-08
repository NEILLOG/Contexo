using Contexo.Core.Abstractions;
using static Contexo.Core.Tests.Parsing.SpreadsheetTests.SpreadsheetTestSupport;

namespace Contexo.Core.Tests.Parsing.SpreadsheetTests;

public sealed class SpreadsheetParserTests
{
    [Fact]
    public async Task Quotation_form_becomes_one_whole_table_with_colspan()
    {
        var builder = new XlsxBuilder();
        var sheet = builder.AddSheet("報價單");
        sheet.Set("A1", "報價單", CellStyle.Bold).Merge("A1:F1");
        sheet.Set("A3", "客戶").Set("B3", "ABC 公司").Merge("B3:C3");
        sheet.Set("E3", "日期").Set("F3", new DateTime(2025, 3, 15), CellStyle.Date);
        sheet.Set("A4", "聯絡人").Set("B4", "王小明").Set("E4", "電話").Set("F4", "02-1234-5678");
        sheet.SetRow(6, 0, CellStyle.Bold, "品名", "規格", "數量", "單價", "金額");
        sheet.SetRow(7, 0, CellStyle.Plain, "螺絲", "M3", 100, 2.5, 250);
        sheet.SetRow(8, 0, CellStyle.Plain, "螺母", "M3", 100, 1.5, 150);
        sheet.SetRow(9, 0, CellStyle.Plain, "墊片", "M3", 200, 0.5, 100);
        sheet.Set("D12", "合計").Set("E12", new FormulaValue("SUM(E8:E10)", 500));
        sheet.Set("A14", "備註").Set("B14", "含稅，三十日內付款").Merge("B14:F14");
        sheet.Set("A15", "報價人").Set("B15", "李大華");

        var document = await ParseAsync(builder);

        var section = Assert.Single(document.Sections);
        Assert.Equal(SectionKind.Table, section.Kind);
        Assert.True(section.KeepWhole);
        Assert.Equal("報價單", section.Location.Sheet);
        Assert.Equal("報價單", section.Location.Title);
        Assert.Equal("A1:F15", section.Location.CellRange);
        Assert.Contains("<td colspan=\"6\">報價單</td>", section.Text);
        Assert.Contains("<td colspan=\"2\">ABC 公司</td>", section.Text);
        Assert.Contains("<td colspan=\"5\">含稅，三十日內付款</td>", section.Text);
        Assert.Contains("<td>2025/03/15</td>", section.Text);
        Assert.Contains("<td>500</td>", section.Text);
        Assert.Empty(document.Tables);
    }

    [Fact]
    public async Task Large_list_becomes_a_table_summary_and_a_registered_table()
    {
        var builder = new XlsxBuilder();
        FillList(builder.AddSheet("清單"), firstRow: 0, dataRows: 500);

        var document = await ParseAsync(builder, "庫存.xlsx");

        var section = Assert.Single(document.Sections);
        Assert.Equal(SectionKind.TableSummary, section.Kind);
        Assert.True(section.KeepWhole);
        Assert.Equal("清單!A1:F501", section.TableKey);
        Assert.Equal("A1:F501", section.Location.CellRange);

        var table = Assert.Single(document.Tables);
        Assert.Equal("清單!A1:F501", table.TableKey);
        Assert.Equal("清單", table.Sheet);
        Assert.Equal("A1:F501", table.CellRange);
        Assert.Equal(1, table.HeaderRowCount);
        Assert.Equal(["品名", "類別", "數量", "單價", "日期", "備註"], table.Columns);
        Assert.Equal(500, table.DataRowCount);
        Assert.Equal(5, table.SampleRows.Count);
        Assert.Equal(["品名001", "耗材", "1", "1.5", "2025/01/02", ""], table.SampleRows[0]);
        Assert.Equal(section.Text, table.Description);

        var lines = table.Description.Split('\n');
        Assert.Equal("檔案：庫存.xlsx", lines[0]);
        Assert.Equal("工作表：清單（範圍 A1:F501，共 500 筆資料）", lines[1]);
        Assert.Equal("欄位：品名、類別、數量、單價、日期、備註", lines[2]);
        Assert.Equal("範例資料：", lines[3]);
        Assert.Equal("品名=品名001；類別=耗材；數量=1；單價=1.5；日期=2025/01/02；備註=", lines[4]);
        Assert.Equal("品名=品名005；類別=耗材；數量=5；單價=7.5；日期=2025/01/06；備註=備註5", lines[8]);
        Assert.Equal("（完整內容請使用 query_table 查詢）", lines[9]);
        Assert.Equal(10, lines.Length);
    }

    [Fact]
    public async Task Two_tables_on_one_sheet_are_found_separately()
    {
        var builder = new XlsxBuilder();
        var sheet = builder.AddSheet("兩張表");
        FillList(sheet, firstRow: 0, dataRows: 300);
        FillList(sheet, firstRow: 303, dataRows: 300);

        var document = await ParseAsync(builder);

        Assert.Equal(2, document.Sections.Count);
        Assert.All(document.Sections, s => Assert.Equal(SectionKind.TableSummary, s.Kind));
        Assert.Equal(["兩張表!A1:F301", "兩張表!A304:F604"], document.Tables.Select(t => t.TableKey));
        Assert.All(document.Tables, t =>
        {
            Assert.Equal(1, t.HeaderRowCount);
            Assert.Equal(300, t.DataRowCount);
            Assert.Equal("品名", t.Columns[0]);
        });
    }

    [Fact]
    public async Task Banner_row_above_the_header_becomes_the_caption()
    {
        var builder = new XlsxBuilder();
        var sheet = builder.AddSheet("銷售");
        sheet.Set("A1", "2025 年度銷售", CellStyle.Bold).Merge("A1:F1");
        FillList(sheet, firstRow: 1, dataRows: 500);

        var document = await ParseAsync(builder);

        var table = Assert.Single(document.Tables);
        Assert.Equal("銷售!A2:F502", table.TableKey);
        Assert.Equal(["品名", "類別", "數量", "單價", "日期", "備註"], table.Columns);
        Assert.Equal(1, table.HeaderRowCount);
        Assert.Equal(500, table.DataRowCount);
        Assert.Contains("表格標題：2025 年度銷售", table.Description.Split('\n'));
        Assert.DoesNotContain("2025 年度銷售", table.Columns);
    }

    [Fact]
    public async Task Single_cell_title_a_few_rows_above_a_table_is_its_caption()
    {
        var builder = new XlsxBuilder();
        var sheet = builder.AddSheet("銷售");
        sheet.Set("A1", "二月出貨清單");
        FillList(sheet, firstRow: 2, dataRows: 500);

        var document = await ParseAsync(builder);

        var table = Assert.Single(document.Tables);
        Assert.Equal("銷售!A3:F503", table.TableKey);
        Assert.Contains("表格標題：二月出貨清單", table.Description.Split('\n'));
        Assert.Single(document.Sections);
    }

    [Fact]
    public async Task Two_level_header_joins_the_levels_with_an_underscore()
    {
        var builder = new XlsxBuilder();
        var sheet = builder.AddSheet("季報");
        sheet.Set("A1", "部門", CellStyle.Bold).Merge("A1:A2");
        sheet.Set("B1", "第一季", CellStyle.Bold).Merge("B1:D1");
        sheet.Set("E1", "第二季", CellStyle.Bold).Merge("E1:G1");
        sheet.SetRow(1, 1, CellStyle.Bold, "一月", "二月", "三月", "四月", "五月", "六月");
        for (var i = 0; i < 200; i++)
        {
            sheet.Set(2 + i, 0, $"部門{i}");
            for (var c = 1; c <= 6; c++)
            {
                sheet.Set(2 + i, c, (i * 10) + c);
            }
        }

        var document = await ParseAsync(builder);

        var table = Assert.Single(document.Tables);
        Assert.Equal("季報!A1:G202", table.TableKey);
        Assert.Equal(2, table.HeaderRowCount);
        Assert.Equal(200, table.DataRowCount);
        Assert.Equal(
            ["部門", "第一季_一月", "第一季_二月", "第一季_三月", "第二季_四月", "第二季_五月", "第二季_六月"],
            table.Columns);
        Assert.Equal(["部門0", "1", "2", "3", "4", "5", "6"], table.SampleRows[0]);
    }

    [Fact]
    public async Task Header_is_not_found_in_an_all_number_list_and_columns_are_named_by_letter()
    {
        var builder = new XlsxBuilder();
        var sheet = builder.AddSheet("數字");
        for (var r = 0; r < 100; r++)
        {
            for (var c = 0; c < 5; c++)
            {
                sheet.Set(r, c + 2, (r * 5) + c);
            }
        }

        var document = await ParseAsync(builder);

        var table = Assert.Single(document.Tables);
        Assert.Equal(0, table.HeaderRowCount);
        Assert.Equal(["欄C", "欄D", "欄E", "欄F", "欄G"], table.Columns);
        Assert.Equal(100, table.DataRowCount);
        Assert.Equal("數字!C1:G100", table.TableKey);
    }

    [Fact]
    public async Task Blank_and_repeated_header_names_are_made_unique()
    {
        var builder = new XlsxBuilder();
        var sheet = builder.AddSheet("重複");
        sheet.SetRow(0, 0, CellStyle.Bold, "名稱", "數量", null, "數量", "數量");
        for (var i = 1; i <= 100; i++)
        {
            sheet.SetRow(i, 0, CellStyle.Plain, $"項目{i}", i, "x", i * 2, i * 3);
        }

        var document = await ParseAsync(builder);

        var table = Assert.Single(document.Tables);
        Assert.Equal(["名稱", "數量", "欄C", "數量_2", "數量_3"], table.Columns);
    }

    [Fact]
    public async Task Values_are_shown_the_way_Excel_shows_them()
    {
        var builder = new XlsxBuilder();
        var sheet = builder.AddSheet("格式");
        sheet.Set("A1", new DateTime(2025, 3, 15), CellStyle.Date);
        sheet.Set("A2", new DateTime(2025, 3, 15, 13, 45, 0), CellStyle.DateTime);
        sheet.Set("A3", ((13 * 60) + 45) / 1440.0, CellStyle.Time);
        sheet.Set("A4", 0.256, CellStyle.Percent);
        sheet.Set("A5", 0.07, CellStyle.Percent2);
        sheet.Set("A6", new DateTime(2025, 12, 31), CellStyle.CustomDate);
        sheet.Set("A7", 12.5, CellStyle.QuotedM);
        sheet.Set("A8", 1234567, CellStyle.Thousands);
        sheet.Set("A9", 0.1 + 0.2);
        sheet.Set("A10", new FormulaValue("SUM(1,41)", 42));
        sheet.Set("A11", new FormulaValue("\"a\"&\"bc\"", "abc"));
        sheet.Set("A12", true);
        sheet.Set("A13", false);
        sheet.Set("A14", new ErrorValue("#N/A"));
        sheet.Set("A15", 100);
        sheet.Set("A16", -3.25);
        sheet.Set("A17", 1234.5);
        sheet.Set("A18", new FormulaValue("A1+1", new DateTime(2025, 3, 16)), CellStyle.Date);
        sheet.Set("A19", 0.0345, CellStyle.CustomPercent);

        var document = await ParseAsync(builder);

        var html = Assert.Single(document.Sections).Text;
        foreach (var expected in new[]
                 {
                     "2025/03/15", "2025/03/15 13:45", "13:45", "25.6%", "7%", "2025/12/31", "12.5", "1234567", "0.3", "42", "abc",
                     "TRUE", "FALSE", "#N/A", "100", "-3.25", "1234.5", "2025/03/16", "3.45%",
                 })
        {
            Assert.Contains($"<td>{expected}</td>", html);
        }
    }

    [Fact]
    public async Task Dates_are_right_in_a_1904_workbook()
    {
        var builder = new XlsxBuilder { Date1904 = true };
        builder.AddSheet("日期").Set("A1", new DateTime(2025, 3, 15), CellStyle.Date).Set("A2", new DateTime(2019, 1, 1, 8, 30, 0), CellStyle.DateTime);

        var document = await ParseAsync(builder);

        var html = Assert.Single(document.Sections).Text;
        Assert.Contains("<td>2025/03/15</td>", html);
        Assert.Contains("<td>2019/01/01 08:30</td>", html);
    }

    [Fact]
    public async Task Hidden_sheets_are_skipped_with_a_warning()
    {
        var builder = new XlsxBuilder();
        builder.AddSheet("公開").Set("A1", "看得到");
        builder.AddSheet("隱藏", hidden: true).Set("A1", "看不到");

        var document = await ParseAsync(builder);

        var section = Assert.Single(document.Sections);
        Assert.Contains("看得到", section.Text);
        Assert.DoesNotContain(document.Sections, s => s.Text.Contains("看不到"));
        Assert.Equal(["hidden-sheet:隱藏"], document.Warnings);
    }

    [Fact]
    public async Task Sheets_are_read_in_workbook_order_and_empty_sheets_give_nothing()
    {
        var builder = new XlsxBuilder();
        builder.AddSheet("甲").Set("A1", "第一張");
        builder.AddSheet("乙");
        builder.AddSheet("丙").Set("A1", "第三張");

        var document = await ParseAsync(builder);

        Assert.Equal(["甲", "丙"], document.Sections.Select(s => s.Location.Sheet));
    }

    [Fact]
    public async Task Inline_strings_and_cells_without_references_are_read()
    {
        var builder = new XlsxBuilder { UseInlineStrings = true, OmitCellReferences = true };
        var sheet = builder.AddSheet("相容");
        sheet.SetRow(0, 0, CellStyle.Plain, "甲", "乙", "丙");
        sheet.SetRow(1, 0, CellStyle.Plain, 1, 2, 3);

        var document = await ParseAsync(builder);

        var section = Assert.Single(document.Sections);
        Assert.Equal("A1:C2", section.Location.CellRange);
        Assert.Contains("<tr><td>甲</td><td>乙</td><td>丙</td></tr>", section.Text);
        Assert.Contains("<tr><td>1</td><td>2</td><td>3</td></tr>", section.Text);
    }

    [Fact]
    public async Task Macro_enabled_workbooks_are_read()
    {
        var builder = new XlsxBuilder { MacroEnabled = true };
        builder.AddSheet("巨集").Set("A1", "內容");

        var document = await ParseAsync(builder, "含巨集.xlsm");

        Assert.Contains("內容", Assert.Single(document.Sections).Text);
    }

    [Fact]
    public async Task Cells_far_apart_on_a_small_sheet_do_not_produce_a_huge_blank_table()
    {
        var builder = new XlsxBuilder();
        builder.AddSheet("稀疏").Set("A1", "左上").Set("Z500", "右下");

        var document = await ParseAsync(builder);

        var section = Assert.Single(document.Sections);
        Assert.Equal("A1:Z500", section.Location.CellRange);
        Assert.True(section.Text.Length < 200, "Blank rows and columns should be left out.");
        Assert.Contains("左上", section.Text);
        Assert.Contains("右下", section.Text);
    }

    [Fact]
    public async Task SmallTableMaxCells_decides_which_way_a_sheet_goes()
    {
        var builder = new XlsxBuilder();
        FillList(builder.AddSheet("清單"), 0, 20);

        var asTable = await ParseAsync(builder, options: new ParserOptions { SmallTableMaxCells = 1000 });
        var asSummary = await ParseAsync(builder, options: new ParserOptions { SmallTableMaxCells = 50, TableSampleRows = 2 });

        Assert.Equal(SectionKind.Table, Assert.Single(asTable.Sections).Kind);
        Assert.Empty(asTable.Tables);
        Assert.Equal(SectionKind.TableSummary, Assert.Single(asSummary.Sections).Kind);
        Assert.Equal(2, Assert.Single(asSummary.Tables).SampleRows.Count);
    }

    [Fact]
    public async Task A_small_table_on_a_busy_sheet_is_its_own_table_section()
    {
        var builder = new XlsxBuilder();
        var sheet = builder.AddSheet("混合");
        FillList(sheet, 0, 300);
        sheet.SetRow(304, 0, CellStyle.Plain, "備註", "請在月底前核對");
        sheet.SetRow(310, 0, CellStyle.Bold, "姓名", "電話");
        sheet.SetRow(311, 0, CellStyle.Plain, "王小明", "0912");
        sheet.SetRow(312, 0, CellStyle.Plain, "李大華", "0922");

        var document = await ParseAsync(builder);

        Assert.Equal([SectionKind.TableSummary, SectionKind.Table, SectionKind.Table], document.Sections.Select(s => s.Kind));
        Assert.Equal("A305:B305", document.Sections[1].Location.CellRange);
        Assert.Equal("A311:B313", document.Sections[2].Location.CellRange);
        Assert.Contains("<thead>", document.Sections[2].Text);
        Assert.Contains("<th>姓名</th>", document.Sections[2].Text);
        Assert.Single(document.Tables);
    }

    [Fact]
    public async Task Embedded_files_and_images_are_collected_per_sheet()
    {
        var builder = new XlsxBuilder();
        builder.AddSheet("附件").Set("A1", "有附件");
        var bytes = AddEmbeddedParts(builder.Build());

        using var stream = new MemoryStream(bytes);
        var context = new ParseContext(stream, "外層.xlsx", new ParserOptions(), ["上層.pptx"]);
        var document = await new Contexo.Core.Parsing.Spreadsheet.SpreadsheetParser().ParseAsync(context, CancellationToken.None);

        var embedded = Assert.Single(document.EmbeddedFiles);
        Assert.EndsWith(".xlsx", embedded.FileName);
        Assert.Equal("附件", embedded.ContainerLocation.Sheet);
        var image = Assert.Single(document.Images);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal("附件", image.Location.Sheet);
        Assert.Equal(["上層.pptx"], Assert.Single(document.Sections).Location.EmbeddedPath);
    }

    [Fact]
    public async Task Output_is_cut_off_at_the_character_limit_with_a_warning()
    {
        var builder = new XlsxBuilder();
        builder.AddSheet("一").Set("A1", new string('字', 500));
        builder.AddSheet("二").Set("A1", new string('字', 500));

        var document = await ParseAsync(builder, options: new ParserOptions { MaxExtractedChars = 800 });

        Assert.Single(document.Sections);
        Assert.Contains("truncated:max-extracted-chars", document.Warnings);
    }

    [Fact]
    public async Task Password_protected_file_is_reported()
    {
        byte[] compoundFile = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[600], .. System.Text.Encoding.Unicode.GetBytes("EncryptedPackage"), .. new byte[100]];

        var ex = await Assert.ThrowsAsync<DocumentParseException>(() => ParseAsync(compoundFile));

        Assert.Equal(DocumentErrorCode.PasswordProtected, ex.Code);
    }

    [Theory]
    [InlineData("not a spreadsheet at all")]
    [InlineData("PK-but-not-a-real-zip-file")]
    public async Task Broken_file_is_reported_as_corrupted(string content)
    {
        var ex = await Assert.ThrowsAsync<DocumentParseException>(() => ParseAsync(System.Text.Encoding.UTF8.GetBytes(content)));

        Assert.Equal(DocumentErrorCode.Corrupted, ex.Code);
    }

    [Fact]
    public async Task Parsing_can_be_cancelled()
    {
        var builder = new XlsxBuilder();
        builder.AddSheet("甲").Set("A1", "x");
        using var stream = new MemoryStream(builder.Build());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Contexo.Core.Parsing.Spreadsheet.SpreadsheetParser().ParseAsync(new ParseContext(stream, "a.xlsx", new ParserOptions()), cts.Token));
    }

    private static byte[] AddEmbeddedParts(byte[] workbook)
    {
        var inner = new XlsxBuilder();
        inner.AddSheet("內嵌").Set("A1", "內嵌內容");
        var innerBytes = inner.Build();

        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

        using var stream = new MemoryStream();
        stream.Write(workbook);
        using (var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(stream, true))
        {
            var sheetPart = document.WorkbookPart!.WorksheetParts.First();
            var embedded = sheetPart.AddNewPart<DocumentFormat.OpenXml.Packaging.EmbeddedPackagePart>("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            using (var target = embedded.GetStream(FileMode.Create, FileAccess.Write))
            {
                target.Write(innerBytes);
            }

            var drawings = sheetPart.AddNewPart<DocumentFormat.OpenXml.Packaging.DrawingsPart>();
            drawings.WorksheetDrawing = new DocumentFormat.OpenXml.Drawing.Spreadsheet.WorksheetDrawing();
            var image = drawings.AddNewPart<DocumentFormat.OpenXml.Packaging.ImagePart>("image/png");
            using (var target = image.GetStream(FileMode.Create, FileAccess.Write))
            {
                target.Write(png);
            }
        }

        return stream.ToArray();
    }
}
