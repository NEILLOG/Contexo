using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Contexo.Core.Parsing.Spreadsheet;

internal enum NumberFormatKind
{
    General,
    Date,
    Percent,
}

/// <summary>What the parser needs to know about a cell format (<c>cellXfs</c> entry).</summary>
/// <param name="HasDate">Date part is shown (year, month or day).</param>
/// <param name="HasTime">Time part is shown (hour, minute or second).</param>
/// <param name="Elapsed">Elapsed-time format such as <c>[h]:mm</c>.</param>
internal readonly record struct StyleInfo(NumberFormatKind Format, bool HasDate, bool HasTime, bool Elapsed, bool Emphasis);

/// <summary>Cell formats of a workbook, indexed by the <c>s</c> attribute of a cell.</summary>
internal sealed class XlsxStyles
{
    private readonly StyleInfo[] _styles;

    private XlsxStyles(StyleInfo[] styles) => _styles = styles;

    public static XlsxStyles Empty { get; } = new([]);

    public StyleInfo Get(int index) => (uint)index < (uint)_styles.Length ? _styles[index] : default;

    public static XlsxStyles Load(WorkbookPart workbook)
    {
        var stylesheet = workbook.WorkbookStylesPart?.Stylesheet;
        if (stylesheet is null)
        {
            return Empty;
        }

        var codes = new Dictionary<int, string>();
        if (stylesheet.NumberingFormats is { } numberingFormats)
        {
            foreach (var format in numberingFormats.Elements<NumberingFormat>())
            {
                if (format.NumberFormatId?.Value is { } id && format.FormatCode?.Value is { } code)
                {
                    codes[(int)id] = code;
                }
            }
        }

        var boldFonts = new List<bool>();
        if (stylesheet.Fonts is { } fonts)
        {
            foreach (var font in fonts.Elements<Font>())
            {
                boldFonts.Add(font.Bold is { } bold && (bold.Val is null || bold.Val.Value));
            }
        }

        var coloredFills = new List<bool>();
        if (stylesheet.Fills is { } fills)
        {
            foreach (var fill in fills.Elements<Fill>())
            {
                var pattern = fill.PatternFill?.PatternType;
                coloredFills.Add(pattern?.Value is { } value && value != PatternValues.None && value != PatternValues.Gray125);
            }
        }

        var result = new List<StyleInfo>();
        if (stylesheet.CellFormats is { } cellFormats)
        {
            foreach (var xf in cellFormats.Elements<CellFormat>())
            {
                var numFmtId = (int)(xf.NumberFormatId?.Value ?? 0);
                codes.TryGetValue(numFmtId, out var code);
                var classified = NumberFormatClassifier.Classify(numFmtId, code);

                var fontId = (int)(xf.FontId?.Value ?? 0);
                var fillId = (int)(xf.FillId?.Value ?? 0);
                var emphasis = (fontId < boldFonts.Count && boldFonts[fontId])
                               || (fillId < coloredFills.Count && coloredFills[fillId]);

                result.Add(classified with { Emphasis = emphasis });
            }
        }

        return new XlsxStyles([.. result]);
    }
}

/// <summary>Decides whether a number format is a date, a time or a percentage, and renders numbers accordingly.</summary>
internal static class NumberFormatClassifier
{
    private static readonly StyleInfo DateOnly = new(NumberFormatKind.Date, true, false, false, false);
    private static readonly StyleInfo TimeOnly = new(NumberFormatKind.Date, false, true, false, false);
    private static readonly StyleInfo DateAndTime = new(NumberFormatKind.Date, true, true, false, false);
    private static readonly StyleInfo ElapsedTime = new(NumberFormatKind.Date, false, true, true, false);
    private static readonly StyleInfo Percent = new(NumberFormatKind.Percent, false, false, false, false);

