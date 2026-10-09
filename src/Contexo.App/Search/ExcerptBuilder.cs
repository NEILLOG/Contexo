using System.Text;

namespace Contexo.App.Search;

/// <summary>Builds the short excerpt shown in a search result: tables become plain text, long text is cut, query words are marked.</summary>
public static class ExcerptBuilder
{
    public const int MaxLength = 300;
    private const string Ellipsis = "…";

    public static IReadOnlyList<TextFragment> Build(string? text, IReadOnlyList<string> terms, int maxLength = MaxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var plain = Normalize(TableHtmlText.ToPlainText(text));
        if (plain.Length == 0)
        {
            return [];
        }

        var start = 0;
        var first = QueryHighlighter.FindFirstMatch(plain, terms);
        if (first > maxLength - 40)
        {
            start = Math.Max(0, first - 60);
            if (start > 0 && char.IsLowSurrogate(plain[start]))
            {
                start--;
            }
        }

        var length = Math.Min(maxLength, plain.Length - start);
        if (length > 0 && start + length < plain.Length && char.IsHighSurrogate(plain[start + length - 1]))
        {
            length--;
        }

        var excerpt = plain.Substring(start, length);
        var fragments = QueryHighlighter.Split(excerpt, terms).ToList();
        if (start > 0)
        {
            fragments.Insert(0, new TextFragment(Ellipsis, false));
        }

        if (start + length < plain.Length)
        {
            fragments.Add(new TextFragment(Ellipsis, false));
        }

        return fragments;
    }

    /// <summary>Trims, drops \r and squeezes runs of blank lines.</summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var newlines = 0;
        foreach (var ch in text.Trim())
        {
            if (ch == '\r')
            {
                continue;
            }

            if (ch == '\n')
            {
                newlines++;
                if (newlines <= 1)
                {
                    builder.Append(ch);
                }

                continue;
            }

            newlines = 0;
            builder.Append(ch);
        }

        return builder.ToString();
    }
}
