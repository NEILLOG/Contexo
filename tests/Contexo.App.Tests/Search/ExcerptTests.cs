using Contexo.App.Search;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;

namespace Contexo.App.Tests.Search;

public sealed class ExcerptTests
{
    private static string Join(IEnumerable<TextFragment> fragments) => string.Concat(fragments.Select(f => f.Text));

    private static string[] Marked(IEnumerable<TextFragment> fragments) => fragments.Where(f => f.Highlight).Select(f => f.Text).ToArray();

    // Terms

    [Fact]
    public void Terms_mix_latin_words_and_cjk_pieces()
    {
        var terms = QueryHighlighter.ExtractTerms("去年 Q3 營收 ABC-123 x");

        // "Q3" and "ABC", "123" are words of two or more characters; "x" is too short; 營收 is a two-character CJK word; 去年 too.
        Assert.Equal(["去年", "Q3", "營收", "ABC", "123"], terms);
    }

    [Fact]
    public void Long_cjk_runs_become_three_character_windows()
    {
        var terms = QueryHighlighter.ExtractTerms("去年給客戶的報價單");

        Assert.Equal(["去年給", "年給客", "給客戶", "客戶的", "戶的報", "的報價", "報價單"], terms);
    }

    [Fact]
    public void Full_width_letters_are_normalized_and_duplicates_dropped()
    {
        var terms = QueryHighlighter.ExtractTerms("Ｑ３ q3 Q3");

        Assert.Equal(["Q3"], terms);
    }

    [Fact]
    public void Blank_query_has_no_terms()
    {
        Assert.Empty(QueryHighlighter.ExtractTerms("  "));
        Assert.Empty(QueryHighlighter.ExtractTerms(null));
        Assert.Empty(QueryHighlighter.ExtractTerms("a - ?"));
    }

    // Splitting

    [Fact]
    public void Split_marks_chinese_and_english_words_in_mixed_text()
    {
        var fragments = QueryHighlighter.Split("本季報價 Quotation 已寄出", ["報價", "quotation"]);

        Assert.Equal("本季報價 Quotation 已寄出", Join(fragments));
        Assert.Equal(["報價", "Quotation"], Marked(fragments));
        Assert.Equal(
            [("本季", false), ("報價", true), (" ", false), ("Quotation", true), (" 已寄出", false)],
            fragments.Select(f => (f.Text, f.Highlight)).ToArray());
    }

    [Fact]
    public void Split_merges_overlapping_terms_into_one_fragment()
    {
        var fragments = QueryHighlighter.Split("附上報價單請確認", ["報價", "價單", "報價單"]);

        Assert.Equal(["報價單"], Marked(fragments));
        Assert.Equal(3, fragments.Count);
    }

    [Fact]
    public void Split_merges_touching_matches()
    {
        var fragments = QueryHighlighter.Split("abcdef", ["abc", "def"]);

        Assert.Equal(["abcdef"], Marked(fragments));
    }

    [Fact]
    public void Split_finds_every_occurrence()
    {
        var fragments = QueryHighlighter.Split("報價, 再報價", ["報價"]);

        Assert.Equal(["報價", "報價"], Marked(fragments));
        Assert.Equal("報價, 再報價", Join(fragments));
    }

    [Fact]
    public void Split_without_a_match_returns_the_whole_text()
    {
        var fragments = QueryHighlighter.Split("沒有關係", ["報價"]);

        Assert.Equal([new TextFragment("沒有關係", false)], fragments);
    }

    // Tables

    private static string Html(int rows, int columns, int headerRows, params (int R, int C, string T)[] cells) =>
        HtmlTableRenderer.Render(new TableModel(rows, columns, cells.Select(c => new TableCell(c.R, c.C, c.T)).ToList(), headerRows));

    [Fact]
    public void Table_html_becomes_column_value_lines()
    {
        var html = Html(3, 3, 1,
            (0, 0, "品名"), (0, 1, "數量"), (0, 2, "金額"),
            (1, 0, "監視系統"), (1, 1, "1 式"), (1, 2, "1,280,000"),
            (2, 0, "安裝"), (2, 1, "2"), (2, 2, "R&D <5>"));

        var text = TableHtmlText.ToPlainText(html);

        Assert.Equal("品名：監視系統；數量：1 式；金額：1,280,000\n品名：安裝；數量：2；金額：R&D <5>", text);
    }

