namespace Contexo.Core.Parsing.Spreadsheet;

/// <summary>Reads display values out of a sheet: a handful of small rectangles in one pass, or one big rectangle row by row.</summary>
internal static class SheetReading
{
    /// <summary>
    /// Collects the cells of several rectangles with a single pass over the sheet. The pass stops after the lowest rectangle,
    /// so reading the top of a 100,000-row table touches only its first rows.
    /// </summary>
    public static RectData[] ReadRects(ISheetCells cells, SheetScan scan, IReadOnlyList<CellRect> rects, CancellationToken cancellationToken)
    {
        var result = new RectData[rects.Count];
        var rectsByRow = new Dictionary<int, List<int>>();
        var lastRow = -1;
        for (var i = 0; i < rects.Count; i++)
        {
            result[i] = new RectData(rects[i]);
            for (var k = scan.LowerBound(rects[i].Top); k < scan.RowNumbers.Length && scan.RowNumbers[k] <= rects[i].Bottom; k++)
            {
                var row = scan.RowNumbers[k];
                if (!rectsByRow.TryGetValue(row, out var list))
                {
                    rectsByRow[row] = list = [];
                }

                list.Add(i);
                lastRow = Math.Max(lastRow, row);
            }
        }

        if (rectsByRow.Count == 0)
        {
            return result;
        }

        cells.Walk(true, (row, column, cell) =>
        {
            if (row > lastRow)
            {
                return false;
            }

            if (rectsByRow.TryGetValue(row, out var list))
            {
                foreach (var index in list)
                {
                    if (rects[index].Contains(row, column))
                    {
                        result[index].Set(row, column, cell);
                    }
                }
            }

            return true;
        }, cancellationToken);

        return result;
    }

    /// <summary>
    /// Streams the rows of <paramref name="rect"/> top to bottom, one array per row (width of the rectangle, empty cells are default).
    /// Rows without any cell are reported too, up to the bottom of the rectangle once any row at or below its top exists.
    /// </summary>
    /// <returns>
    /// <c>LastRow</c>: the highest row inside the rectangle that holds a cell (-1 when none).
    /// <c>ReachedEnd</c>: the sheet ended inside or before the rectangle, so rows below <c>LastRow</c> do not exist.
    /// </returns>
    public static (int LastRow, bool ReachedEnd) ReadRows(ISheetCells cells, CellRect rect, Action<int, SheetCell[]> onRow, CancellationToken cancellationToken)
    {
        var width = rect.ColumnCount;
        var nextRow = rect.Top;
        SheetCell[]? current = null;
        var currentRow = -1;
        var lastRow = -1;
        var stopped = false;

        void Flush()
        {
            if (current is not null)
            {
                onRow(currentRow, current);
                current = null;
                nextRow = currentRow + 1;
            }
        }

        cells.Walk(true, (row, column, cell) =>
        {
            if (row < rect.Top)
            {
                return true;
            }

            if (row > rect.Bottom)
            {
                stopped = true;
                return false;
            }

            if (row != currentRow || current is null)
            {
                Flush();
                for (; nextRow < row; nextRow++)
                {
                    onRow(nextRow, new SheetCell[width]);
                }

                current = new SheetCell[width];
                currentRow = row;
                lastRow = row;
            }

            if (column >= rect.Left && column <= rect.Right)
            {
                current[column - rect.Left] = cell;
            }

            return true;
        }, cancellationToken);

        Flush();
        if (lastRow >= 0)
        {
            for (; nextRow <= rect.Bottom; nextRow++)
            {
                onRow(nextRow, new SheetCell[width]);
            }
        }

        return (lastRow, !stopped);
    }

    /// <summary>The cells of the given rows of a rectangle fetched earlier, one array per row.</summary>
    public static SheetCell[][] Slice(RectData data, int top, int rowCount, int left, int width)
    {
        var rows = new SheetCell[rowCount][];
        for (var i = 0; i < rowCount; i++)
        {
            rows[i] = data.GetRow(top + i, left, width);
        }

        return rows;
    }
}
