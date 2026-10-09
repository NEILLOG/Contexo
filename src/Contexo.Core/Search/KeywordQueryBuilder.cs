using System.Text;

namespace Contexo.Core.Search;

/// <summary>
/// Turns a free-form user query into an FTS5 MATCH expression (trigram tokenizer) plus short terms for the LIKE fallback.
/// Every FTS term is a quoted string, so user input can never be read as FTS5 syntax (AND, NOT, *, :, ^, parentheses).
/// </summary>
internal static class KeywordQueryBuilder
{
    public const int MaxFtsTerms = 32;
    public const int MaxLikeTerms = 8;

    private enum CharClass
    {
        Other,
        Alnum,
        Cjk,
    }

    public static (string? FtsQuery, IReadOnlyList<string> LikeTerms) Build(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return (null, []);
        }

        var runes = Normalize(query);
        var ftsTerms = new List<string>();
        var ftsSeen = new HashSet<string>(StringComparer.Ordinal);
        var likeTerms = new List<string>();

        var i = 0;
        while (i < runes.Count)
        {
            var cls = Classify(runes[i]);
            if (cls == CharClass.Other)
            {
                i++;
                continue;
            }

            var start = i;
            i++;
            while (i < runes.Count)
            {
                var next = Classify(runes[i]);
                if (next == cls)
                {
                    i++;
                }
                else if (cls == CharClass.Alnum && IsConnector(runes[i]) && i + 1 < runes.Count && Classify(runes[i + 1]) == CharClass.Alnum)
                {
                    // '-', '_' and '.' are kept only between letters/digits: model numbers (ABC-123), versions (1.2).
                    i += 2;
                }
                else
                {
                    break;
                }
            }

            AddSegment(runes.GetRange(start, i - start), cls, ftsTerms, ftsSeen, likeTerms);
        }

        var fts = ftsTerms.Count == 0 ? null : string.Join(" OR ", ftsTerms);
        return (fts, likeTerms);
    }

    private static void AddSegment(List<Rune> segment, CharClass cls, List<string> ftsTerms, HashSet<string> ftsSeen, List<string> likeTerms)
    {
        var length = segment.Count;
        if (length < 2)
        {
            return;
        }

        if (length == 2)
        {
            if (likeTerms.Count < MaxLikeTerms)
            {
                var term = ToText(segment, 0, 2);
                if (!likeTerms.Contains(term))
                {
                    likeTerms.Add(term);
                }
            }

            return;
        }

        if (cls == CharClass.Alnum)
        {
            AddFts(ToText(segment, 0, length), ftsTerms, ftsSeen);
            return;
        }

        for (var start = 0; start + 3 <= length && ftsTerms.Count < MaxFtsTerms; start++)
        {
            AddFts(ToText(segment, start, 3), ftsTerms, ftsSeen);
        }
    }

    private static void AddFts(string text, List<string> ftsTerms, HashSet<string> ftsSeen)
    {
        if (ftsTerms.Count >= MaxFtsTerms || !ftsSeen.Add(text))
        {
            return;
        }

        ftsTerms.Add("\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
    }

    private static string ToText(List<Rune> runes, int start, int count)
    {
        var builder = new StringBuilder(count * 2);
        for (var i = start; i < start + count; i++)
        {
            builder.Append(runes[i].ToString());
        }

        return builder.ToString();
    }

    /// <summary>Full-width ASCII to half-width, ideographic space to space, lower case.</summary>
    private static List<Rune> Normalize(string query)
    {
        var result = new List<Rune>(query.Length);
        foreach (var rune in query.EnumerateRunes())
        {
            var value = rune;
            if (value.Value == 0x3000)
            {
                value = new Rune(' ');
            }
            else if (value.Value is >= 0xFF01 and <= 0xFF5E)
            {
                value = new Rune(value.Value - 0xFEE0);
            }

            result.Add(Rune.ToLowerInvariant(value));
        }

        return result;
    }

    private static bool IsConnector(Rune rune) => rune.Value is '-' or '_' or '.';

    private static CharClass Classify(Rune rune)
    {
        if (IsCjk(rune.Value))
        {
            return CharClass.Cjk;
        }

        return Rune.IsLetterOrDigit(rune) ? CharClass.Alnum : CharClass.Other;
    }

    private static bool IsCjk(int c) =>
        c is >= 0x4E00 and <= 0x9FFF
            or >= 0x3400 and <= 0x4DBF
            or >= 0x20000 and <= 0x2A6DF
            or >= 0x2A700 and <= 0x2B73F
            or >= 0x2B740 and <= 0x2B81F
            or >= 0x2B820 and <= 0x2CEAF
            or >= 0xF900 and <= 0xFAFF
            or >= 0x2F800 and <= 0x2FA1F
            or >= 0x3040 and <= 0x30FF // hiragana, katakana
            or >= 0x3100 and <= 0x312F // bopomofo
            or >= 0xAC00 and <= 0xD7AF; // hangul syllables
}
