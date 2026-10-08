using System.Text;

namespace Contexo.Core.Parsing.Pdf;

/// <summary>One visual line of text on a PDF page. Coordinates are PDF user space (origin bottom-left, Y grows upwards).</summary>
internal sealed record PdfLine(string Text, double Left, double Bottom, double Top)
{
    public double CenterY => (Bottom + Top) / 2;
}

/// <summary>A paragraph-like group of lines, in reading order.</summary>
internal sealed record PdfBlock(IReadOnlyList<PdfLine> Lines);

/// <summary>Layout-analysed text of a single page. Blocks are in reading order.</summary>
internal sealed record PdfPageText(int PageNumber, IReadOnlyList<PdfBlock> Blocks)
{
    public int NonWhitespaceCount => Blocks.Sum(b => b.Lines.Sum(l => CountNonWhitespace(l.Text)));

    private static int CountNonWhitespace(string text)
    {
        var count = 0;
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c))
            {
                count++;
            }
        }

        return count;
    }
}

/// <summary>Pure text logic for <see cref="PdfParser"/>: line joining, header / footer detection. No PdfPig types here so it is unit-testable.</summary>
internal static class PdfTextLayout
{
    /// <summary>Separator placed between paragraphs (blocks) of a page.</summary>
    public const string ParagraphSeparator = "\n\n";

    /// <summary>
    /// Joins the lines of one paragraph. A line break between two CJK characters is a layout wrap and is removed
    /// (no space is added); any other break is kept as a newline.
    /// </summary>
    public static string JoinLines(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var sb = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (sb.Length > 0 && !(EndsWithCjk(sb) && StartsWithCjk(line)))
            {
                sb.Append('\n');
            }

            sb.Append(line);
        }

        return sb.ToString();
    }

    /// <summary>Renders a page: lines are joined per block, blocks are separated by a blank line.</summary>
    public static string BuildPageText(PdfPageText page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var paragraphs = new List<string>();
        foreach (var block in page.Blocks)
        {
            var text = JoinLines(block.Lines.Select(l => l.Text));
            if (text.Length > 0)
            {
                paragraphs.Add(text);
            }
        }

        return string.Join(ParagraphSeparator, paragraphs);
    }

    /// <summary>
    /// Removes running headers and footers. The topmost and bottommost row of every page is compared with digits and
    /// whitespace stripped (so page numbers do not matter); a row that repeats on more than half of the pages is removed.
    /// Documents with fewer than 3 pages are returned unchanged.
    /// </summary>
    public static IReadOnlyList<PdfPageText> RemoveHeadersAndFooters(IReadOnlyList<PdfPageText> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);

        if (pages.Count < 3)
        {
            return pages;
        }

        var topRows = new List<(string Key, HashSet<PdfLine> Lines)?>(pages.Count);
        var bottomRows = new List<(string Key, HashSet<PdfLine> Lines)?>(pages.Count);
        var topCounts = new Dictionary<string, int>();
        var bottomCounts = new Dictionary<string, int>();

        foreach (var page in pages)
        {
            var lines = page.Blocks.SelectMany(b => b.Lines).Where(l => l.Text.Trim().Length > 0).ToList();
            var top = RowAt(lines, atTop: true);
            var bottom = RowAt(lines, atTop: false);
            topRows.Add(top);
            bottomRows.Add(bottom);
            if (top is { } t)
            {
                topCounts[t.Key] = topCounts.GetValueOrDefault(t.Key) + 1;
            }

            if (bottom is { } b)
            {
                bottomCounts[b.Key] = bottomCounts.GetValueOrDefault(b.Key) + 1;
            }
        }

        var result = new List<PdfPageText>(pages.Count);
        for (var i = 0; i < pages.Count; i++)
        {
            var remove = new HashSet<PdfLine>();
            if (topRows[i] is { } top && IsRepeated(topCounts[top.Key], pages.Count))
            {
                remove.UnionWith(top.Lines);
            }

            if (bottomRows[i] is { } bottom && IsRepeated(bottomCounts[bottom.Key], pages.Count))
            {
                remove.UnionWith(bottom.Lines);
            }

            if (remove.Count == 0)
            {
                result.Add(pages[i]);
                continue;
            }

            var blocks = new List<PdfBlock>();
            foreach (var block in pages[i].Blocks)
            {
                var kept = block.Lines.Where(l => !remove.Contains(l)).ToList();
                if (kept.Count > 0)
                {
                    blocks.Add(new PdfBlock(kept));
                }
            }

            result.Add(pages[i] with { Blocks = blocks });
        }

        return result;
    }

    /// <summary>Comparison key of a header / footer row: lower-case, digits and whitespace removed.</summary>
    public static string NormalizeForComparison(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c) && !char.IsDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    /// <summary>True for ideographs, kana, hangul and bopomofo. Punctuation is deliberately excluded.</summary>
    public static bool IsCjk(int codePoint) => codePoint switch
    {
        >= 0x2E80 and <= 0x2FDF => true,   // radicals
        >= 0x3005 and <= 0x3007 => true,   // 々 〆 〇
        >= 0x3040 and <= 0x30FF => true,   // hiragana, katakana
        >= 0x3100 and <= 0x312F => true,   // bopomofo
        >= 0x31A0 and <= 0x31BF => true,   // bopomofo extended
        >= 0x31F0 and <= 0x31FF => true,   // katakana extensions
        >= 0x3400 and <= 0x4DBF => true,   // extension A
        >= 0x4E00 and <= 0x9FFF => true,   // unified ideographs
        >= 0xAC00 and <= 0xD7AF => true,   // hangul syllables
        >= 0xF900 and <= 0xFAFF => true,   // compatibility ideographs
        >= 0xFF66 and <= 0xFF9F => true,   // half-width katakana
        >= 0x20000 and <= 0x2FA1F => true, // extensions B and beyond
        _ => false,
    };

    private static bool IsRepeated(int count, int pageCount) => count * 2 > pageCount;

    /// <summary>The topmost (or bottommost) visual row: all lines whose vertical centre falls inside the extreme line.</summary>
    private static (string Key, HashSet<PdfLine> Lines)? RowAt(List<PdfLine> lines, bool atTop)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        var extreme = lines[0];
        foreach (var line in lines)
        {
            if (atTop ? line.CenterY > extreme.CenterY : line.CenterY < extreme.CenterY)
            {
                extreme = line;
            }
        }

        var row = lines.Where(l => l.CenterY >= extreme.Bottom && l.CenterY <= extreme.Top)
            .OrderBy(l => l.Left)
            .ToList();
        var key = NormalizeForComparison(string.Join(' ', row.Select(l => l.Text)));
        return (key, row.ToHashSet());
    }

    private static bool EndsWithCjk(StringBuilder sb)
    {
        var last = sb[^1];
        if (char.IsLowSurrogate(last) && sb.Length >= 2 && char.IsHighSurrogate(sb[^2]))
        {
            return IsCjk(char.ConvertToUtf32(sb[^2], last));
        }

        return IsCjk(last);
    }

    private static bool StartsWithCjk(string line)
    {
        var first = line[0];
        if (char.IsHighSurrogate(first) && line.Length >= 2 && char.IsLowSurrogate(line[1]))
        {
            return IsCjk(char.ConvertToUtf32(first, line[1]));
        }

        return IsCjk(first);
    }
}
