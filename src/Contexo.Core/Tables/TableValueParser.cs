using System.Globalization;
using System.Text;

namespace Contexo.Core.Tables;

/// <summary>Recognises numbers and dates in the display strings read from a spreadsheet, and builds SQL-safe column names.</summary>
internal static class TableValueParser
{
    private static readonly string[] DateFormats = BuildDateFormats();

    private static string[] BuildDateFormats()
    {
        var formats = new List<string>();
        foreach (var separator in new[] { '/', '-' })
        {
            var date = $"yyyy{separator}M{separator}d";
            formats.Add(date);
            foreach (var time in new[] { "H:mm", "H:mm:ss", "H:mm:ss.FFF" })
            {
                formats.Add($"{date} {time}");
                formats.Add($"{date}'T'{time}");
            }
        }

        return [.. formats];
    }

    /// <summary>
    /// Accepts thousands separators, a trailing %, surrounding spaces, currency symbols ($ NT$ ￥ ¥), a leading minus and accounting parentheses.
    /// A percent keeps the number as displayed (25.6% becomes 25.6). Integers with a leading zero (007, 0912) are treated as text such as codes and phone numbers.
    /// </summary>
    public static bool TryParseNumber(ReadOnlySpan<char> text, out double value)
    {
        value = 0;
        var span = text.Trim();
        if (span.IsEmpty)
        {
            return false;
        }

        var negative = false;
        if (span.Length >= 2 && span[0] == '(' && span[^1] == ')')
        {
            negative = true;
            span = span[1..^1].Trim();
        }

        span = TakeSign(span, out var firstSign);
        span = TakeCurrency(span);
        span = TakeSign(span, out var secondSign);
        negative ^= firstSign ^ secondSign;

        if (!span.IsEmpty && span[^1] == '%')
        {
            span = span[..^1].TrimEnd();
        }

        if (span.IsEmpty)
        {
            return false;
        }

        // Body: digits with optional thousands separators, then an optional fraction.
        var dot = span.IndexOf('.');
        var integerPart = dot < 0 ? span : span[..dot];
        var fraction = dot < 0 ? default : span[(dot + 1)..];
        if (dot >= 0 && (fraction.IsEmpty || !AllDigits(fraction)))
        {
            return false;
        }

        var comma = integerPart.IndexOf(',');
        var hasComma = comma >= 0;
        if (hasComma)
        {
            if (!ValidThousands(integerPart))
            {
                return false;
            }
        }
        else if (!AllDigits(integerPart))
        {
            return false;
        }

        var firstDigits = hasComma ? integerPart[..comma] : integerPart;
        if (firstDigits.Length > 1 && firstDigits[0] == '0')
        {
            return false;
        }

        bool parsed;
        double number;
        if (hasComma)
        {
            parsed = double.TryParse(span.ToString().Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
        }
        else
        {
            parsed = double.TryParse(span, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
        }

        if (!parsed || double.IsInfinity(number))
        {
            return false;
        }

        value = negative ? -number : number;
        return true;
    }

    public static bool TryParseDate(ReadOnlySpan<char> text, out DateTime value)
    {
        var span = text.Trim();
        // Cheap rejection before the exact parse: a date starts with four digits and a separator.
        if (span.Length < 8 || span.Length > 30 || !char.IsAsciiDigit(span[0]) || !char.IsAsciiDigit(span[3]) || (span[4] != '/' && span[4] != '-'))
        {
            value = default;
            return false;
        }

        return DateTime.TryParseExact(span, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    public static string FormatDate(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Keeps letters (including Chinese) and digits, replaces everything else with "_", prefixes "c_" when the name starts with a digit,
    /// and appends "_2", "_3"... to duplicates (compared case-insensitively, like SQLite does).
    /// </summary>
    public static IReadOnlyList<string> BuildSqlNames(IReadOnlyList<string> headers)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(headers.Count);
        for (var i = 0; i < headers.Count; i++)
        {
            var name = Sanitize(headers[i]);
            if (name.Length == 0)
            {
                name = "column_" + (i + 1).ToString(CultureInfo.InvariantCulture);
            }

            if (char.IsDigit(name[0]))
            {
                name = "c_" + name;
            }

            var candidate = name;
            for (var n = 2; !used.Add(candidate); n++)
            {
                candidate = name + "_" + n.ToString(CultureInfo.InvariantCulture);
            }

            result.Add(candidate);
        }

        return result;
    }

    private static string Sanitize(string header)
    {
        var builder = new StringBuilder(header.Length);
        foreach (var ch in header.Trim())
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        return builder.ToString();
    }

    private static ReadOnlySpan<char> TakeSign(ReadOnlySpan<char> span, out bool isMinus)
    {
        isMinus = false;
        if (!span.IsEmpty && (span[0] == '-' || span[0] == '+'))
        {
            isMinus = span[0] == '-';
            return span[1..].TrimStart();
        }

        return span;
    }

    private static ReadOnlySpan<char> TakeCurrency(ReadOnlySpan<char> span)
    {
        if (span.StartsWith("NT$", StringComparison.OrdinalIgnoreCase))
        {
            return span[3..].TrimStart();
        }

        if (!span.IsEmpty && (span[0] == '$' || span[0] == '＄' || span[0] == '￥' || span[0] == '¥'))
        {
            return span[1..].TrimStart();
        }

        return span;
    }

    private static bool AllDigits(ReadOnlySpan<char> span)
    {
        foreach (var ch in span)
        {
            if (!char.IsAsciiDigit(ch))
            {
                return false;
            }
        }

        return !span.IsEmpty;
    }

    /// <summary>1,234,567 style: first group 1 to 3 digits, every later group exactly 3.</summary>
    private static bool ValidThousands(ReadOnlySpan<char> span)
    {
        var groupLength = 0;
        var groups = 0;
        foreach (var ch in span)
        {
            if (ch == ',')
            {
                if (groups == 0 ? groupLength is < 1 or > 3 : groupLength != 3)
                {
                    return false;
                }

                groups++;
                groupLength = 0;
            }
            else if (char.IsAsciiDigit(ch))
            {
                groupLength++;
            }
            else
            {
                return false;
            }
        }

        return groups > 0 && groupLength == 3;
    }
}
