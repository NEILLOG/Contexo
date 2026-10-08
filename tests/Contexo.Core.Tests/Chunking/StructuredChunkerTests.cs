using System.Globalization;
using System.Text;
using Contexo.Core.Abstractions;
using Contexo.Core.Chunking;
using Contexo.Core.Common;

namespace Contexo.Core.Tests.Chunking;

public sealed class StructuredChunkerTests
{
    private static readonly ChunkingOptions Defaults = new();

    private static IReadOnlyList<Chunk> Run(IReadOnlyList<DocumentSection> sections, ChunkingOptions? options = null, string title = "文件")
        => new StructuredChunker().Split(title, sections, options ?? Defaults);

    private static DocumentSection Prose(string text, params string[] headings) =>
        new(SectionKind.Prose, text, new SourceLocation { HeadingPath = headings });

    private static int Len(string text) => new StringInfo(text).LengthInTextElements;

    /// <summary>Sentences that are all different, 22 characters each.</summary>
    private static string Sentences(int count, int start = 0)
    {
        var sb = new StringBuilder();
        for (var i = start; i < start + count; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"第{i:D3}句：這是用來測試切塊功能的句子內容。");
        }

        return sb.ToString();
    }

    /// <summary>Joins consecutive chunks of one section back together by removing the overlap.</summary>
    private static string Reassemble(IReadOnlyList<Chunk> chunks, int maxOverlap)
    {
        var result = chunks[0].Text;
        for (var i = 1; i < chunks.Count; i++)
        {
            var next = chunks[i].Text;
            var k = 0;
            for (var candidate = Math.Min(maxOverlap, Math.Min(next.Length, result.Length)); candidate > 0; candidate--)
            {
                if (result.EndsWith(next[..candidate], StringComparison.Ordinal))
                {
                    k = candidate;
                    break;
                }
            }

            result += next[k..];
        }

        return result;
    }

    private static string RenderTable(int dataRows, int headerRows = 1, string? caption = null, int columns = 3, Func<int, int, string>? cellText = null)
    {
        cellText ??= (r, c) => $"R{r:D3}C{c}值";
        var cells = new List<TableCell>();
        for (var r = 0; r < dataRows + headerRows; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                cells.Add(new TableCell(r, c, r < headerRows ? $"欄{c}" : cellText(r, c)));
            }
        }

        return HtmlTableRenderer.Render(new TableModel(dataRows + headerRows, columns, cells, headerRows, caption));
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    // 1. Long prose

    [Fact]
    public void Long_chinese_text_is_split_with_overlap_and_nothing_is_lost()
    {
        var original = Sentences(91); // about 2000 characters
        Assert.InRange(original.Length, 1990, 2010);

        var chunks = Run([Prose(original, "章")]);

        Assert.True(chunks.Count >= 4);
        Assert.All(chunks, c => Assert.True(Len(c.Text) <= Defaults.MaxChars + Defaults.OverlapChars, $"chunk {c.Ordinal} has {Len(c.Text)} characters"));
        for (var i = 1; i < chunks.Count; i++)
        {
            // The next chunk starts with the end of the previous one.
            var overlapFound = false;
            for (var k = 1; k <= Defaults.OverlapChars; k++)
            {
                if (chunks[i - 1].Text.EndsWith(chunks[i].Text[..k], StringComparison.Ordinal))
                {
                    overlapFound = true;
                }
            }

            Assert.True(overlapFound, $"no overlap between chunk {i - 1} and {i}");
        }

        Assert.Equal(original, Reassemble(chunks, Defaults.OverlapChars));
    }

    [Fact]
    public void Text_without_any_punctuation_is_cut_by_characters_with_character_overlap()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 2000; i++)
        {
            sb.Append((char)(0x4E00 + i)); // 2000 different characters, no sentence ends
        }

        var original = sb.ToString();
        var chunks = Run([Prose(original)]);

        Assert.True(chunks.Count >= 4);
        Assert.All(chunks, c => Assert.True(Len(c.Text) <= 580));
        Assert.Equal(original, Reassemble(chunks, 80));
        Assert.Equal(80, chunks[0].Text[^80..].Length);
        Assert.StartsWith(chunks[0].Text[^80..], chunks[1].Text, StringComparison.Ordinal);
    }

    // 2. Cut points

    [Fact]
    public void Chunks_end_at_sentence_boundaries()
    {
        var chunks = Run([Prose(Sentences(91))]);

        Assert.All(chunks, c => Assert.EndsWith("。", c.Text, StringComparison.Ordinal));
        // Overlap is made of whole sentences.
        Assert.All(chunks.Skip(1), c => Assert.StartsWith("第", c.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void Chunks_break_at_paragraph_boundaries()
    {
        var first = Sentences(13);
        var second = Sentences(13, 100);
        Assert.InRange(Len(first), 200, 400);

        var chunks = Run([Prose(first + "\n" + second)]);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(first, chunks[0].Text);
        Assert.EndsWith("\n" + second, chunks[1].Text, StringComparison.Ordinal);
        Assert.StartsWith("第", chunks[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Paragraphs_are_accumulated_until_the_limit()
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 20).Select(i => $"段落{i:D2}：" + new string('字', 40) + "。"));

        var chunks = Run([Prose(text)]);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(Len(c.Text) <= 580));
        Assert.Contains("\n\n", chunks[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dot_between_digits_or_inside_a_word_is_not_a_sentence_end()
    {
        Assert.Equal(["Pi is 3.14 today. ", "Next one."], ProseSplitter.Sentences("Pi is 3.14 today. Next one.").ToArray());
        Assert.Equal(["版本 v1.2.3 發佈！", "請更新。"], ProseSplitter.Sentences("版本 v1.2.3 發佈！請更新。").ToArray());
        Assert.Equal(["他說：「好。」", "然後走了。"], ProseSplitter.Sentences("他說：「好。」然後走了。").ToArray());
    }

    [Fact]
    public void Overlap_never_pushes_a_chunk_past_max_plus_overlap_even_with_blank_lines()
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 60).Select(i => $"第{i:D2}段，" + new string('文', 70) + "。"));

        var chunks = Run([Prose(text)]);

        Assert.All(chunks, c => Assert.True(Len(c.Text) <= 580, $"chunk {c.Ordinal}: {Len(c.Text)}"));
    }

    [Fact]
    public void A_single_over_long_sentence_is_cut_at_max_chars()
    {
        var original = new string('字', 1300) + "。";

        var chunks = Run([Prose(original)]);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.True(Len(c.Text) <= 580));
        Assert.Equal(original, Reassemble(chunks, 80));
    }

    // 3. Merging

    [Fact]
    public void Short_pieces_with_the_same_heading_are_merged()
    {
        var chunks = Run(
        [
            Prose("短段落一。", "章", "節"),
            Prose("短段落二。", "章", "節"),
            Prose("短段落三。", "章", "節"),
        ]);

        var chunk = Assert.Single(chunks);
        Assert.Contains("短段落一。", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("短段落三。", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Short_lines_inside_one_section_are_merged()
    {
        var chunks = Run([Prose("甲。\n乙。\n丙。")]);

        Assert.Equal("甲。\n乙。\n丙。", Assert.Single(chunks).Text);
    }

    [Fact]
    public void Pieces_with_different_heading_paths_are_not_merged()
    {
        var chunks = Run(
        [
            Prose("短段落一。", "章", "甲"),
            Prose("短段落二。", "章", "乙"),
        ]);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(["章", "甲"], chunks[0].Location.HeadingPath);
        Assert.Equal(["章", "乙"], chunks[1].Location.HeadingPath);
    }

    [Fact]
    public void Pdf_pages_are_not_merged_even_when_short()
    {
        var chunks = Run(
        [
            new DocumentSection(SectionKind.Prose, "第一頁很短。", new SourceLocation { Page = 1 }),
            new DocumentSection(SectionKind.Prose, "第二頁很短。", new SourceLocation { Page = 2 }),
        ]);

        Assert.Equal(2, chunks.Count);
    }

    [Fact]
    public void A_short_piece_is_not_merged_when_the_result_would_exceed_max_chars()
    {
        var big = new string('字', 497) + "。";
        var chunks = Run([Prose(big, "章"), Prose("短。", "章")]);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("短。", chunks[1].Text);
    }

    [Fact]
    public void A_short_leading_piece_is_merged_into_the_following_one()
    {
        var big = new string('字', 470) + "。";
        var chunks = Run([Prose("標題一行。", "章"), Prose(big, "章")]);

        var chunk = Assert.Single(chunks);
        Assert.StartsWith("標題一行。\n", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlap_is_not_added_between_different_sections()
    {
        var chunks = Run([Prose(Sentences(5), "甲"), Prose(Sentences(5, 50), "乙")]);

        Assert.Equal(2, chunks.Count);
        Assert.StartsWith("第050句", chunks[1].Text, StringComparison.Ordinal);
    }

    // 4. Tables

    [Fact]
    public void A_small_table_is_one_chunk()
    {
        var html = RenderTable(5);

        var chunks = Run([new DocumentSection(SectionKind.Table, html, SourceLocation.None, KeepWhole: true)]);

        var chunk = Assert.Single(chunks);
        Assert.Equal(SectionKind.Table, chunk.Kind);
        Assert.Equal(html, chunk.Text);
    }

    [Fact]
    public void A_huge_table_is_split_into_complete_tables_that_repeat_the_header()
    {
        var html = RenderTable(300, caption: "報價 & 明細");
        var options = new ChunkingOptions { HardMaxChars = 1000 };
        Assert.True(Len(html) > 4000);

        var chunks = Run([new DocumentSection(SectionKind.Table, html, new SourceLocation { Sheet = "Sheet1" }, KeepWhole: true)], options);

        Assert.True(chunks.Count >= 4);
        Assert.All(chunks, c =>
        {
            Assert.True(Len(c.Text) <= 1000, $"{Len(c.Text)}");
            Assert.StartsWith("<table>\n<caption>報價 &amp; 明細</caption>\n<thead>\n<tr><th>欄0</th><th>欄1</th><th>欄2</th></tr>\n</thead>\n<tbody>\n", c.Text, StringComparison.Ordinal);
            Assert.EndsWith("</tbody>\n</table>", c.Text, StringComparison.Ordinal);
            Assert.Equal(1, Count(c.Text, "<table>"));
            Assert.Equal(1, Count(c.Text, "</table>"));
            Assert.Equal(Count(c.Text, "<tr>"), Count(c.Text, "</tr>"));
            Assert.Equal(SectionKind.Table, c.Kind);
            Assert.Equal("Sheet1", c.Location.Sheet);
        });

        // Every data row appears exactly once.
        for (var r = 1; r <= 300; r++)
        {
            Assert.Equal(1, chunks.Sum(c => Count(c.Text, $"<td>R{r:D3}C0值</td>")));
        }
    }

    [Fact]
    public void A_huge_table_without_thead_repeats_the_first_row()
    {
        var html = RenderTable(200, headerRows: 0);
        var firstRow = html.Split('\n').First(l => l.StartsWith("<tr>", StringComparison.Ordinal));
        var options = new ChunkingOptions { HardMaxChars = 800 };

        var chunks = Run([new DocumentSection(SectionKind.Table, html, SourceLocation.None, KeepWhole: true)], options);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c =>
        {
            Assert.True(Len(c.Text) <= 800);
            Assert.StartsWith("<table>\n<tbody>\n" + firstRow + "\n", c.Text, StringComparison.Ordinal);
        });
        Assert.Equal(200 + chunks.Count - 1, chunks.Sum(c => Count(c.Text, "<tr>")));
    }

    [Fact]
    public void A_row_larger_than_the_limit_is_divided_without_losing_text_or_breaking_tags()
    {
        var html = RenderTable(3, columns: 2, cellText: (r, c) => c == 0 ? $"左{r}" : new string('右', 700) + "<>&");
        var options = new ChunkingOptions { HardMaxChars = 400 };

        var chunks = Run([new DocumentSection(SectionKind.Table, html, SourceLocation.None, KeepWhole: true)], options);

        Assert.True(chunks.Count >= 6);
        Assert.All(chunks, c =>
        {
            Assert.True(Len(c.Text) <= 400, $"{Len(c.Text)}");
            Assert.Equal(Count(c.Text, "<tr>"), Count(c.Text, "</tr>"));
            Assert.Equal(Count(c.Text, "<td>"), Count(c.Text, "</td>"));
            Assert.DoesNotContain("&a\n", c.Text, StringComparison.Ordinal);
        });
        Assert.Equal(3 * 700, chunks.Sum(c => Count(c.Text, "右")));
        Assert.Equal(3, chunks.Sum(c => Count(c.Text, "&lt;&gt;&amp;")));
    }

    [Fact]
    public void A_table_with_unrecognised_markup_falls_back_to_paragraph_splitting()
    {
        var text = string.Join("\n", Enumerable.Range(0, 100).Select(i => $"第{i:D3}列，內容文字文字文字文字文字文字文字文字。"));

        var chunks = Run([new DocumentSection(SectionKind.Table, text, SourceLocation.None, KeepWhole: true)], new ChunkingOptions { HardMaxChars = 500 });

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(Len(c.Text) <= 500));
    }

    // 5. Table summaries

    [Fact]
    public void A_table_summary_is_never_split_and_keeps_its_key()
    {
        var text = "檔案：報價.xlsx\n" + string.Join("\n", Enumerable.Range(0, 400).Select(i => $"欄位{i}：範例值範例值範例值"));
        Assert.True(Len(text) > Defaults.HardMaxChars);

        var chunks = Run([new DocumentSection(SectionKind.TableSummary, text, new SourceLocation { Sheet = "明細", CellRange = "A1:Z999" }, KeepWhole: true, TableKey: "明細!A1:Z999")]);

        var chunk = Assert.Single(chunks);
        Assert.Equal(SectionKind.TableSummary, chunk.Kind);
        Assert.Equal("明細!A1:Z999", chunk.TableKey);
        Assert.Equal(text, chunk.Text);
        Assert.StartsWith("文件 › 明細\n", chunk.EmbeddingText, StringComparison.Ordinal);
    }

    // Slides, notes, diagrams, KeepWhole

    [Theory]
    [InlineData(SectionKind.Slide)]
    [InlineData(SectionKind.Notes)]
    [InlineData(SectionKind.Diagram)]
    public void Slides_notes_and_diagrams_stay_whole_when_they_fit(SectionKind kind)
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 30).Select(i => $"要點 {i}：" + new string('字', 50)));
        Assert.InRange(Len(text), Defaults.MaxChars + 1, Defaults.HardMaxChars);

        var chunks = Run([new DocumentSection(kind, text, new SourceLocation { Slide = 3 })]);

        var chunk = Assert.Single(chunks);
        Assert.Equal(kind, chunk.Kind);
        Assert.Equal(text, chunk.Text);
    }

    [Fact]
    public void An_oversized_slide_is_split_by_paragraph_without_overlap()
    {
        var paragraphs = Enumerable.Range(0, 40).Select(i => $"要點{i:D2}：" + new string('字', 100) + "。").ToList();
        var options = new ChunkingOptions { HardMaxChars = 1000 };

        var chunks = Run([new DocumentSection(SectionKind.Slide, string.Join("\n\n", paragraphs), new SourceLocation { Slide = 2 })], options);

        Assert.True(chunks.Count >= 4);
        Assert.All(chunks, c =>
        {
            Assert.True(Len(c.Text) <= 1000);
            Assert.Equal(SectionKind.Slide, c.Kind);
        });
        Assert.Equal(string.Join("\n\n", paragraphs), string.Join("\n\n", chunks.Select(c => c.Text)));
    }

    [Fact]
    public void Keep_whole_prose_is_not_split_below_the_hard_limit()
    {
        var text = Sentences(60); // about 1300 characters

        var whole = Run([new DocumentSection(SectionKind.Prose, text, SourceLocation.None, KeepWhole: true)]);
        var split = Run([Prose(text)]);

        Assert.Equal(text, Assert.Single(whole).Text);
        Assert.True(split.Count > 1);
    }

    [Fact]
    public void Keep_whole_prose_over_the_hard_limit_is_split()
    {
        var chunks = Run([new DocumentSection(SectionKind.Prose, Sentences(60), SourceLocation.None, KeepWhole: true)], new ChunkingOptions { HardMaxChars = 600 });

        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.True(Len(c.Text) <= 600));
    }

    // 6. Embedding text

    [Fact]
    public void Embedding_text_for_word_uses_the_heading_path()
    {
        var chunks = Run([Prose("內文很短。", "採購規範", "第 3 章 驗收", "3.2 驗收標準")], title: "合約");

        Assert.Equal("合約 › 採購規範 › 第 3 章 驗收 › 3.2 驗收標準\n內文很短。", Assert.Single(chunks).EmbeddingText);
    }

    [Fact]
    public void Embedding_text_for_powerpoint_has_slide_number_and_title()
    {
        var section = new DocumentSection(SectionKind.Slide, "市場分析\n\n成長 20%", new SourceLocation { Slide = 5, Title = "市場分析" });

        var chunk = Assert.Single(Run([section], title: "簡報"));

        Assert.Equal("簡報 › 第 5 張投影片 › 市場分析\n市場分析\n\n成長 20%", chunk.EmbeddingText);
    }

    [Fact]
    public void Embedding_text_for_excel_has_the_sheet_name()
    {
        var section = new DocumentSection(SectionKind.Table, RenderTable(2), new SourceLocation { Sheet = "報價", CellRange = "A1:C3" }, KeepWhole: true);

        var chunk = Assert.Single(Run([section], title: "訂單"));

        Assert.StartsWith("訂單 › 報價\n<table>", chunk.EmbeddingText, StringComparison.Ordinal);
    }

    [Fact]
    public void Embedding_text_for_pdf_has_the_page_number()
    {
        var section = new DocumentSection(SectionKind.Prose, "這一頁的文字。", new SourceLocation { Page = 12 });

        Assert.Equal("手冊 › 第 12 頁\n這一頁的文字。", Assert.Single(Run([section], title: "手冊")).EmbeddingText);
    }

    [Fact]
    public void Embedding_text_for_an_embedded_file_includes_its_name_first()
    {
        var location = new SourceLocation { EmbeddedPath = ["內嵌.xlsx", "更深.docx"], Sheet = "Sheet1", HeadingPath = ["標題"], Title = "圖表" };
        var chunk = Assert.Single(Run([new DocumentSection(SectionKind.Prose, "資料。", location)], title: "簡報"));

        Assert.Equal("簡報 › 內嵌.xlsx › 更深.docx › Sheet1 › 標題 › 圖表\n資料。", chunk.EmbeddingText);
    }

    [Fact]
    public void Title_equal_to_the_last_heading_is_not_repeated_and_missing_parts_are_skipped()
    {
        var withTitle = new SourceLocation { HeadingPath = ["章", "表格"], Title = "表格" };

        Assert.Equal("文件 › 章 › 表格\n字。", Assert.Single(Run([new DocumentSection(SectionKind.Prose, "字。", withTitle)])).EmbeddingText);
        Assert.Equal("字。", Assert.Single(Run([new DocumentSection(SectionKind.Prose, "字。", SourceLocation.None)], title: "")).EmbeddingText);
    }

    [Fact]
    public void Location_and_kind_are_copied_from_the_section()
    {
        var location = new SourceLocation { Slide = 2, Title = "T" };
        var chunk = Assert.Single(Run([new DocumentSection(SectionKind.Notes, "備忘", location)]));

        Assert.Same(location, chunk.Location);
        Assert.Equal(SectionKind.Notes, chunk.Kind);
        Assert.Null(chunk.TableKey);
    }

    // 7. Ordinals and blanks

    [Fact]
    public void Ordinals_are_consecutive_and_blank_sections_are_skipped()
    {
        var chunks = Run(
        [
            Prose("   \n\n  ", "空"),
            new DocumentSection(SectionKind.Slide, "", new SourceLocation { Slide = 1 }),
            new DocumentSection(SectionKind.TableSummary, " ", SourceLocation.None, true, "k"),
            Prose(Sentences(60), "章"),
            new DocumentSection(SectionKind.Table, RenderTable(2), SourceLocation.None, KeepWhole: true),
            new DocumentSection(SectionKind.Notes, "備忘內容", new SourceLocation { Slide = 2 }),
        ]);

        Assert.True(chunks.Count >= 4);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Ordinal));
        Assert.All(chunks, c => Assert.False(string.IsNullOrWhiteSpace(c.Text)));
        Assert.Equal(SectionKind.Notes, chunks[^1].Kind);
    }

    [Fact]
    public void No_sections_gives_no_chunks()
    {
        Assert.Empty(Run([]));
    }

    [Fact]
    public void Windows_and_old_mac_line_endings_are_handled()
    {
        var chunk = Assert.Single(Run([Prose("甲。\r\n乙。\r丙。")]));

        Assert.Equal("甲。\n乙。\n丙。", chunk.Text);
    }

    [Fact]
    public void Custom_options_are_respected()
    {
        var options = new ChunkingOptions { MaxChars = 100, OverlapChars = 20, MinChars = 10 };

        var chunks = Run([Prose(Sentences(20))], options);

        Assert.True(chunks.Count >= 4);
        Assert.All(chunks, c => Assert.True(Len(c.Text) <= 120));
        Assert.Equal(Sentences(20), Reassemble(chunks, 20));
    }

    [Fact]
    public void Zero_overlap_gives_no_repeated_text()
    {
        var chunks = Run([Prose(Sentences(40))], new ChunkingOptions { OverlapChars = 0 });

        Assert.True(chunks.Count > 1);
        Assert.Equal(Sentences(40), string.Concat(chunks.Select(c => c.Text)));
    }

    // 8. Emoji and rare characters

    [Fact]
    public void Emoji_and_rare_characters_are_never_cut_in_the_middle()
    {
        var unit = "😀𠀀👨‍👩‍👧é"; // emoji, U+20000, ZWJ family sequence, e + combining acute
        var original = string.Concat(Enumerable.Repeat(unit, 400));
        var options = new ChunkingOptions { MaxChars = 100, OverlapChars = 10 };

        var chunks = Run([Prose(original)], options);

        Assert.True(chunks.Count > 5);
        foreach (var chunk in chunks)
        {
            Assert.True(Len(chunk.Text) <= 110, $"{Len(chunk.Text)}");
            AssertNoBrokenSurrogates(chunk.Text);
            Assert.False(chunk.Text.StartsWith('́'));
            Assert.False(chunk.Text.StartsWith('‍'));
            Assert.False(char.IsLowSurrogate(chunk.Text[0]));
            // Whole units only: text elements start with one of the unit's first characters.
            var elements = StringInfo.GetTextElementEnumerator(chunk.Text);
            while (elements.MoveNext())
            {
                Assert.Contains((string)elements.Current, new[] { "😀", "𠀀", "👨‍👩‍👧", "é" });
            }
        }

        Assert.Equal(original, Reassemble(chunks, 40));
    }

    [Fact]
    public void Rare_cjk_characters_count_as_one_each()
    {
        var original = string.Concat(Enumerable.Range(0x20000, 1200).Select(char.ConvertFromUtf32));

        var chunks = Run([Prose(original)]);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c =>
        {
            Assert.True(Len(c.Text) <= 580);
            AssertNoBrokenSurrogates(c.Text);
        });
    }

    private static void AssertNoBrokenSurrogates(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                Assert.True(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]), "lone high surrogate");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(text[i]), "lone low surrogate");
            }
        }
    }

    // Misc

    [Fact]
    public void Concurrent_calls_give_identical_results()
    {
        var chunker = new StructuredChunker();
        var sections = new[] { Prose(Sentences(91), "章") };

        var results = Enumerable.Range(0, 8).AsParallel()
            .Select(_ => string.Join("|", chunker.Split("文件", sections, Defaults).Select(c => c.EmbeddingText)))
            .Distinct()
            .ToList();

        Assert.Single(results);
    }
}
