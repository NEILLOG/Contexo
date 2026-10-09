using System.Text;

namespace Contexo.App.Search;

/// <summary>Finds the words of a query inside an excerpt and cuts the excerpt into highlighted / plain fragments.</summary>
public static class QueryHighlighter
{
    private const int MaxTerms = 64;

    /// <summary>
    /// Words worth highlighting: Latin / digit words of two or more characters, two-character CJK words as they are,
    /// and every three-character window of longer CJK runs (the same pieces the keyword search matches on).
    /// </summary>
    public static IReadOnlyList<string> ExtractTerms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        string normalized;
        try
        {
            normalized = query.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            normalized = query;
        }

        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var run = new List<Rune>();
        var runIsCjk = false;

        void Add(string term)
        {
            if (terms.Count < MaxTerms && seen.Add(term))
            {
                terms.Add(term);
            }
        }

        void Flush()
        {
            if (run.Count >= 2)
            {
                if (!runIsCjk || run.Count == 2)
                {
                    Add(Join(run, 0, run.Count));
                }
                else
                {
                    for (var start = 0; start + 3 <= run.Count; start++)
                    {
                        Add(Join(run, start, 3));
                    }
                }
            }

            run.Clear();
        }

        foreach (var rune in normalized.EnumerateRunes())
        {
            if (!Rune.IsLetterOrDigit(rune))
            {
                Flush();
                continue;
            }

            var cjk = IsCjk(rune);
            if (run.Count > 0 && cjk != runIsCjk)
            {
                Flush();
            }

            runIsCjk = cjk;
            run.Add(rune);
        }

        Flush();
        return terms;
    }

    /// <summary>Cuts <paramref name="text"/> into fragments; overlapping or touching matches become one highlighted fragment.</summary>
    public static IReadOnlyList<TextFragment> Split(string text, IReadOnlyList<string> terms)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var spans = new List<(int Start, int End)>();
        foreach (var term in terms)
        {
            if (term.Length == 0)
            {
                continue;
            }

            var index = 0;
            while ((index = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                spans.Add((index, index + term.Length));
                index += term.Length;
            }
        }

        if (spans.Count == 0)
        {
            return [new TextFragment(text, false)];
        }

        spans.Sort();
        var merged = new List<(int Start, int End)>();
        foreach (var span in spans)
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, span.End));
            }
            else
            {
                merged.Add(span);
            }
        }

        var fragments = new List<TextFragment>();
        var position = 0;
        foreach (var (start, end) in merged)
        {
            if (start > position)
            {
                fragments.Add(new TextFragment(text[position..start], false));
            }

            fragments.Add(new TextFragment(text[start..end], true));
            position = end;
        }

        if (position < text.Length)
        {
            fragments.Add(new TextFragment(text[position..], false));
        }

        return fragments;
    }

    /// <summary>Index of the first match of any term, or -1.</summary>
    public static int FindFirstMatch(string text, IReadOnlyList<string> terms)
    {
        var best = -1;
        foreach (var term in terms)
        {
            var index = term.Length == 0 ? -1 : text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (best < 0 || index < best))
            {
                best = index;
            }
        }

        return best;
    }

    private static string Join(List<Rune> runes, int start, int count)
    {
        var builder = new StringBuilder();
        for (var i = start; i < start + count; i++)
        {
            builder.Append(runes[i].ToString());
        }

        return builder.ToString();
    }

    private static bool IsCjk(Rune rune)
    {
        var value = rune.Value;
        return value is (>= 0x3400 and <= 0x4DBF)
            or (>= 0x4E00 and <= 0x9FFF)
            or (>= 0xF900 and <= 0xFAFF)
            or (>= 0x3040 and <= 0x30FF)
            or (>= 0x3100 and <= 0x312F)
            or (>= 0x31A0 and <= 0x31BF)
            or (>= 0xAC00 and <= 0xD7AF)
            or (>= 0x20000 and <= 0x2FFFF);
    }
}
