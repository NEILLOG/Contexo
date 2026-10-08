using Contexo.Core.Abstractions;
using DocumentFormat.OpenXml.Wordprocessing;
using ModelCell = Contexo.Core.Abstractions.TableCell;
using WordCell = DocumentFormat.OpenXml.Wordprocessing.TableCell;

namespace Contexo.Core.Parsing.Word;

/// <summary>Helpers for block level Word structures: table of contents controls and tables.</summary>
internal static class WordBlocks
{
    /// <summary>True for a content control that wraps an automatic table of contents.</summary>
    public static bool IsTableOfContents(SdtBlock sdt)
    {
        var properties = sdt.SdtProperties;
        if (properties is null)
        {
            return false;
        }

        foreach (var gallery in properties.Descendants<DocPartGallery>())
        {
            if (string.Equals(gallery.Val?.Value, "Table of Contents", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Rows of a table, without rows that were deleted by a tracked change.</summary>
    public static IEnumerable<TableRow> EnumerateRows(Table table)
    {
        foreach (var row in table.Elements<TableRow>())
        {
            if (row.TableRowProperties?.Elements<Deleted>().Any() == true)
            {
                continue;
            }

            yield return row;
        }
    }

    /// <summary>Cells of a row, including cells wrapped in content controls.</summary>
    public static IEnumerable<WordCell> EnumerateCells(TableRow row)
    {
        foreach (var child in row.ChildElements)
        {
            switch (child)
            {
                case WordCell cell:
                    yield return cell;
                    break;

                case SdtCell sdt when sdt.SdtContentCell is { } content:
                    foreach (var inner in content.Elements<WordCell>())
                    {
                        yield return inner;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Converts a Word table to a <see cref="TableModel"/>: <c>gridSpan</c> becomes ColSpan, <c>vMerge</c> (restart + continuations) becomes RowSpan.
    /// Returns null for a table without rows or columns.
    /// </summary>
    public static TableModel? ReadTable(Table table, WordContext context)
    {
        var rows = EnumerateRows(table).ToList();
        if (rows.Count == 0)
        {
            return null;
        }

        var cells = new List<MutableCell>();
        var openMerges = new Dictionary<int, MutableCell>();
        var columnCount = 0;

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var column = Math.Max(rows[rowIndex].TableRowProperties?.GetFirstChild<GridBefore>()?.Val?.Value ?? 0, 0);
            foreach (var cell in EnumerateCells(rows[rowIndex]))
            {
                var properties = cell.TableCellProperties;
                var span = Math.Max(properties?.GridSpan?.Val?.Value ?? 1, 1);
                var merge = properties?.VerticalMerge;

                var continued = false;
                if (merge is not null && merge.Val?.Value != MergedCellValues.Restart
                    && openMerges.TryGetValue(column, out var origin)
                    && origin.Row + origin.RowSpan == rowIndex)
                {
                    origin.RowSpan++;
                    continued = true;
                }

                if (!continued)
                {
                    var text = string.Join('\n', new WordTextExtractor(context).ReadLines(cell));
                    var created = new MutableCell(rowIndex, column, text.Trim(), span);
                    cells.Add(created);
                    if (merge is not null && merge.Val?.Value == MergedCellValues.Restart)
                    {
                        openMerges[column] = created;
                    }
                    else
                    {
                        openMerges.Remove(column);
                    }
                }

                column += span;
            }

            columnCount = Math.Max(columnCount, column);
        }

        if (columnCount == 0)
        {
            return null;
        }

        var headerRows = 0;
        foreach (var row in rows)
        {
            var header = row.TableRowProperties?.Elements<TableHeader>().FirstOrDefault();
            if (header is null || header.Val?.Value == OnOffOnlyValues.Off)
            {
                break;
            }

            headerRows++;
        }

        headerRows = Math.Clamp(headerRows == 0 ? 1 : headerRows, 1, rows.Count);

        var model = cells.Select(static c => new ModelCell(c.Row, c.Column, c.Text, c.RowSpan, c.ColSpan)).ToList();
        return new TableModel(rows.Count, columnCount, model, headerRows);
    }

    private sealed class MutableCell(int row, int column, string text, int colSpan)
    {
        public int Row { get; } = row;

        public int Column { get; } = column;

        public string Text { get; } = text;

        public int ColSpan { get; } = colSpan;

        public int RowSpan { get; set; } = 1;
    }
}
