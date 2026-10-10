using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Contexo.CorpusGen.Office;

/// <summary>Cell formats the builder knows. The value is the index into <c>cellXfs</c>.</summary>
internal enum CellStyle
{
    Plain = 0,
    Bold = 1,
    Fill = 2,
    Date = 3,
    DateTime = 4,
    Time = 5,
    Percent = 6,
    Percent2 = 7,
    CustomDate = 8,
    QuotedM = 9,
    Thousands = 10,
    BoldFill = 11,
    CustomPercent = 12,
}

/// <summary>A formula with the value Excel last calculated for it.</summary>
internal sealed record FormulaValue(string Formula, object? Cached);

/// <summary>A cell that shows an error such as #N/A.</summary>
internal sealed record ErrorValue(string Code);

/// <summary>Builds small .xlsx files for tests with the Open XML SDK (rows are streamed, so 100,000-row sheets are practical).</summary>
internal sealed class XlsxBuilder
{
    private readonly List<SheetBuilder> _sheets = [];

    public bool Date1904 { get; set; }

    public bool MacroEnabled { get; set; }

    /// <summary>Write text as inline strings instead of the shared string table.</summary>
    public bool UseInlineStrings { get; set; }

    /// <summary>Leave the <c>r</c> attribute off cells, as some generators do.</summary>
    public bool OmitCellReferences { get; set; }

    public SheetBuilder AddSheet(string name, bool hidden = false)
    {
        var sheet = new SheetBuilder(name, hidden);
        _sheets.Add(sheet);
        return sheet;
    }

    public byte[] Build()
    {
        using var stream = new MemoryStream();
        Build(stream);
        return stream.ToArray();
    }

