using Contexo.Core.Abstractions;

namespace Contexo.Core.Parsing.Spreadsheet;

/// <summary>Merged ranges of a sheet, searchable by the cell they start at.</summary>
internal sealed class MergeIndex
{
    private readonly CellRect[] _merges;

    public MergeIndex(IReadOnlyList<CellRect> merges)
    {
        _merges = [.. merges];
        Array.Sort(_merges, static (a, b) => a.Top != b.Top ? a.Top.CompareTo(b.Top) : a.Left.CompareTo(b.Left));
    }

    /// <summary>Merges whose top-left cell lies inside <paramref name="rect"/>.</summary>
    public List<CellRect> AnchoredIn(CellRect rect)
    {
        var result = new List<CellRect>();
        var low = 0;
        var high = _merges.Length;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_merges[mid].Top < rect.Top)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        for (var i = low; i < _merges.Length && _merges[i].Top <= rect.Bottom; i++)
        {
            if (_merges[i].Left >= rect.Left && _merges[i].Left <= rect.Right)
            {
                result.Add(_merges[i]);
            }
        }

        return result;
    }
}

/// <summary>Turns a rectangle of cells into a <see cref="TableModel"/> (merged cells become colspan / rowspan).</summary>
internal static class SmallTableBuilder
{
    public static TableModel Build(RectData data, SheetScan scan, MergeIndex merges, CellRect rect, int headerRows, string? caption, int smallTableMaxCells)
    {
        // A sheet with a few cells far apart would render as a huge, mostly blank table; leave out rows and columns that hold nothing.
        var compress = rect.Area > 4L * Math.Max(smallTableMaxCells, 1);
        int[] keptRows;
        int[] keptColumns;
        if (compress)
        {
            var rows = new List<int>();
            var used = new bool[rect.ColumnCount];
            for (var k = scan.LowerBound(rect.Top); k < scan.RowNumbers.Length && scan.RowNumbers[k] <= rect.Bottom; k++)
            {
                rows.Add(scan.RowNumbers[k]);
                var runs = scan.Runs[k];
                for (var i = 0; i < runs.Length; i += 2)
                {
                    for (var c = Math.Max(runs[i], rect.Left); c <= Math.Min(runs[i + 1], rect.Right); c++)
                    {
                        used[c - rect.Left] = true;
                    }
                }
            }

            keptRows = [.. rows];
            keptColumns = [.. Enumerable.Range(0, used.Length).Where(i => used[i]).Select(i => i + rect.Left)];
        }
        else
        {
            keptRows = [.. Enumerable.Range(rect.Top, rect.RowCount)];
            keptColumns = [.. Enumerable.Range(rect.Left, rect.ColumnCount)];
        }

        var anchored = merges.AnchoredIn(rect).ToDictionary(m => (m.Top, m.Left));
        var cells = new List<TableCell>();
        foreach (var (row, column, cell) in data.Cells)
        {
            if (!rect.Contains(row, column) || cell.IsEmpty)
            {
                continue;
            }

            var r = Array.BinarySearch(keptRows, row);
            var c = Array.BinarySearch(keptColumns, column);
            if (r < 0 || c < 0)
            {
                continue;
            }

            var rowSpan = 1;
            var colSpan = 1;
            if (anchored.TryGetValue((row, column), out var merge))
            {
                rowSpan = Math.Max(1, LowerBound(keptRows, Math.Min(merge.Bottom, rect.Bottom) + 1) - r);
                colSpan = Math.Max(1, LowerBound(keptColumns, Math.Min(merge.Right, rect.Right) + 1) - c);
            }

            cells.Add(new TableCell(r, c, cell.Text ?? string.Empty, rowSpan, colSpan));
        }

        cells.Sort(static (a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Column.CompareTo(b.Column));
        return new TableModel(keptRows.Length, keptColumns.Length, cells, Math.Clamp(headerRows, 0, keptRows.Length), caption);
    }

    /// <summary>Index of the first element that is at least <paramref name="value"/> in an ascending array.</summary>
    private static int LowerBound(int[] sorted, int value)
    {
        var index = Array.BinarySearch(sorted, value);
        return index >= 0 ? index : ~index;
    }
}