    [Fact]
    public void Table_caption_comes_first_and_empty_cells_are_skipped()
    {
        var html = HtmlTableRenderer.Render(new TableModel(2, 2,
            [new TableCell(0, 0, "名稱"), new TableCell(0, 1, "備註"), new TableCell(1, 0, "甲")], 1, "清單"));

        Assert.Equal("清單\n名稱：甲", TableHtmlText.ToPlainText(html));
    }

    [Fact]
    public void Table_without_a_header_lists_the_values()
    {
        var html = Html(1, 2, 0, (0, 0, "甲"), (0, 1, "乙"));

        Assert.Equal("甲；乙", TableHtmlText.ToPlainText(html));
    }

    [Fact]
    public void Spans_and_multi_level_headers_line_up()
    {
        var html = HtmlTableRenderer.Render(new TableModel(4, 3,
        [
            new TableCell(0, 0, "區域", 2, 1), new TableCell(0, 1, "銷售", 1, 2),
            new TableCell(1, 1, "Q1"), new TableCell(1, 2, "Q2"),
            new TableCell(2, 0, "北", 2, 1), new TableCell(2, 1, "10"), new TableCell(2, 2, "20"),
            new TableCell(3, 1, "30"), new TableCell(3, 2, "40"),
        ], 2));

        var text = TableHtmlText.ToPlainText(html);

        Assert.Equal("區域：北；銷售_Q1：10；銷售_Q2：20\n區域：北；銷售_Q1：30；銷售_Q2：40", text);
    }

    [Fact]
    public void Text_around_a_table_is_kept()
    {
        var html = Html(2, 1, 1, (0, 0, "欄"), (1, 0, "值"));

        var text = TableHtmlText.ToPlainText("前言\n" + html + "\n結語");

        Assert.Equal("前言\n欄：值\n結語", text);
    }

    [Fact]
    public void Plain_text_is_returned_untouched()
    {
        Assert.Equal("a < b 而且 c > d", TableHtmlText.ToPlainText("a < b 而且 c > d"));
    }

    [Fact]
    public void Broken_table_html_does_not_throw()
    {
        var text = TableHtmlText.ToPlainText("<table><tr><td>只有開頭<td");

        Assert.NotNull(text);
    }

    // Excerpt

    [Fact]
    public void Short_text_is_kept_whole()
    {
        var fragments = ExcerptBuilder.Build("本季報價單", ["報價"]);

        Assert.Equal("本季報價單", Join(fragments));
        Assert.Equal(["報價"], Marked(fragments));
    }

    [Fact]
    public void Long_text_is_cut_at_300_characters_with_an_ellipsis()
    {
        var fragments = ExcerptBuilder.Build(new string('字', 1000), []);

        Assert.Equal(300 + 1, Join(fragments).Length);
        Assert.EndsWith("…", Join(fragments));
    }

    [Fact]
    public void Excerpt_moves_to_the_first_match_when_it_is_far_away()
    {
        var text = new string('甲', 600) + "報價單" + new string('乙', 600);

        var fragments = ExcerptBuilder.Build(text, ["報價單"]);

        var joined = Join(fragments);
        Assert.StartsWith("…", joined);
        Assert.EndsWith("…", joined);
        Assert.Equal(["報價單"], Marked(fragments));
        Assert.True(joined.Length <= 300 + 2);
    }

    [Fact]
    public void Table_excerpt_is_plain_text_with_highlights()
    {
        var html = Html(2, 2, 1, (0, 0, "品名"), (0, 1, "報價金額"), (1, 0, "監視系統"), (1, 1, "NT$ 1,280,000"));

        var fragments = ExcerptBuilder.Build(html, ["報價"]);

        Assert.Equal("品名：監視系統；報價金額：NT$ 1,280,000", Join(fragments));
        Assert.Equal(["報價"], Marked(fragments));
    }

    [Fact]
    public void Blank_text_gives_no_fragments()
    {
        Assert.Empty(ExcerptBuilder.Build("   ", ["報價"]));
        Assert.Empty(ExcerptBuilder.Build(null, ["報價"]));
    }
}
