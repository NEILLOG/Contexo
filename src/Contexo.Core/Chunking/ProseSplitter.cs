using System.Text;

namespace Contexo.Core.Chunking;

/// <summary>A sentence-sized unit of prose. <see cref="Sep"/> is the text that separated it from the previous unit in the source ("" inside a paragraph).</summary>
internal sealed record Seg(string Text, string Sep, int Len);

/// <summary>Splits prose into pieces of at most a given size, cutting at paragraph, then sentence, then text-element boundaries.</summary>
internal static class ProseSplitter
{
    private const string FullWidthTerminators = "。！？；";
    private const string AsciiTerminators = ".!?;";
    private const string Closers = "」』”’）)]】》〉\"'";

    /// <summary>Greedy fill. Every piece is at most <paramref name="max"/> long (separators included).</summary>
    public static List<List<Seg>> Split(string text, int max)
    {
        var pieces = new List<List<Seg>>();
        var cur = new List<Seg>();
        var curLen = 0;

        void Add(Seg seg)
        {
            curLen += (cur.Count == 0 ? 0 : seg.Sep.Length) + seg.Len;
            cur.Add(seg);
        }

        void Flush()
        {
            if (cur.Count > 0)
            {
                pieces.Add(cur);
                cur = [];
                curLen = 0;
            }
        }

        foreach (var paragraph in Paragraphs(text, max))
        {
            var paragraphLen = 0;
            foreach (var seg in paragraph)
            {
                paragraphLen += seg.Len;
            }

            var firstSepLen = cur.Count == 0 ? 0 : paragraph[0].Sep.Length;
            if (curLen + firstSepLen + paragraphLen <= max)
            {
                foreach (var seg in paragraph)
                {
                    Add(seg);
                }
            }
            else if (paragraphLen <= max)
            {
                Flush();
                foreach (var seg in paragraph)
                {
                    Add(seg);
                }
            }
            else
            {
                // A paragraph larger than one piece: continue sentence by sentence.
                foreach (var seg in paragraph)
                {
                    var sepLen = cur.Count == 0 ? 0 : seg.Sep.Length;
                    if (curLen + sepLen + seg.Len > max)
                    {
                        Flush();
                    }

                    Add(seg);
                }
            }
        }

        Flush();
        return pieces;
    }

    public static string Join(IReadOnlyList<Seg> segments)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(segments[i].Sep);
            }

            sb.Append(segments[i].Text);
        }

        return sb.ToString();
    }

    public static int JoinedLength(IReadOnlyList<Seg> segments)
    {
        var len = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            len += segments[i].Len + (i > 0 ? segments[i].Sep.Length : 0);
        }

        return len;
    }

    /// <summary>
    /// Text taken from the end of <paramref name="previous"/> to repeat at the start of the next piece:
    /// as many whole sentences as fit in <paramref name="budget"/>, or the last <paramref name="budget"/> characters when even the last sentence is too long.
    /// </summary>
    public static string Overlap(IReadOnlyList<Seg> previous, int budget)
    {
        if (budget <= 0 || previous.Count == 0)
        {
            return string.Empty;
        }

        var total = 0;
        var first = previous.Count;
        for (var i = previous.Count - 1; i >= 0; i--)
        {
            var add = previous[i].Len + (i < previous.Count - 1 ? previous[i + 1].Sep.Length : 0);
            if (total + add > budget)
            {
                break;
            }

            total += add;
            first = i;
        }

        if (first == previous.Count)
        {
            return TextMeasure.Tail(previous[^1].Text, budget);
        }

        var sb = new StringBuilder();
        for (var i = first; i < previous.Count; i++)
        {
            if (i > first)
            {
                sb.Append(previous[i].Sep);
            }

            sb.Append(previous[i].Text);
        }

        return sb.ToString();
    }

    /// <summary>Paragraphs (non-blank lines) as lists of sentence units; sentences longer than <paramref name="max"/> are cut by text element.</summary>
    private static List<List<Seg>> Paragraphs(string text, int max)
    {
        var result = new List<List<Seg>>();
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var any = false;
        var blankSeen = false;
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
            {
                blankSeen |= any;
                continue;
            }

            var sep = !any ? string.Empty : blankSeen ? "\n\n" : "\n";
            any = true;
            blankSeen = false;

            var paragraph = new List<Seg>();
            foreach (var sentence in Sentences(line))
            {
                var len = TextMeasure.Length(sentence);
                if (len <= max)
                {
                    paragraph.Add(new Seg(sentence, paragraph.Count == 0 ? sep : string.Empty, len));
                    continue;
                }

                foreach (var cut in TextMeasure.HardCut(sentence, max))
                {
                    paragraph.Add(new Seg(cut, paragraph.Count == 0 ? sep : string.Empty, TextMeasure.Length(cut)));
                }
            }

            result.Add(paragraph);
        }

        return result;
    }

    /// <summary>Splits one line into sentences. The pieces concatenate back to the original line.</summary>
    internal static IEnumerable<string> Sentences(string line)
    {
        var start = 0;
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            var isFull = FullWidthTerminators.Contains(c);
            if (!isFull && !AsciiTerminators.Contains(c))
            {
                i++;
                continue;
            }

            var strong = isFull;
            var j = i + 1;
            while (j < line.Length)
            {
                var d = line[j];
                if (FullWidthTerminators.Contains(d))
                {
                    strong = true;
                }
                else if (!AsciiTerminators.Contains(d) && !Closers.Contains(d))
                {
                    break;
                }

                j++;
            }

            if (strong || j == line.Length || char.IsWhiteSpace(line[j]))
            {
                while (j < line.Length && char.IsWhiteSpace(line[j]))
                {
                    j++;
                }

                yield return line[start..j];
                start = j;
            }

            i = j;
        }

        if (start < line.Length)
        {
            yield return line[start..];
        }
    }
}