    /// <summary>
    /// Built-in ids first (14-22, 45-47 as listed in the spec, plus the East Asian date and time ids 27-36 and 50-58),
    /// then the custom format code.
    /// </summary>
    public static StyleInfo Classify(int numFmtId, string? code)
    {
        switch (numFmtId)
        {
            case 9:
            case 10:
                return Percent;
            case >= 14 and <= 17:
            case >= 27 and <= 31:
            case 36:
            case >= 50 and <= 58:
                return DateOnly;
            case >= 18 and <= 21:
            case >= 32 and <= 35:
            case 45:
            case 47:
                return TimeOnly;
            case 22:
                return DateAndTime;
            case 46:
                return ElapsedTime;
        }

        return string.IsNullOrEmpty(code) ? default : ClassifyCode(code);
    }

    /// <summary>
    /// Looks only at the first section of the code and ignores quoted text, escaped characters, <c>_x</c> / <c>*x</c> padding
    /// and bracketed parts such as colours and locale tags.
    /// </summary>
    public static StyleInfo ClassifyCode(string code)
    {
        bool year = false, day = false, month = false, hour = false, second = false, ampm = false, elapsed = false, percent = false;

        for (var i = 0; i < code.Length; i++)
        {
            var ch = code[i];
            switch (ch)
            {
                case ';':
                    i = code.Length;
                    break;
                case '"':
                    i = code.IndexOf('"', i + 1);
                    if (i < 0)
                    {
                        i = code.Length;
                    }

                    break;
                case '\\':
                case '_':
                case '*':
                    i++;
                    break;
                case '[':
                    var close = code.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        close = code.Length - 1;
                    }

                    var inner = code[(i + 1)..close].Trim().ToLowerInvariant();
                    if (inner is "h" or "hh" or "m" or "mm" or "s" or "ss")
                    {
                        elapsed = true;
                    }

                    i = close;
                    break;
                case '%':
                    percent = true;
                    break;
                case 'y' or 'Y':
                    year = true;
                    break;
                case 'd' or 'D':
                    day = true;
                    break;
                case 'm' or 'M':
                    month = true;
                    break;
                case 'h' or 'H':
                    hour = true;
                    break;
                case 's' or 'S':
                    second = true;
                    break;
                case 'a' or 'A' when IsAmPm(code, i):
                    ampm = true;
                    i += code.AsSpan(i).StartsWith("am/pm", StringComparison.OrdinalIgnoreCase) ? 4 : 2;
                    break;
            }
        }

        var hasTime = hour || second || ampm || elapsed;
        var hasDate = year || day || (month && !hasTime);
        if (hasDate || hasTime)
        {
            return new StyleInfo(NumberFormatKind.Date, hasDate, hasTime, elapsed && !hasDate, false);
        }

        return percent ? Percent : default;
    }

    private static bool IsAmPm(string code, int index)
    {
        var rest = code.AsSpan(index);
        return rest.StartsWith("am/pm", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("a/p", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Display string of a stored number (<paramref name="raw"/> is the text of the <c>v</c> element).</summary>
    public static string Format(string raw, in StyleInfo style, bool date1904)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            return raw;
        }

        switch (style.Format)
        {
            case NumberFormatKind.Date:
                return FormatDate(value, style, date1904) ?? General(value);
            case NumberFormatKind.Percent:
                return General(value * 100) + "%";
            default:
                return General(value);
        }
    }

    private static string General(double value) =>
        value == 0 ? "0" : value.ToString("G15", CultureInfo.InvariantCulture);

    private static string? FormatDate(double serial, in StyleInfo style, bool date1904)
    {
        if (style.Elapsed)
        {
            var totalMinutes = (long)Math.Floor(Math.Round(serial * 86400) / 60);
            return string.Create(CultureInfo.InvariantCulture, $"{totalMinutes / 60:00}:{Math.Abs(totalMinutes % 60):00}");
        }

        var adjusted = date1904 ? serial + 1462 : serial;
        if (adjusted is < -657434 or >= 2958466)
        {
            return null;
        }

        DateTime date;
        try
        {
            date = DateTime.FromOADate(adjusted);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (style.HasDate)
        {
            var text = date.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
            return style.HasTime ? text + " " + date.ToString("HH:mm", CultureInfo.InvariantCulture) : text;
        }

        return date.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}
