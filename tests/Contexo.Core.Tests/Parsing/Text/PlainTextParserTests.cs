using System.Text;
using Contexo.Core.Abstractions;
using Contexo.Core.Parsing.Text;
using static Contexo.Core.Tests.Parsing.TextParsers.TextParserTestHelper;

namespace Contexo.Core.Tests.Parsing.TextParsers;

public sealed class PlainTextParserTests
{
    private const string Sample = "採購規範第三章：驗收標準與付款條件。\n廠商應於三十日內完成交貨，逾期每日罰款千分之一。\n聯絡人王小明，電話 02-1234-5678，信箱 service@example.com。";

    static PlainTextParserTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private readonly PlainTextParser _parser = new();

    public static TheoryData<string> Encodings => new() { "utf8", "utf8-bom", "utf16-le-bom", "big5" };

    private static byte[] Encode(string name, string text) => name switch
    {
        "utf8" => new UTF8Encoding(false).GetBytes(text),
        "utf8-bom" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)],
        "utf16-le-bom" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
        "big5" => Encoding.GetEncoding(950).GetBytes(text),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Txt_in_each_encoding_restores_traditional_chinese(string encoding)
    {
        var result = await ParseAsync(_parser, "公文.txt", Encode(encoding, Sample));

        var section = Assert.Single(result.Sections);
        Assert.Equal(SectionKind.Prose, section.Kind);
        Assert.Equal(Sample, section.Text);
    }

    [Fact]
    public async Task Crlf_line_endings_are_normalized()
    {
        var result = await ParseAsync(_parser, "a.txt", "第一行\r\n第二行\r\n");

        Assert.Equal("第一行\n第二行", Assert.Single(result.Sections).Text);
    }

    [Fact]
    public async Task Markdown_headings_build_the_heading_path()
    {
        const string md = "前言文字\n\n# 採購規範\n總則內容\n\n## 第 3 章 驗收\n驗收說明\n\n### 3.2 驗收標準\n標準內容\n\n## 第 4 章 付款\n付款內容\n\n# 附錄\n附錄內容\n";

        var result = await ParseAsync(_parser, "規範.md", md);

        Assert.Equal(6, result.Sections.Count);
        Assert.Null(result.Sections[0].Location.HeadingPath);
        Assert.Equal(["採購規範"], result.Sections[1].Location.HeadingPath);
        Assert.Equal(["採購規範", "第 3 章 驗收"], result.Sections[2].Location.HeadingPath);
        Assert.Equal(["採購規範", "第 3 章 驗收", "3.2 驗收標準"], result.Sections[3].Location.HeadingPath);
        Assert.Equal(["採購規範", "第 4 章 付款"], result.Sections[4].Location.HeadingPath);
        Assert.Equal(["附錄"], result.Sections[5].Location.HeadingPath);
        Assert.Contains("標準內容", result.Sections[3].Text);
        Assert.All(result.Sections, s => Assert.Equal(SectionKind.Prose, s.Kind));
    }

    [Fact]
    public async Task Hash_lines_inside_code_blocks_are_not_headings()
    {
        const string md = "# 安裝說明\n步驟如下：\n```bash\n# 這是註解不是標題\n## 也不是\necho hi\n```\n結尾文字\n\n~~~\n# 波浪線圍欄內\n~~~\n";

        var result = await ParseAsync(_parser, "readme.markdown", md);

        var section = Assert.Single(result.Sections);
        Assert.Equal(["安裝說明"], section.Location.HeadingPath);
        Assert.Contains("# 這是註解不是標題", section.Text);
        Assert.Contains("# 波浪線圍欄內", section.Text);
        Assert.Contains("結尾文字", section.Text);
    }

    [Fact]
    public async Task Hash_without_space_is_not_a_heading_and_closing_hashes_are_removed()
    {
        var result = await ParseAsync(_parser, "a.md", "#hashtag 不是標題\n\n## 真標題 ##\n內容");

        Assert.Equal(2, result.Sections.Count);
        Assert.Null(result.Sections[0].Location.HeadingPath);
        Assert.Equal(["真標題"], result.Sections[1].Location.HeadingPath);
    }

    [Fact]
    public async Task Markdown_with_only_a_heading_still_yields_a_section()
    {
        var result = await ParseAsync(_parser, "a.md", "# 只有標題");

        Assert.Equal(["只有標題"], Assert.Single(result.Sections).Location.HeadingPath);
    }

    [Fact]
    public async Task Json_is_indented_and_keeps_chinese()
    {
        var result = await ParseAsync(_parser, "設定.json", "{\"名稱\":\"文脈\",\"清單\":[1,2],}");

        var text = Assert.Single(result.Sections).Text;
        Assert.Contains("\"名稱\": \"文脈\"", text);
        Assert.Contains('\n', text);
    }

    [Fact]
    public async Task Broken_json_is_treated_as_plain_text()
    {
        const string broken = "{\"名稱\": \"文脈\", ";

        var result = await ParseAsync(_parser, "壞.json", broken);

        Assert.Equal(broken.Trim(), Assert.Single(result.Sections).Text);
    }

    [Fact]
    public async Task Xml_keeps_only_text_nodes_one_per_line()
    {
        const string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<訂單 id=\"A1\"><客戶>王小明</客戶><!-- 註解 --><品項><名稱>筆記本</名稱><數量>3</數量></品項><備註><![CDATA[請準時送達]]></備註></訂單>";

        var result = await ParseAsync(_parser, "訂單.xml", xml);

        Assert.Equal("王小明\n筆記本\n3\n請準時送達", Assert.Single(result.Sections).Text);
    }

    [Fact]
    public async Task Broken_xml_is_treated_as_plain_text()
    {
        const string broken = "<a><b>未關閉的標籤";

        var result = await ParseAsync(_parser, "壞.xml", broken);

        Assert.Equal(broken, Assert.Single(result.Sections).Text);
    }

    [Fact]
    public async Task Xml_with_dtd_does_not_throw_or_resolve_entities()
    {
        const string xml = "<!DOCTYPE a [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><a>&x;</a>";

        var result = await ParseAsync(_parser, "dtd.xml", xml);

        Assert.DoesNotContain("root:", AllText(result));
    }

    [Fact]
    public async Task Log_over_the_limit_keeps_the_tail_and_warns()
    {
        var lines = Enumerable.Range(1, 200).Select(i => $"2025-01-01 第 {i:000} 行記錄");
        var options = new ParserOptions { MaxExtractedChars = 300 };

        var result = await ParseAsync(_parser, "app.log", string.Join("\n", lines), options);

        var text = Assert.Single(result.Sections).Text;
        Assert.True(text.Length <= 300);
        Assert.EndsWith("第 200 行記錄", text);
        Assert.DoesNotContain("第 001 行", text);
        Assert.StartsWith("2025", text);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task Txt_over_the_limit_keeps_the_head_and_warns()
    {
        var lines = Enumerable.Range(1, 200).Select(i => $"第 {i:000} 行內容");
        var options = new ParserOptions { MaxExtractedChars = 300 };

        var result = await ParseAsync(_parser, "big.txt", string.Join("\n", lines), options);

        var text = Assert.Single(result.Sections).Text;
        Assert.True(text.Length <= 300);
        Assert.StartsWith("第 001 行", text);
        Assert.Single(result.Warnings);
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("a.md")]
    [InlineData("a.json")]
    [InlineData("a.xml")]
    [InlineData("a.log")]
    public async Task Empty_and_whitespace_only_files_return_empty(string fileName)
    {
        Assert.Same(ParsedDocument.Empty, await ParseAsync(_parser, fileName, Array.Empty<byte>()));
        Assert.Same(ParsedDocument.Empty, await ParseAsync(_parser, fileName, "  \r\n\t \n"));
    }

    [Theory]
    [InlineData("a.txt", "內容")]
    [InlineData("a.md", "# 標題\n內容")]
    [InlineData("a.json", "{\"a\":1}")]
    [InlineData("a.xml", "<a>內容</a>")]
    [InlineData("a.log", "內容")]
    public async Task EmbeddedPath_is_copied_to_every_section(string fileName, string content)
    {
        string[] path = ["外層.docx", "內嵌.txt"];

        var result = await ParseAsync(_parser, fileName, content, embeddedPath: path);

        Assert.NotEmpty(result.Sections);
        Assert.All(result.Sections, s => Assert.Equal(path, s.Location.EmbeddedPath));
    }

    [Fact]
    public async Task EmbeddedPath_is_null_for_top_level_files_and_stream_is_left_open()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("內容"));

        var result = await _parser.ParseAsync(new ParseContext(stream, "a.txt", new ParserOptions()), CancellationToken.None);

        Assert.Null(Assert.Single(result.Sections).Location.EmbeddedPath);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void Supported_extensions_cover_the_text_formats()
    {
        Assert.Equivalent(new[] { ".txt", ".md", ".markdown", ".json", ".xml", ".log" }, _parser.SupportedExtensions);
    }
}
