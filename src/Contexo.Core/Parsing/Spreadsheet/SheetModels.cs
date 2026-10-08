namespace Contexo.Core.Parsing.Spreadsheet;

internal enum CellKind
{
    Empty = 0,
    Text,
    Number,
    Date,
    Bool,
    Error,
}

/// <summary>One non-empty cell as shown to the user.</summary>
/// <param name="Emphasis">Bold or filled with a colour; a hint that the cell belongs to a header.</param>
internal readonly record struct SheetCell(string? Text, CellKind Kind, bool Emphasis)
{
    public bool IsEmpty => Kind == CellKind.Empty;
}

/// <summary>Streams the non-empty cells of one worksheet (or of a CSV file).</summary>
internal interface ISheetCells
{
    /// <summary>
    /// Calls <paramref name="visit"/> for each non-empty cell in file order (row by row). Stops when it returns false.
    /// With <paramref name="format"/> false the text is the raw stored value (cheaper); with true it is the display string.
    /// </summary>
    void Walk(bool format, Func<int, int, SheetCell, bool> visit, CancellationToken cancellationToken);

    /// <summary>All merged ranges of the sheet. Can cost a pass over the sheet.</summary>
    IReadOnlyList<CellRect> ReadMerges();
}

/// <summary>The cells of one rectangle, collected during a shared pass.</summary>
internal sealed class RectData(CellRect rect)
{
    private readonly Dictionary<long, SheetCell> _cells = [];

    public CellRect Rect { get; } = rect;

    public IEnumerable<(int Row, int Column, SheetCell Cell)> Cells =>
        _cells.Select(pair => ((int)(pair.Key >> 32), (int)(pair.Key & 0xFFFFFFFF), pair.Value));

    public void Set(int row, int column, SheetCell cell) => _cells[Key(row, column)] = cell;

    public SheetCell Get(int row, int column) => _cells.TryGetValue(Key(row, column), out var cell) ? cell : default;

    public SheetCell[] GetRow(int row, int left, int width)
    {
        var result = new SheetCell[width];
        for (var i = 0; i < width; i++)
        {
            result[i] = Get(row, left + i);
        }

        return result;
    }

    private static long Key(int row, int column) => ((long)row << 32) | (uint)column;
}
