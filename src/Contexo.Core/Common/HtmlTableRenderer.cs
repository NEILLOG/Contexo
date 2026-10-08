using System.Text;
using Contexo.Core.Abstractions;

namespace Contexo.Core.Common;

/// <summary>
/// Renders a <see cref="TableModel"/> as HTML. The format is relied on by the chunker and the search UI:
/// <c>&lt;table&gt;</c>, optional <c>&lt;caption&gt;</c>, optional <c>&lt;thead&gt;</c>, <c>&lt;tbody&gt;</c>,
/// one <c>&lt;tr&gt;</c> per line, <c>&lt;th&gt;</c>/<c>&lt;td&gt;</c> cells. No style or other attributes except rowspan / colspan (only when greater than 1).
/// </summary>
public static class HtmlTableRenderer
{
    public static string Render(TableModel table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var cells = new Dictionary<(int Row, int Column), TableCell>();
        var covered = new HashSet<(int Row, int Column)>();
        foreach (var cell in table.Cells)
        {
            cells[(cell.Row, cell.Column)] = cell;
            for (var r = cell.Row; r < cell.Row + Math.Max(cell.RowSpan, 1); r++)
            {
                for (var c = cell.Column; c < cell.Column + Math.Max(cell.ColSpan, 1); c++)
                {
                    if (r != cell.Row || c != cell.Column)
                    {
                        covered.Add((r, c));
                    }
                }
            }
        }

        var headerRows = Math.Clamp(table.HeaderRowCount, 0, table.RowCount);
        var sb = new StringBuilder();
        sb.Append("<table>\n");

        if (!string.IsNullOrEmpty(table.Caption))
        {
            sb.Append("<caption>");
            AppendEncoded(sb, table.Caption);
            sb.Append("</caption>\n");
        }

        if (headerRows > 0)
        {
            sb.Append("<thead>\n");
            for (var r = 0; r < headerRows; r++)
            {
                AppendRow(sb, r, table.ColumnCount, "th", cells, covered);
            }

            sb.Append("</thead>\n");
        }

        if (table.RowCount > headerRows)
        {
            sb.Append("<tbody>\n");
            for (var r = headerRows; r < table.RowCount; r++)
            {
                AppendRow(sb, r, table.ColumnCount, "td", cells, covered);
            }

            sb.Append("</tbody>\n");
        }

        sb.Append("</table>");
        return sb.ToString();
    }

    private static void AppendRow(
        StringBuilder sb,
        int row,
        int columnCount,
        string tag,
        Dictionary<(int Row, int Column), TableCell> cells,
        HashSet<(int Row, int Column)> covered)
    {
        sb.Append("<tr>");
        for (var c = 0; c < columnCount; c++)
        {
            if (covered.Contains((row, c)))
            {
                continue;
            }

            if (!cells.TryGetValue((row, c), out var cell))
            {
                // Keep columns aligned when the model omits an empty cell.
                sb.Append('<').Append(tag).Append("></").Append(tag).Append('>');
                continue;
            }

            sb.Append('<').Append(tag);
            if (cell.RowSpan > 1)
            {
                sb.Append(" rowspan=\"").Append(cell.RowSpan).Append('"');
            }

            if (cell.ColSpan > 1)
            {
                sb.Append(" colspan=\"").Append(cell.ColSpan).Append('"');
            }

            sb.Append('>');
            AppendEncoded(sb, cell.Text);
            sb.Append("</").Append(tag).Append('>');
        }

        sb.Append("</tr>\n");
    }

    private static void AppendEncoded(StringBuilder sb, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\r':
                    sb.Append("<br>");
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    break;
                case '\n': sb.Append("<br>"); break;
                default: sb.Append(ch); break;
            }
        }
    }
}
