using System.Net;
using System.Text;

namespace Contexo.App.Search;

/// <summary>
/// Converts the HTML produced by <c>HtmlTableRenderer</c> (table, caption, thead, tbody, tr, th, td, br) into plain text:
/// one line per row, written as 「欄：值；欄：值」. Text outside a table is kept as is.
/// </summary>
public static class TableHtmlText
{
    public static bool ContainsTable(string text) => text.Contains("<table", StringComparison.OrdinalIgnoreCase);

    public static string ToPlainText(string text)
    {
        if (!ContainsTable(text))
        {
            return text;
        }

        var result = new StringBuilder();
        var position = 0;
        while (position < text.Length)
        {
            var start = text.IndexOf("<table", position, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                result.Append(text, position, text.Length - position);
                break;
            }

            result.Append(text, position, start - position);
            var end = text.IndexOf("</table>", start, StringComparison.OrdinalIgnoreCase);
            var tableEnd = end < 0 ? text.Length : end + "</table>".Length;
            var rendered = RenderTable(text.Substring(start, tableEnd - start));
            if (result.Length > 0 && result[^1] != '\n' && rendered.Length > 0)
            {
                result.Append('\n');
            }

            result.Append(rendered);
            position = tableEnd;
        }

        return result.ToString().Trim();
    }

    private sealed class Cell(string text, bool header, int rowSpan, int colSpan)
    {
        public string Text { get; } = text;

        public bool Header { get; } = header;

        public int RowSpan { get; } = rowSpan;

        public int ColSpan { get; } = colSpan;
    }

    private static string RenderTable(string html)
    {
        var caption = new StringBuilder();
        var rows = new List<List<Cell>>();
        var headRows = new HashSet<int>();

        var inHead = false;
        var inCaption = false;
        List<Cell>? row = null;
        StringBuilder? cell = null;
        var cellHeader = false;
        var rowSpan = 1;
        var colSpan = 1;

        void EndCell()
        {
            if (cell is not null && row is not null)
            {
                row.Add(new Cell(Clean(cell.ToString()), cellHeader, rowSpan, colSpan));
            }

            cell = null;
        }

        var i = 0;
        while (i < html.Length)
        {
            if (html[i] != '<')
            {
                var next = html.IndexOf('<', i);
                var length = next < 0 ? html.Length - i : next - i;
                var chunk = html.Substring(i, length);
                if (cell is not null)
                {
                    cell.Append(chunk);
                }
                else if (inCaption)
                {
                    caption.Append(chunk);
                }

                i += length;
                continue;
            }

            var close = html.IndexOf('>', i);
            if (close < 0)
            {
                break;
            }

            var tag = html.Substring(i + 1, close - i - 1);
            i = close + 1;
            var closing = tag.StartsWith('/');
            if (closing)
            {
                tag = tag[1..];
            }

            var nameEnd = 0;
            while (nameEnd < tag.Length && !char.IsWhiteSpace(tag[nameEnd]) && tag[nameEnd] != '/')
            {
                nameEnd++;
            }

            var name = tag[..nameEnd].ToLowerInvariant();
            switch (name)
            {
                case "thead":
                    inHead = !closing;
                    break;
                case "caption":
                    inCaption = !closing;
                    break;
                case "tr":
                    EndCell();
                    if (!closing)
                    {
                        row = [];
                        rows.Add(row);
                        if (inHead)
                        {
                            headRows.Add(rows.Count - 1);
                        }
                    }
                    else
                    {
                        row = null;
                    }

                    break;
                case "th":
                case "td":
                    EndCell();
                    if (!closing && row is not null)
                    {
                        cell = new StringBuilder();
                        cellHeader = name == "th";
                        rowSpan = ReadSpan(tag, "rowspan");
                        colSpan = ReadSpan(tag, "colspan");
                    }

                    break;
                case "br":
                    (cell ?? (inCaption ? caption : null))?.Append(' ');
                    break;
            }
        }

        EndCell();
        return Layout(Clean(caption.ToString()), rows, headRows);
    }

    private static string Layout(string caption, List<List<Cell>> rows, HashSet<int> headRows)
    {
        // Place the cells on a grid, repeating spanned text over the cells it covers.
        var grid = new List<Dictionary<int, string>>();
        for (var r = 0; r < rows.Count; r++)
        {
            while (grid.Count <= r)
            {
                grid.Add([]);
            }

            var column = 0;
            foreach (var cell in rows[r])
            {
                while (grid[r].ContainsKey(column))
                {
                    column++;
                }

                for (var dr = 0; dr < cell.RowSpan && dr < 1000; dr++)
                {
                    while (grid.Count <= r + dr)
                    {
                        grid.Add([]);
                    }

                    for (var dc = 0; dc < cell.ColSpan && dc < 1000; dc++)
                    {
                        grid[r + dr][column + dc] = cell.Text;
                    }
                }

                column += cell.ColSpan;
            }
        }

        // Without <thead>, leading rows made only of <th> are the header.
        if (headRows.Count == 0)
        {
            for (var r = 0; r < rows.Count && rows[r].Count > 0 && rows[r].All(c => c.Header); r++)
            {
                headRows.Add(r);
            }
        }

        var headers = new Dictionary<int, string>();
        foreach (var r in headRows.Order())
        {
            foreach (var (column, text) in grid[r])
            {
                if (text.Length == 0)
                {
                    continue;
                }

                headers[column] = headers.TryGetValue(column, out var existing)
                    ? (existing.Split('_').Contains(text) ? existing : existing + "_" + text)
                    : text;
            }
        }

        var lines = new List<string>();
        if (caption.Length > 0)
        {
            lines.Add(caption);
        }

        var bodyCount = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            if (headRows.Contains(r))
            {
                continue;
            }

            bodyCount++;
            var pairs = new List<string>();
            foreach (var (column, text) in grid[r].OrderBy(p => p.Key))
            {
                if (text.Length == 0)
                {
                    continue;
                }

                pairs.Add(headers.TryGetValue(column, out var header) ? $"{header}：{text}" : text);
            }

            if (pairs.Count > 0)
            {
                lines.Add(string.Join("；", pairs));
            }
        }

        if (bodyCount == 0 && headers.Count > 0)
        {
            lines.Add(string.Join("；", headers.OrderBy(p => p.Key).Select(p => p.Value)));
        }

        return string.Join('\n', lines);
    }

    private static int ReadSpan(string tag, string attribute)
    {
        var index = tag.IndexOf(attribute + "=", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return 1;
        }

        var start = index + attribute.Length + 1;
        if (start < tag.Length && (tag[start] == '"' || tag[start] == '\''))
        {
            start++;
        }

        var end = start;
        while (end < tag.Length && char.IsAsciiDigit(tag[end]))
        {
            end++;
        }

        return int.TryParse(tag.AsSpan(start, end - start), out var value) && value > 1 ? value : 1;
    }

    private static string Clean(string text)
    {
        var decoded = WebUtility.HtmlDecode(text);
        var builder = new StringBuilder(decoded.Length);
        var space = false;
        foreach (var ch in decoded)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
