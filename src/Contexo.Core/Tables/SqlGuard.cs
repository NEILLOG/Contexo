using System.Text.RegularExpressions;
using Contexo.Core.Abstractions;

namespace Contexo.Core.Tables;

/// <summary>
/// First line of defence for query_table: one SELECT / WITH statement only, no ATTACH / DETACH / PRAGMA / load_extension.
/// The connection itself is also read-only (PRAGMA query_only = ON), so a gap here still cannot write.
/// </summary>
internal static partial class SqlGuard
{
    private const string EmptyMessage = "SQL 是空的。請提供一句 SELECT 查詢，資料表名稱固定為 t。";

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])(attach|detach|pragma|load_extension)(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenWords();

    [GeneratedRegex(@"^(select|with)(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AllowedStart();

    /// <summary>Returns the statement to run (trimmed, trailing semicolons removed) or throws <see cref="TableQueryException"/>.</summary>
    public static string Validate(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new TableQueryException(EmptyMessage);
        }

        var masked = Mask(sql);
        var end = masked.Length;
        while (end > 0 && (char.IsWhiteSpace(masked[end - 1]) || masked[end - 1] == ';'))
        {
            end--;
        }

        var start = 0;
        while (start < end && char.IsWhiteSpace(masked[start]))
        {
            start++;
        }

        var body = masked.Substring(start, end - start);
        if (body.Length == 0)
        {
            throw new TableQueryException(EmptyMessage);
        }

        if (body.Contains(';', StringComparison.Ordinal))
        {
            throw new TableQueryException("一次只能執行一句 SQL，請移除分號後面的內容。");
        }

        if (!AllowedStart().IsMatch(body))
        {
            throw new TableQueryException("只能執行查詢（以 SELECT 或 WITH 開頭的單一語句），不能修改資料。資料表名稱固定為 t。");
        }

        var forbidden = ForbiddenWords().Match(body);
        if (forbidden.Success)
        {
            throw new TableQueryException($"不允許使用 {forbidden.Value.ToUpperInvariant()}。只能用 SELECT 查詢資料表 t。");
        }

        // Trim from the original text: masked characters (quotes, comments) are blanks there but real text here.
        var executeEnd = sql.Length;
        while (executeEnd > start && (masked[executeEnd - 1] == ';' || (char.IsWhiteSpace(masked[executeEnd - 1]) && char.IsWhiteSpace(sql[executeEnd - 1]))))
        {
            executeEnd--;
        }

        return sql.Substring(start, executeEnd - start);
    }

    /// <summary>Replaces string literals, quoted identifiers and comments with spaces (same length) so keywords inside them are ignored.</summary>
    private static string Mask(string sql)
    {
        var chars = sql.ToCharArray();
        var i = 0;
        while (i < chars.Length)
        {
            var ch = chars[i];
            if (ch is '\'' or '"' or '`' or '[')
            {
                var close = ch == '[' ? ']' : ch;
                var j = i + 1;
                while (true)
                {
                    if (j >= chars.Length)
                    {
                        throw new TableQueryException("引號沒有成對，請檢查 SQL。");
                    }

                    if (chars[j] == close)
                    {
                        // A doubled quote is an escaped quote (not inside [ ]).
                        if (close != ']' && j + 1 < chars.Length && chars[j + 1] == close)
                        {
                            j += 2;
                            continue;
                        }

                        break;
                    }

                    j++;
                }

                Blank(chars, i, j);
                i = j + 1;
            }
            else if (ch == '-' && i + 1 < chars.Length && chars[i + 1] == '-')
            {
                var j = i;
                while (j < chars.Length && chars[j] != '\n')
                {
                    j++;
                }

                Blank(chars, i, j - 1);
                i = j;
            }
            else if (ch == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var last = close < 0 ? chars.Length - 1 : close + 1;
                Blank(chars, i, last);
                i = last + 1;
            }
            else
            {
                i++;
            }
        }

        return new string(chars);
    }

    private static void Blank(char[] chars, int from, int to)
    {
        for (var k = from; k <= to && k < chars.Length; k++)
        {
            chars[k] = ' ';
        }
    }
}
