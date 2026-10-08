using System.Globalization;

namespace Contexo.Core.Parsing.Spreadsheet;

/// <summary>A1-style reference helpers. All indexes are 0-based.</summary>
internal static class CellAddress
{
    public const int MaxColumns = 16384;
    public const int MaxRows = 1_048_576;

    /// <summary>0 becomes "A", 25 "Z", 26 "AA".</summary>
    public static string ColumnName(int column)
    {
        Span<char> buffer = stackalloc char[8];
        var i = buffer.Length;
        var n = column + 1;
        while (n > 0)
        {
            n--;
            buffer[--i] = (char)('A' + (n % 26));
            n /= 26;
        }

        return new string(buffer[i..]);
    }

    /// <summary>Returns the 0-based column index, or -1 when <paramref name="letters"/> is not a column name.</summary>
    public static int ParseColumn(ReadOnlySpan<char> letters)
    {
        if (letters.IsEmpty || letters.Length > 3)
        {
            return -1;
        }

        var value = 0;
        foreach (var ch in letters)
        {
            if (!char.IsAsciiLetter(ch))
            {
                return -1;
            }

            value = (value * 26) + (char.ToUpperInvariant(ch) - 'A' + 1);
        }

        return value - 1;
    }

    /// <summary>Parses "AB12" (optionally with $ signs) into 0-based row and column.</summary>
    public static bool TryParseCell(ReadOnlySpan<char> text, out int row, out int column)
    {
        row = -1;
        column = -1;
        text = text.Trim();

        var i = 0;
        while (i < text.Length && text[i] == '$')
        {
            i++;
        }

        var letterStart = i;
        while (i < text.Length && char.IsAsciiLetter(text[i]))
        {
            i++;
        }

        if (i == letterStart)
        {
            return false;
        }

        var parsedColumn = ParseColumn(text[letterStart..i]);
        if (parsedColumn < 0 || parsedColumn >= MaxColumns)
        {
            return false;
        }

        while (i < text.Length && text[i] == '$')
        {
            i++;
        }

        var digitStart = i;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        if (i == digitStart || i != text.Length)
        {
            return false;
        }

        if (!int.TryParse(text[digitStart..i], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedRow)
            || parsedRow < 1 || parsedRow > MaxRows)
        {
            return false;
        }

        row = parsedRow - 1;
        column = parsedColumn;
        return true;
    }
}

/// <summary>A rectangle of cells, inclusive on every side, 0-based.</summary>
internal readonly record struct CellRect(int Top, int Left, int Bottom, int Right)
{
    public int RowCount => Bottom - Top + 1;

    public int ColumnCount => Right - Left + 1;

    public long Area => (long)RowCount * ColumnCount;

    public bool Contains(int row, int column) => row >= Top && row <= Bottom && column >= Left && column <= Right;

    public bool Intersects(CellRect other) =>
        Top <= other.Bottom && other.Top <= Bottom && Left <= other.Right && other.Left <= Right;

    public CellRect Union(CellRect other) =>
        new(Math.Min(Top, other.Top), Math.Min(Left, other.Left), Math.Max(Bottom, other.Bottom), Math.Max(Right, other.Right));

    /// <summary>"A1:F24", or just "A1" for a single cell.</summary>
    public string ToA1()
    {
        var first = CellAddress.ColumnName(Left) + (Top + 1).ToString(CultureInfo.InvariantCulture);
        if (Top == Bottom && Left == Right)
        {
            return first;
        }

        return first + ":" + CellAddress.ColumnName(Right) + (Bottom + 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Parses "A1:F24" or "A1". Reversed corners are normalised.</summary>
    public static bool TryParse(string? text, out CellRect rect)
    {
        rect = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        var colon = span.IndexOf(':');
        int r1, c1, r2, c2;
        if (colon < 0)
        {
            if (!CellAddress.TryParseCell(span, out r1, out c1))
            {
                return false;
            }

            r2 = r1;
            c2 = c1;
        }
        else if (!CellAddress.TryParseCell(span[..colon], out r1, out c1)
                 || !CellAddress.TryParseCell(span[(colon + 1)..], out r2, out c2))
        {
            return false;
        }

        rect = new CellRect(Math.Min(r1, r2), Math.Min(c1, c2), Math.Max(r1, r2), Math.Max(c1, c2));
        return true;
    }
}
