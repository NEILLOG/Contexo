using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Contexo.Core.Common;

namespace Contexo.Core.Parsing.Spreadsheet;

/// <summary>A CSV file seen as one worksheet named "csv". Fields are strings; the kind (text, number, date) is guessed for header detection only.</summary>
internal sealed partial class CsvSheetCells : ISheetCells
{
    public const string SheetName = "csv";

    private static readonly char[] Delimiters = [',', ';', '\t'];

    private readonly string _text;
    private readonly char _delimiter;

    private CsvSheetCells(string text)
    {
        _text = text;
        _delimiter = DetectDelimiter(text);
    }

    public static CsvSheetCells Load(Stream stream)
    {
        var text = TextDecoder.Decode(stream, out _);
        return new CsvSheetCells(text);
    }

    public void Walk(bool format, Func<int, int, SheetCell, bool> visit, CancellationToken cancellationToken)
    {
        var row = 0;
        foreach (var record in Parse(_text, _delimiter, int.MaxValue))
        {
            if ((row & 1023) == 1023)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            for (var column = 0; column < record.Count; column++)
            {
                var value = record[column].Trim();
                if (value.Length > 0 && !visit(row, column, new SheetCell(value, GuessKind(value), false)))
                {
                    return;
                }
            }

            row++;
        }
    }

    public IReadOnlyList<CellRect> ReadMerges() => [];

    /// <summary>
    /// Picks the delimiter among comma, semicolon and tab by looking at the first 20 records:
    /// the one whose records agree best on the number of fields (and, on ties, that has more fields).
    /// </summary>
    internal static char DetectDelimiter(string text)
    {
        var best = ',';
        double bestConsistency = -1;
        var bestColumns = 0;
        foreach (var candidate in Delimiters)
        {
            var counts = Parse(text, candidate, 20).Select(r => r.Count).ToList();
            if (counts.Count == 0)
            {
                continue;
            }

            var (mode, occurrences) = counts.GroupBy(c => c).Select(g => (g.Key, g.Count())).OrderByDescending(g => g.Item2).ThenByDescending(g => g.Key).First();
            if (mode < 2)
            {
                continue;
            }

            var consistency = (double)occurrences / counts.Count;
            if (consistency > bestConsistency || (consistency == bestConsistency && mode > bestColumns))
            {
                best = candidate;
                bestConsistency = consistency;
                bestColumns = mode;
            }
        }

        return best;
    }

    /// <summary>RFC 4180 records: quoted fields may contain the delimiter, line breaks and doubled quotes. A trailing line break adds no record.</summary>
    internal static IEnumerable<List<string>> Parse(string text, char delimiter, int maxRecords)
    {
        var produced = 0;
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStart = true;
        var hasContent = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }

                continue;
            }

            if (ch == '"' && fieldStart)
            {
                inQuotes = true;
                fieldStart = false;
                hasContent = true;
            }
            else if (ch == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
                fieldStart = true;
                hasContent = true;
            }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                fields.Add(field.ToString());
                field.Clear();
                yield return fields;
                if (++produced >= maxRecords)
                {
                    yield break;
                }

                fields = [];
                fieldStart = true;
                hasContent = false;
            }
            else
            {
                field.Append(ch);
                fieldStart = false;
                hasContent = true;
            }
        }

        if (hasContent || field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return fields;
        }
    }

    internal static CellKind GuessKind(string value)
    {
        if (DatePattern().IsMatch(value))
        {
            return CellKind.Date;
        }

        var number = value.EndsWith('%') ? value[..^1] : value;
        return double.TryParse(number, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _)
            ? CellKind.Number
            : CellKind.Text;
    }

    [GeneratedRegex(@"^(\d{4}[-/.]\d{1,2}[-/.]\d{1,2}|\d{1,2}[-/]\d{1,2}[-/]\d{4})([ T]\d{1,2}:\d{2}(:\d{2})?)?$")]
    private static partial Regex DatePattern();
}
