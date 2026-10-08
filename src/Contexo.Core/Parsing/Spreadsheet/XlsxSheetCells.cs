using System.Globalization;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml.Packaging;

namespace Contexo.Core.Parsing.Spreadsheet;

/// <summary>
/// Reads the cells of one worksheet with a forward-only XML reader, so a 100,000-row sheet is never loaded as a DOM.
/// </summary>
internal sealed class XlsxSheetCells(XlsxWorkbook workbook, WorksheetPart part) : ISheetCells
{
    internal static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    public void Walk(bool format, Func<int, int, SheetCell, bool> visit, CancellationToken cancellationToken)
    {
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        using var xml = XmlReader.Create(stream, ReaderSettings);

        xml.MoveToContent();
        while (!xml.EOF)
        {
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "sheetData" && !ReadSheetData(xml, format, visit, cancellationToken))
            {
                return;
            }

            xml.Read();
        }
    }

    public IReadOnlyList<CellRect> ReadMerges()
    {
        var merges = new List<CellRect>();
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        using var xml = XmlReader.Create(stream, ReaderSettings);

        xml.MoveToContent();
        while (!xml.EOF)
        {
            if (xml.NodeType == XmlNodeType.Element)
            {
                if (xml.LocalName == "sheetData")
                {
                    xml.Skip();
                    continue;
                }

                if (xml.LocalName == "mergeCell" && CellRect.TryParse(xml.GetAttribute("ref"), out var rect) && rect.Area > 1)
                {
                    merges.Add(rect);
                }
            }

            xml.Read();
        }

        return merges;
    }

    /// <summary>Concatenates the text runs of a <c>si</c> / <c>is</c> element, leaving out phonetic guides. Leaves the reader on the element's end.</summary>
    internal static string ReadRichText(XmlReader xml)
    {
        var builder = new StringBuilder();
        using (var sub = xml.ReadSubtree())
        {
            sub.Read();
            var advance = true;
            while (advance ? sub.Read() : !sub.EOF)
            {
                advance = true;
                if (sub.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (sub.LocalName == "rPh")
                {
                    sub.Skip();
                    advance = false;
                }
                else if (sub.LocalName == "t" && !sub.IsEmptyElement && sub.Read() && IsTextNode(sub.NodeType))
                {
                    builder.Append(sub.Value);
                }
            }
        }

        return builder.ToString();
    }

    private static bool IsTextNode(XmlNodeType type) =>
        type is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace;

    /// <summary>Returns false when the visitor asked to stop.</summary>
    private bool ReadSheetData(XmlReader xml, bool format, Func<int, int, SheetCell, bool> visit, CancellationToken cancellationToken)
    {
        if (xml.IsEmptyElement)
        {
            return true;
        }

        var depth = xml.Depth;
        var previousRow = -1;
        var rowsSeen = 0;
        while (xml.Read() && xml.Depth > depth)
        {
            if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "row")
            {
                continue;
            }

            var rowIndex = previousRow + 1;
            if (int.TryParse(xml.GetAttribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var declared) && declared >= 1)
            {
                rowIndex = declared - 1;
            }

            previousRow = rowIndex;
            if ((++rowsSeen & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (xml.IsEmptyElement)
            {
                continue;
            }

            var rowDepth = xml.Depth;
            var previousColumn = -1;
            while (xml.Read() && xml.Depth > rowDepth)
            {
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "c"
                    && !ReadCell(xml, rowIndex, ref previousColumn, format, visit))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool ReadCell(XmlReader xml, int rowIndex, ref int previousColumn, bool format, Func<int, int, SheetCell, bool> visit)
    {
        var column = previousColumn + 1;
        if (CellAddress.TryParseCell(xml.GetAttribute("r"), out var declaredRow, out var declaredColumn))
        {
            rowIndex = declaredRow;
            column = declaredColumn;
        }

        previousColumn = column;
        if (xml.IsEmptyElement)
        {
            return true;
        }

        var type = xml.GetAttribute("t");
        var styleIndex = int.TryParse(xml.GetAttribute("s"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;

        string? value = null;
        string? inline = null;
        var depth = xml.Depth;
        while (xml.Read() && xml.Depth > depth)
        {
            if (xml.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (xml.LocalName == "v" && value is null && !xml.IsEmptyElement)
            {
                value = xml.Read() && IsTextNode(xml.NodeType) ? xml.Value : string.Empty;
            }
            else if (xml.LocalName == "is")
            {
                inline = ReadRichText(xml);
            }
        }

        var cell = Interpret(type, styleIndex, value, inline, format);
        return cell.IsEmpty || visit(rowIndex, column, cell);
    }

    private SheetCell Interpret(string? type, int styleIndex, string? value, string? inline, bool format)
    {
        var style = workbook.Styles.Get(styleIndex);
        switch (type)
        {
            case "s":
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    var table = workbook.SharedStrings;
                    if ((uint)index < (uint)table.Length)
                    {
                        return Text(table[index], style);
                    }
                }

                return default;
            case "str":
                return Text(value, style);
            case "inlineStr":
                return Text(inline ?? value, style);
            case "b":
                return string.IsNullOrWhiteSpace(value)
                    ? default
                    : new SheetCell(value.Trim() == "1" ? "TRUE" : "FALSE", CellKind.Bool, style.Emphasis);
            case "e":
                return string.IsNullOrWhiteSpace(value) ? default : new SheetCell(value.Trim(), CellKind.Error, style.Emphasis);
            case "d":
                return string.IsNullOrWhiteSpace(value) ? default : new SheetCell(value.Trim(), CellKind.Date, style.Emphasis);
            default:
                if (string.IsNullOrWhiteSpace(value))
                {
                    return default;
                }

                var kind = style.Format == NumberFormatKind.Date ? CellKind.Date : CellKind.Number;
                var text = format ? NumberFormatClassifier.Format(value.Trim(), style, workbook.Date1904) : value;
                return new SheetCell(text, kind, style.Emphasis);
        }
    }

    private static SheetCell Text(string? text, in StyleInfo style)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        return new SheetCell(text.Trim(), CellKind.Text, style.Emphasis);
    }
}
