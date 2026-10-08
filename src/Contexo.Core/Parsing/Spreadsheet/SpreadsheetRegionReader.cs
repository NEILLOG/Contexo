using Contexo.Core.Abstractions;

namespace Contexo.Core.Parsing.Spreadsheet;

/// <summary>
/// Reads a registered table back from its file for SQL queries. Column names come from the same
/// <see cref="HeaderDetector"/> code as at parse time, so they match <see cref="SpreadsheetTable.Columns"/>.
/// </summary>
internal sealed class SpreadsheetRegionReader : ISpreadsheetRegionReader
{
    public Task<SpreadsheetRegion> ReadAsync(string filePath, string sheet, string cellRange, int headerRowCount, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(sheet);
        return Task.Run(() => Read(filePath, sheet, cellRange, headerRowCount, cancellationToken), cancellationToken);
    }

    private static SpreadsheetRegion Read(string filePath, string sheet, string cellRange, int headerRowCount, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("找不到檔案：" + Path.GetFileName(filePath), filePath);
        }

        if (!CellRect.TryParse(cellRange, out var rect))
        {
            throw new InvalidOperationException($"儲存格範圍「{cellRange}」格式不正確。");
        }

        if (headerRowCount < 0 || headerRowCount > rect.RowCount)
        {
            throw new InvalidOperationException($"表頭列數 {headerRowCount} 超出範圍「{cellRange}」。");
        }

        // The user may have the file open in Excel, so allow other readers and writers.
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (string.Equals(Path.GetExtension(filePath), ".csv", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(sheet, CsvSheetCells.SheetName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"CSV 檔只有一個工作表「{CsvSheetCells.SheetName}」，找不到「{sheet}」。");
            }

            return ReadRegion(CsvSheetCells.Load(stream), sheet, rect, cellRange, headerRowCount, cancellationToken);
        }

        using var workbook = XlsxWorkbook.Open(stream);
        var match = workbook.Sheets.FirstOrDefault(s => string.Equals(s.Name, sheet, StringComparison.Ordinal))
                    ?? workbook.Sheets.FirstOrDefault(s => string.Equals(s.Name, sheet, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"檔案中找不到工作表「{sheet}」。");

        return ReadRegion(new XlsxSheetCells(workbook, match.Part), sheet, rect, cellRange, headerRowCount, cancellationToken);
    }

    private static SpreadsheetRegion ReadRegion(ISheetCells cells, string sheet, CellRect rect, string cellRange, int headerRowCount, CancellationToken cancellationToken)
    {
        var headerRows = new List<SheetCell[]>();
        var rows = new List<IReadOnlyList<string?>>();

        var (lastRow, reachedEnd) = SheetReading.ReadRows(cells, rect, (row, values) =>
        {
            if (row - rect.Top < headerRowCount)
            {
                headerRows.Add(values);
                return;
            }

            var texts = new string?[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                texts[i] = values[i].IsEmpty ? null : values[i].Text;
            }

            rows.Add(texts);
        }, cancellationToken);

        if (lastRow < 0 || (reachedEnd && lastRow < rect.Bottom))
        {
            throw new InvalidOperationException($"範圍「{cellRange}」超出工作表「{sheet}」現有的資料。");
        }

        var merges = headerRowCount == 0
            ? []
            : new MergeIndex(cells.ReadMerges()).AnchoredIn(new CellRect(rect.Top, rect.Left, rect.Top + headerRowCount - 1, rect.Right));
        var columns = HeaderDetector.BuildColumnNames([.. headerRows], rect.ColumnCount, rect.Top, rect.Left, merges);
        return new SpreadsheetRegion(columns, rows);
    }
}