    public void Build(Stream stream)
    {
        var type = MacroEnabled ? SpreadsheetDocumentType.MacroEnabledWorkbook : SpreadsheetDocumentType.Workbook;
        using var document = SpreadsheetDocument.Create(stream, type);
        var workbookPart = document.AddWorkbookPart();
        var workbook = new Workbook();
        if (Date1904)
        {
            workbook.Append(new WorkbookProperties { Date1904 = true });
        }

        var sheets = new Sheets();
        workbook.Append(sheets);
        workbookPart.Workbook = workbook;

        var strings = new List<string>();
        var stringIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        uint sheetId = 1;
        foreach (var sheet in _sheets)
        {
            var part = workbookPart.AddNewPart<WorksheetPart>();
            WriteSheet(part, sheet, strings, stringIndex);
            var entry = new Sheet { Id = workbookPart.GetIdOfPart(part), SheetId = sheetId++, Name = sheet.Name };
            if (sheet.Hidden)
            {
                entry.State = SheetStateValues.Hidden;
            }

            sheets.Append(entry);
        }

        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = CreateStylesheet();

        if (!UseInlineStrings)
        {
            var sst = workbookPart.AddNewPart<SharedStringTablePart>();
            using var writer = OpenXmlWriter.Create(sst);
            writer.WriteStartElement(new SharedStringTable());
            foreach (var text in strings)
            {
                writer.WriteElement(new SharedStringItem(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
            }

            writer.WriteEndElement();
        }
    }

    private void WriteSheet(WorksheetPart part, SheetBuilder sheet, List<string> strings, Dictionary<string, int> stringIndex)
    {
        using var writer = OpenXmlWriter.Create(part);
        writer.WriteStartElement(new Worksheet());
        writer.WriteStartElement(new SheetData());

        foreach (var (rowIndex, cells) in sheet.Rows.OrderBy(r => r.Key))
        {
            writer.WriteStartElement(new Row { RowIndex = (uint)(rowIndex + 1) });
            foreach (var (column, value, style) in cells.OrderBy(c => c.Column))
            {
                var cell = CreateCell(rowIndex, column, value, style, strings, stringIndex);
                if (cell is not null)
                {
                    writer.WriteElement(cell);
                }
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();

        if (sheet.Merges.Count > 0)
        {
            writer.WriteStartElement(new MergeCells());
            foreach (var merge in sheet.Merges)
            {
                writer.WriteElement(new MergeCell { Reference = merge });
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private Cell? CreateCell(int row, int column, object? value, CellStyle style, List<string> strings, Dictionary<string, int> stringIndex)
    {
        if (value is null)
        {
            return OmitCellReferences ? null : new Cell { CellReference = Ref(row, column), StyleIndex = (uint)style };
        }

        var cell = new Cell { StyleIndex = (uint)style };
        if (!OmitCellReferences)
        {
            cell.CellReference = Ref(row, column);
        }

        if (value is FormulaValue formula)
        {
            Fill(cell, formula.Cached, strings, stringIndex, isFormula: true);
            cell.CellFormula = new CellFormula(formula.Formula);
            return cell;
        }

        Fill(cell, value, strings, stringIndex, isFormula: false);
        return cell;
    }

    private void Fill(Cell cell, object? value, List<string> strings, Dictionary<string, int> stringIndex, bool isFormula)
    {
        switch (value)
        {
            case null:
                break;
            case string text when isFormula:
                cell.DataType = CellValues.String;
                cell.CellValue = new CellValue(text);
                break;
            case string text when UseInlineStrings:
                cell.DataType = CellValues.InlineString;
                cell.InlineString = new InlineString(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
                break;
            case string text:
                if (!stringIndex.TryGetValue(text, out var index))
                {
                    index = strings.Count;
                    strings.Add(text);
                    stringIndex[text] = index;
                }

                cell.DataType = CellValues.SharedString;
                cell.CellValue = new CellValue(index.ToString(CultureInfo.InvariantCulture));
                break;
            case bool flag:
                cell.DataType = CellValues.Boolean;
                cell.CellValue = new CellValue(flag ? "1" : "0");
                break;
            case ErrorValue error:
                cell.DataType = CellValues.Error;
                cell.CellValue = new CellValue(error.Code);
                break;
            case DateTime date:
                var serial = date.ToOADate() - (Date1904 ? 1462 : 0);
                cell.CellValue = new CellValue(serial.ToString("R", CultureInfo.InvariantCulture));
                break;
            case IFormattable number:
                cell.CellValue = new CellValue(number.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                throw new NotSupportedException(value.GetType().Name);
        }
    }

    private static string Ref(int row, int column)
    {
        var letters = string.Empty;
        for (var n = column + 1; n > 0; n = (n - 1) / 26)
        {
            letters = (char)('A' + ((n - 1) % 26)) + letters;
        }

        return letters + (row + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static Stylesheet CreateStylesheet()
    {
        var numberingFormats = new NumberingFormats(
            new NumberingFormat { NumberFormatId = 164, FormatCode = "yyyy\"年\"mm\"月\"dd\"日\"" },
            new NumberingFormat { NumberFormatId = 165, FormatCode = "0.0\" m\"" },
            new NumberingFormat { NumberFormatId = 166, FormatCode = "0.0%" });

        var fonts = new Fonts(new Font(), new Font(new Bold()));
        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFFFFF00" }) { PatternType = PatternValues.Solid }));
        var borders = new Borders(new Border());

        static CellFormat Xf(uint numFmt = 0, uint font = 0, uint fill = 0) => new()
        {
            NumberFormatId = numFmt,
            FontId = font,
            FillId = fill,
            BorderId = 0,
        };

        var cellFormats = new CellFormats(
            Xf(),
            Xf(font: 1),
            Xf(fill: 2),
            Xf(numFmt: 14),
            Xf(numFmt: 22),
            Xf(numFmt: 21),
            Xf(numFmt: 9),
            Xf(numFmt: 10),
            Xf(numFmt: 164),
            Xf(numFmt: 165),
            Xf(numFmt: 3),
            Xf(font: 1, fill: 2),
            Xf(numFmt: 166));

        return new Stylesheet(numberingFormats, fonts, fills, borders, cellFormats);
    }
}

internal sealed class SheetBuilder(string name, bool hidden)
{
    public string Name { get; } = name;

    public bool Hidden { get; } = hidden;

    public Dictionary<int, List<(int Column, object? Value, CellStyle Style)>> Rows { get; } = [];

    public List<string> Merges { get; } = [];

    /// <summary>Sets a cell by A1 reference, e.g. <c>Set("B3", 12.5)</c>.</summary>
    public SheetBuilder Set(string reference, object? value, CellStyle style = CellStyle.Plain)
    {
        var (row, column) = Parse(reference);
        return Set(row, column, value, style);
    }

    /// <summary>Sets a cell by 0-based row and column.</summary>
    public SheetBuilder Set(int row, int column, object? value, CellStyle style = CellStyle.Plain)
    {
        if (!Rows.TryGetValue(row, out var cells))
        {
            Rows[row] = cells = [];
        }

        cells.RemoveAll(c => c.Column == column);
        cells.Add((column, value, style));
        return this;
    }

    /// <summary>Writes consecutive cells of one row starting at <paramref name="startColumn"/>.</summary>
    public SheetBuilder SetRow(int row, int startColumn, CellStyle style, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            Set(row, startColumn + i, values[i], style);
        }

        return this;
    }

    public SheetBuilder Merge(string range)
    {
        Merges.Add(range);
        return this;
    }

    public static (int Row, int Column) Parse(string reference)
    {
        var i = 0;
        var column = 0;
        while (i < reference.Length && char.IsAsciiLetter(reference[i]))
        {
            column = (column * 26) + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
            i++;
        }

        return (int.Parse(reference[i..], CultureInfo.InvariantCulture) - 1, column - 1);
    }
}
