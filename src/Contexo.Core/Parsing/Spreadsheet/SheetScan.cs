namespace Contexo.Core.Parsing.Spreadsheet;

/// <summary>
/// Which cells of a sheet are non-empty, without their values. This is all region detection needs, and it stays small
/// enough (a few integers per row) to keep for a sheet with hundreds of thousands of rows.
/// </summary>
internal sealed class SheetScan
{
    public SheetScan(int[] rowNumbers, int[][] columns, int[][] runs, IReadOnlyList<CellRect> merges, long realCells, long gridCells, CellRect? bounds)
    {
        RowNumbers = rowNumbers;
        Columns = columns;
        Runs = runs;
        Merges = merges;
        RealCellCount = realCells;
        GridCellCount = gridCells;
        Bounds = bounds;
    }

    /// <summary>Rows that contain at least one grid cell, ascending.</summary>
    public int[] RowNumbers { get; }

    /// <summary>Columns of the cells that really hold a value, per row (parallel to <see cref="RowNumbers"/>).</summary>
    public int[][] Columns { get; }

    /// <summary>Runs of horizontally adjacent grid cells per row, as flattened (start, end) pairs. Merged areas count as filled.</summary>
    public int[][] Runs { get; }

    /// <summary>Every merged range of the sheet (area greater than one cell).</summary>
    public IReadOnlyList<CellRect> Merges { get; }

    /// <summary>Cells that hold a value.</summary>
    public long RealCellCount { get; }

    /// <summary>Cells that count as non-empty: value cells plus the other cells of merged areas.</summary>
    public long GridCellCount { get; }

    /// <summary>Smallest rectangle around all grid cells; null when the sheet is empty.</summary>
    public CellRect? Bounds { get; }

    /// <summary>Index into <see cref="RowNumbers"/> of the first row that is at least <paramref name="row"/>.</summary>
    public int LowerBound(int row)
    {
        var index = Array.BinarySearch(RowNumbers, row);
        return index >= 0 ? index : ~index;
    }
}

internal static class SheetScanner
{
    public static SheetScan Scan(ISheetCells cells, CancellationToken cancellationToken)
    {
        var segmentRows = new List<int>();
        var segmentStarts = new List<int>();
        var flat = new List<int>();
        var currentRow = -1;
        var needsGrouping = false;

        cells.Walk(false, (row, column, _) =>
        {
            if (row != currentRow)
            {
                if (row < currentRow)
                {
                    needsGrouping = true;
                }

                segmentRows.Add(row);
                segmentStarts.Add(flat.Count);
                currentRow = row;
            }

            flat.Add(column);
            return true;
        }, cancellationToken);

        var (rowNumbers, columns) = BuildRows(segmentRows, segmentStarts, flat, needsGrouping);
        var merges = cells.ReadMerges();
        return Build(rowNumbers, columns, merges);
    }

    private static (int[] Rows, int[][] Columns) BuildRows(List<int> segmentRows, List<int> segmentStarts, List<int> flat, bool needsGrouping)
    {
        if (!needsGrouping)
        {
            for (var i = 1; i < segmentRows.Count; i++)
            {
                if (segmentRows[i] == segmentRows[i - 1])
                {
                    needsGrouping = true;
                    break;
                }
            }
        }

        if (!needsGrouping)
        {
            var rows = segmentRows.ToArray();
            var columns = new int[rows.Length][];
            for (var i = 0; i < rows.Length; i++)
            {
                var start = segmentStarts[i];
                var end = i + 1 < rows.Length ? segmentStarts[i + 1] : flat.Count;
                columns[i] = SortedDistinct(flat.GetRange(start, end - start));
            }

            return (rows, columns);
        }

        var grouped = new SortedDictionary<int, List<int>>();
        for (var i = 0; i < segmentRows.Count; i++)
        {
            var start = segmentStarts[i];
            var end = i + 1 < segmentRows.Count ? segmentStarts[i + 1] : flat.Count;
            if (!grouped.TryGetValue(segmentRows[i], out var list))
            {
                grouped[segmentRows[i]] = list = [];
            }

            list.AddRange(flat.GetRange(start, end - start));
        }

        return ([.. grouped.Keys], [.. grouped.Values.Select(SortedDistinct)]);
    }

