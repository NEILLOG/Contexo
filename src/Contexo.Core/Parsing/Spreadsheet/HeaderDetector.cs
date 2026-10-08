using System.Text;

namespace Contexo.Core.Parsing.Spreadsheet;

/// <summary>Result of looking at the top rows of a region.</summary>
/// <param name="DecorativeRows">Rows at the very top that hold only a banner (title); they are not part of the table.</param>
/// <param name="Caption">Text of the banner rows, or null.</param>
/// <param name="HeaderRows">Header rows right after the banner rows. 0 when no header was found.</param>
internal readonly record struct HeaderLayout(int DecorativeRows, string? Caption, int HeaderRows);

/// <summary>
/// Decides where the header of a table is and what its columns are called. Used when parsing and again by
/// <see cref="SpreadsheetRegionReader"/>, so both always produce the same column names.
/// </summary>
internal static class HeaderDetector
{
    /// <summary>How many top rows are inspected for a header, and the most header rows a table can have.</summary>
    public const int MaxHeaderRows = 4;

    /// <summary>The most banner rows skipped above a header.</summary>
    public const int MaxDecorativeRows = 4;

    /// <summary>Rows a caller must supply (when the region has that many) so that <see cref="Detect"/> can decide.</summary>
    public const int WindowRows = 14;

    private const double TextThreshold = 0.6;
    private const double EmphasisTextThreshold = 0.4;

    /// <param name="rows">The first rows of the region (at least <see cref="WindowRows"/> when it has that many); each as wide as the region.</param>
    /// <param name="totalRows">Rows in the whole region.</param>
    /// <param name="top">Sheet row of <c>rows[0]</c>.</param>
    /// <param name="left">Sheet column of the first column.</param>
    /// <param name="merges">Merged ranges near the top of the region (sheet coordinates).</param>
    public static HeaderLayout Detect(SheetCell[][] rows, int totalRows, int top, int left, IReadOnlyList<CellRect> merges)
    {
        var available = Math.Min(rows.Length, totalRows);
        if (available == 0)
        {
            return default;
        }

        var width = rows[0].Length;

        // 1. Banner rows: a single value cell that is a merged cell spanning at least half the region.
        //    A lone cell followed by a blank row counts too (a title line above a CSV table); blank rows after a banner are skipped.
        var decorative = 0;
        var captions = new List<string>();
        while (decorative < MaxDecorativeRows && decorative + 1 < available)
        {
            var row = rows[decorative];
            var (count, column) = ValueCells(row);
            if (count == 0 && decorative > 0)
            {
                decorative++;
                continue;
            }

            if (count != 1)
            {
                break;
            }

            var merge = AnchoredMerge(merges, top + decorative, left + column);
            var span = 1;
            if (merge is { } m)
            {
                if (m.ColumnCount * 2 < width || decorative + m.RowCount > MaxDecorativeRows * 2)
                {
                    break;
                }

                span = Math.Max(1, m.RowCount);
            }
            else if (width < 3 || ValueCells(rows[decorative + 1]).Count != 0)
            {
                break;
            }

            captions.Add(Normalize(row[column].Text) ?? string.Empty);
            decorative += span;
        }

        decorative = Math.Min(decorative, available - 1);
        var caption = captions.Count == 0 ? null : string.Join(' ', captions);

        // 2./3. Header rows: leading text-heavy rows followed by a row that is clearly less textual.
        var start = decorative;
        var maxBlock = Math.Min(MaxHeaderRows, available - start);
        var stats = new RowStats[Math.Min(available - start, MaxHeaderRows + 1)];
        for (var i = 0; i < stats.Length; i++)
        {
            stats[i] = RowStats.Of(rows[start + i]);
        }

        var headerLike = 0;
        while (headerLike < maxBlock && stats[headerLike].IsHeaderLike)
        {
            headerLike++;
        }

        for (var k = 1; k <= headerLike; k++)
        {
            if (start + k >= totalRows || k >= stats.Length)
            {
                break;
            }

            var next = stats[k];
            var last = stats[k - 1];
            var dropsInText = next.TextRatio < last.TextRatio;
            if (!dropsInText && next.NumericRatio <= 0.5)
            {
                continue;
            }

            if (k == 1 || IsMultiLevel(stats, start, k, top, merges))
            {
                return new HeaderLayout(decorative, caption, k);
            }
        }

        // 4. Nothing by text content; accept a single bold / filled row followed by plain rows.
        if (stats.Length >= 2 && start + 1 < totalRows && stats[0].IsHeaderLike && stats[0].Emphasized && !stats[1].Emphasized)
        {
            return new HeaderLayout(decorative, caption, 1);
        }

        return new HeaderLayout(decorative, caption, 0);
    }

