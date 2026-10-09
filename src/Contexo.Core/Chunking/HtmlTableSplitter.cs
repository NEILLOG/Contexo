using System.Globalization;
using System.Text;

namespace Contexo.Core.Chunking;

/// <summary>
/// Splits a table rendered by <c>HtmlTableRenderer</c> into several complete tables at row boundaries.
/// Every part repeats the caption and the header (<c>&lt;thead&gt;</c>, or the first row when there is none).
/// </summary>
internal static class HtmlTableSplitter
{
    /// <summary>Returns null when the text does not look like a rendered table.</summary>
    public static List<string>? Split(string html, int hardMax)
    {
        if (html.IndexOf("<table>", StringComparison.Ordinal) < 0)
        {
            return null;
        }

        var caption = string.Empty;
        var captionStart = html.IndexOf("<caption>", StringComparison.Ordinal);
        var captionEnd = html.IndexOf("</caption>", StringComparison.Ordinal);
        if (captionStart >= 0 && captionEnd > captionStart)
        {
            caption = html[captionStart..(captionEnd + "</caption>".Length)];
        }

        var theadStart = html.IndexOf("<thead>", StringComparison.Ordinal);
        var theadEnd = html.IndexOf("</thead>", StringComparison.Ordinal);
        var hasThead = theadStart >= 0 && theadEnd > theadStart;

        var header = new List<string>();
        var body = new List<string>();
        var position = 0;
        while (true)
        {
            var start = html.IndexOf("<tr>", position, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var end = html.IndexOf("</tr>", start, StringComparison.Ordinal);
            if (end < 0)
            {
                return null;
            }

            end += "</tr>".Length;
            var row = html[start..end];
            var inHead = hasThead && start > theadStart && end <= theadEnd;
            (inHead || (!hasThead && header.Count == 0) ? header : body).Add(row);
            position = end;
        }

        if (header.Count + body.Count == 0 || body.Count == 0)
        {
            return null;
        }

        var fixedLength = TextMeasure.Length(Build(caption, header, hasThead, []));
        if (fixedLength * 2 > hardMax)
        {
            // The header is too large to repeat: treat every row as an ordinary row.
            body.InsertRange(0, header);
            header = [];
            hasThead = false;
            fixedLength = TextMeasure.Length(Build(caption, header, hasThead, []));
        }

        var rowBudget = Math.Max(1, hardMax - fixedLength);
        var rows = new List<string>();
        foreach (var row in body)
        {
            if (TextMeasure.Length(row) + 1 > rowBudget)
            {
                rows.AddRange(SplitRow(row, rowBudget));
            }
            else
            {
                rows.Add(row);
            }
        }

        var parts = new List<string>();
        var current = new List<string>();
        var currentLength = fixedLength;
        foreach (var row in rows)
        {
            var rowLength = TextMeasure.Length(row) + 1;
            if (current.Count > 0 && currentLength + rowLength > hardMax)
            {
                parts.Add(Build(caption, header, hasThead, current));
                current = [];
                currentLength = fixedLength;
            }

            current.Add(row);
            currentLength += rowLength;
        }

        if (current.Count > 0)
        {
            parts.Add(Build(caption, header, hasThead, current));
        }

        return parts;
    }

    private static string Build(string caption, List<string> header, bool hasThead, List<string> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<table>\n");
        if (caption.Length > 0)
        {
            sb.Append(caption).Append('\n');
        }

        if (hasThead && header.Count > 0)
        {
            sb.Append("<thead>\n");
            foreach (var row in header)
            {
                sb.Append(row).Append('\n');
            }

            sb.Append("</thead>\n");
        }

        sb.Append("<tbody>\n");
        if (!hasThead)
        {
            foreach (var row in header)
            {
                sb.Append(row).Append('\n');
            }
        }

        foreach (var row in rows)
        {
            sb.Append(row).Append('\n');
        }

        sb.Append("</tbody>\n</table>");
        return sb.ToString();
    }

    private sealed record Cell(string OpenTag, string Name, string Content);

    /// <summary>
    /// Splits a row that is too big for one part into several rows by dividing the text of every cell,
    /// so no text is lost and the columns stay aligned.
    /// </summary>
    private static List<string> SplitRow(string row, int budget)
    {
        var cells = ParseCells(row);
        if (cells is null || cells.Count == 0)
        {
            return [row];
        }

        // "<tr>" + "</tr>" + newline + "<td>" + "</td>" per cell.
        var overhead = 10 + (cells.Count * 9);
        var perCell = Math.Max(1, (budget - overhead) / cells.Count);

        var pieces = cells.Select(c => PackTokens(Tokenize(c.Content), perCell)).ToList();
        var count = Math.Max(1, pieces.Max(p => p.Count));
        var result = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var sb = new StringBuilder("<tr>");
            for (var c = 0; c < cells.Count; c++)
            {
                var open = i == 0 ? cells[c].OpenTag : $"<{cells[c].Name}>";
                sb.Append(open);
                if (i < pieces[c].Count)
                {
                    sb.Append(pieces[c][i]);
                }

                sb.Append("</").Append(cells[c].Name).Append('>');
            }

            sb.Append("</tr>");
            result.Add(sb.ToString());
        }

        return result;
    }

    private static List<Cell>? ParseCells(string row)
    {
        var cells = new List<Cell>();
        var p = "<tr>".Length;
        while (p < row.Length)
        {
            if (string.CompareOrdinal(row, p, "</tr>", 0, 5) == 0)
            {
                return cells;
            }

            if (row[p] != '<' || p + 2 >= row.Length || row[p + 1] != 't' || (row[p + 2] != 'h' && row[p + 2] != 'd'))
            {
                return null;
            }

            var name = row[p + 2] == 'h' ? "th" : "td";
            var gt = row.IndexOf('>', p);
            if (gt < 0)
            {
                return null;
            }

            var closeTag = $"</{name}>";
            var close = row.IndexOf(closeTag, gt + 1, StringComparison.Ordinal);
            if (close < 0)
            {
                return null;
            }

            cells.Add(new Cell(row[p..(gt + 1)], name, row[(gt + 1)..close]));
            p = close + closeTag.Length;
        }

        return null;
    }

    /// <summary>Breaks cell content into atoms that must not be divided: "&lt;br&gt;", character entities and text elements.</summary>
    private static List<string> Tokenize(string content)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < content.Length)
        {
            int length;
            if (string.CompareOrdinal(content, i, "<br>", 0, 4) == 0)
            {
                length = 4;
            }
            else if (content[i] == '&' && content.IndexOf(';', i) is var semi and > 0 && semi - i <= 7)
            {
                length = semi - i + 1;
            }
            else
            {
                length = StringInfo.GetNextTextElementLength(content.AsSpan(i));
            }

            tokens.Add(content.Substring(i, length));
            i += length;
        }

        return tokens;
    }

    private static List<string> PackTokens(List<string> tokens, int max)
    {
        var pieces = new List<string>();
        var sb = new StringBuilder();
        var length = 0;
        foreach (var token in tokens)
        {
            var tokenLength = TextMeasure.Length(token);
            if (length > 0 && length + tokenLength > max)
            {
                pieces.Add(sb.ToString());
                sb.Clear();
                length = 0;
            }

            sb.Append(token);
            length += tokenLength;
        }

        if (length > 0)
        {
            pieces.Add(sb.ToString());
        }

        return pieces;
    }
}