    private static int[] SortedDistinct(List<int> values)
    {
        var sorted = true;
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] <= values[i - 1])
            {
                sorted = false;
                break;
            }
        }

        if (sorted)
        {
            return values.ToArray();
        }

        return [.. values.Distinct().Order()];
    }

    private static SheetScan Build(int[] realRows, int[][] realColumns, IReadOnlyList<CellRect> allMerges)
    {
        if (realRows.Length == 0)
        {
            return new SheetScan([], [], [], allMerges, 0, 0, null);
        }

        var maxRow = realRows[^1];
        var maxColumn = 0;
        foreach (var cols in realColumns)
        {
            maxColumn = Math.Max(maxColumn, cols[^1]);
        }

        // A merged area counts as filled only when its top-left cell holds a value (blank merged layout areas stay empty).
        var fill = new SortedDictionary<int, List<(int Start, int End)>>();
        foreach (var merge in allMerges)
        {
            if (!HasValue(realRows, realColumns, merge.Top, merge.Left))
            {
                continue;
            }

            var bottom = Math.Min(merge.Bottom, maxRow);
            var right = Math.Min(merge.Right, maxColumn);
            for (var row = merge.Top; row <= bottom; row++)
            {
                if (!fill.TryGetValue(row, out var list))
                {
                    fill[row] = list = [];
                }

                list.Add((merge.Left, right));
            }
        }

        var rows = new List<int>(realRows.Length);
        var columns = new List<int[]>(realRows.Length);
        var runs = new List<int[]>(realRows.Length);
        long realCells = 0;
        long gridCells = 0;
        int top = int.MaxValue, left = int.MaxValue, bottomMost = int.MinValue, right2 = int.MinValue;

        var scratch = new List<(int Start, int End)>();
        using var fillEnumerator = fill.GetEnumerator();
        var hasFill = fillEnumerator.MoveNext();
        var index = 0;
        while (index < realRows.Length || hasFill)
        {
            int row;
            int[] cols;
            List<(int Start, int End)>? extra = null;
            if (index < realRows.Length && (!hasFill || realRows[index] <= fillEnumerator.Current.Key))
            {
                row = realRows[index];
                cols = realColumns[index];
                index++;
                if (hasFill && fillEnumerator.Current.Key == row)
                {
                    extra = fillEnumerator.Current.Value;
                    hasFill = fillEnumerator.MoveNext();
                }
            }
            else
            {
                row = fillEnumerator.Current.Key;
                cols = [];
                extra = fillEnumerator.Current.Value;
                hasFill = fillEnumerator.MoveNext();
            }

            scratch.Clear();
            if (cols.Length > 0)
            {
                var start = cols[0];
                var previous = start;
                for (var i = 1; i < cols.Length; i++)
                {
                    if (cols[i] == previous + 1)
                    {
                        previous = cols[i];
                        continue;
                    }

                    scratch.Add((start, previous));
                    start = previous = cols[i];
                }

                scratch.Add((start, previous));
            }

            if (extra is not null)
            {
                scratch.AddRange(extra);
                scratch.Sort((a, b) => a.Start.CompareTo(b.Start));
            }

            var rowRuns = new List<int>(scratch.Count * 2);
            var current = scratch[0];
            for (var i = 1; i < scratch.Count; i++)
            {
                if (scratch[i].Start <= current.End + 1)
                {
                    current = (current.Start, Math.Max(current.End, scratch[i].End));
                }
                else
                {
                    rowRuns.Add(current.Start);
                    rowRuns.Add(current.End);
                    gridCells += current.End - current.Start + 1;
                    current = scratch[i];
                }
            }

            rowRuns.Add(current.Start);
            rowRuns.Add(current.End);
            gridCells += current.End - current.Start + 1;

            rows.Add(row);
            columns.Add(cols);
            runs.Add(rowRuns.ToArray());
            realCells += cols.Length;
            top = Math.Min(top, row);
            bottomMost = Math.Max(bottomMost, row);
            left = Math.Min(left, rowRuns[0]);
            right2 = Math.Max(right2, rowRuns[^1]);
        }

        return new SheetScan([.. rows], [.. columns], [.. runs], allMerges, realCells, gridCells, new CellRect(top, left, bottomMost, right2));
    }

    private static bool HasValue(int[] rows, int[][] columns, int row, int column)
    {
        var index = Array.BinarySearch(rows, row);
        return index >= 0 && Array.BinarySearch(columns[index], column) >= 0;
    }
}
