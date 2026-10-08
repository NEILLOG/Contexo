using System.Globalization;

namespace Contexo.Core.Chunking;

/// <summary>Length and slicing helpers that work on text elements (so surrogate pairs, emoji and combining marks are never split).</summary>
internal static class TextMeasure
{
    public static int Length(string text) => Length(text.AsSpan());

    public static int Length(ReadOnlySpan<char> text)
    {
        var count = 0;
        while (!text.IsEmpty)
        {
            text = text[StringInfo.GetNextTextElementLength(text)..];
            count++;
        }

        return count;
    }

    /// <summary>Cuts <paramref name="text"/> into pieces of at most <paramref name="max"/> text elements.</summary>
    public static List<string> HardCut(string text, int max)
    {
        var result = new List<string>();
        var starts = StringInfo.ParseCombiningCharacters(text);
        if (starts.Length <= max)
        {
            result.Add(text);
            return result;
        }

        for (var i = 0; i < starts.Length; i += max)
        {
            var from = starts[i];
            var to = i + max < starts.Length ? starts[i + max] : text.Length;
            result.Add(text[from..to]);
        }

        return result;
    }

    /// <summary>The last <paramref name="count"/> text elements of <paramref name="text"/>.</summary>
    public static string Tail(string text, int count)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        var starts = StringInfo.ParseCombiningCharacters(text);
        return starts.Length <= count ? text : text[starts[starts.Length - count]..];
    }
}