    /// <summary>
    /// Column names for a region. <paramref name="headerRows"/> are the header rows (may be empty), each <paramref name="columnCount"/> wide,
    /// starting at sheet row <paramref name="top"/> and column <paramref name="left"/>.
    /// Merged cells are filled in first, the levels of each column are joined with "_", blank names become "欄C" and repeats get "_2", "_3".
    /// </summary>
    public static string[] BuildColumnNames(SheetCell[][] headerRows, int columnCount, int top, int left, IReadOnlyList<CellRect> merges)
    {
        var levels = headerRows.Length;
        var texts = new string?[levels, columnCount];
        for (var r = 0; r < levels; r++)
        {
            for (var c = 0; c < columnCount; c++)
            {
                texts[r, c] = Normalize(headerRows[r][c].Text);
            }
        }

        foreach (var merge in merges)
        {
            if (merge.Top < top || merge.Top >= top + levels || merge.Left < left || merge.Left >= left + columnCount)
            {
                continue;
            }

            var value = texts[merge.Top - top, merge.Left - left];
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var bottom = Math.Min(merge.Bottom, top + levels - 1);
            var right = Math.Min(merge.Right, left + columnCount - 1);
            for (var r = merge.Top; r <= bottom; r++)
            {
                for (var c = merge.Left; c <= right; c++)
                {
                    texts[r - top, c - left] = value;
                }
            }
        }

        var names = new string[columnCount];
        var parts = new List<string>(levels);
        for (var c = 0; c < columnCount; c++)
        {
            parts.Clear();
            for (var r = 0; r < levels; r++)
            {
                var text = texts[r, c];
                if (!string.IsNullOrEmpty(text) && (parts.Count == 0 || parts[^1] != text))
                {
                    parts.Add(text);
                }
            }

            names[c] = parts.Count == 0 ? "欄" + CellAddress.ColumnName(left + c) : string.Join('_', parts);
        }

        return MakeUnique(names);
    }

    /// <summary>Merges that start inside <paramref name="rect"/>, as a short list a caller can pass to <see cref="Detect"/>.</summary>
    public static List<CellRect> MergesIn(IReadOnlyList<CellRect> merges, CellRect rect)
    {
        var result = new List<CellRect>();
        foreach (var merge in merges)
        {
            if (rect.Contains(merge.Top, merge.Left))
            {
                result.Add(merge);
            }
        }

        return result;
    }

    internal static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (text.AsSpan().IndexOfAny('\r', '\n', '\t') < 0)
        {
            return text.Trim();
        }

        var builder = new StringBuilder(text.Length);
        foreach (var part in text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(part);
        }

        return builder.ToString();
    }

    private static string[] MakeUnique(string[] names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];
            if (!seen.Add(name))
            {
                var suffix = 2;
                string candidate;
                do
                {
                    candidate = name + "_" + suffix++;
                }
                while (!seen.Add(candidate));

                names[i] = candidate;
            }
        }

        return names;
    }

    private static (int Count, int Column) ValueCells(SheetCell[] row)
    {
        var count = 0;
        var column = -1;
        for (var i = 0; i < row.Length; i++)
        {
            if (!row[i].IsEmpty)
            {
                count++;
                column = i;
            }
        }

        return (count, column);
    }

    private static CellRect? AnchoredMerge(IReadOnlyList<CellRect> merges, int row, int column)
    {
        foreach (var merge in merges)
        {
            if (merge.Top == row && merge.Left == column)
            {
                return merge;
            }
        }

        return null;
    }

    /// <summary>
    /// A block of two or more header rows is a multi-level header only when it has some structure to show for it:
    /// a merged cell inside the block, or an upper row that is filled more sparsely than the row below it.
    /// Otherwise the rows are probably just text-heavy data.
    /// </summary>
    private static bool IsMultiLevel(RowStats[] stats, int start, int count, int top, IReadOnlyList<CellRect> merges)
    {
        var blockTop = top + start;
        var blockBottom = blockTop + count - 1;
        foreach (var merge in merges)
        {
            if (merge.Top >= blockTop && merge.Top <= blockBottom && merge.Area > 1)
            {
                return true;
            }
        }

        return stats[0].Count >= 2 && stats[0].Count < stats[count - 1].Count;
    }

    private readonly record struct RowStats(int Count, int Text, int Numeric, int Emphasis)
    {
        public double TextRatio => Count == 0 ? 0 : (double)Text / Count;

        public double NumericRatio => Count == 0 ? 0 : (double)Numeric / Count;

        public bool Emphasized => Count > 0 && Emphasis * 2 > Count;

        public bool IsHeaderLike => Count > 0 && TextRatio >= (Emphasized ? EmphasisTextThreshold : TextThreshold);

        public static RowStats Of(SheetCell[] row)
        {
            int count = 0, text = 0, numeric = 0, emphasis = 0;
            foreach (var cell in row)
            {
                switch (cell.Kind)
                {
                    case CellKind.Empty:
                        continue;
                    case CellKind.Text:
                        text++;
                        break;
                    case CellKind.Number or CellKind.Date:
                        numeric++;
                        break;
                }

                count++;
                if (cell.Emphasis)
                {
                    emphasis++;
                }
            }

            return new RowStats(count, text, numeric, emphasis);
        }
    }
}
