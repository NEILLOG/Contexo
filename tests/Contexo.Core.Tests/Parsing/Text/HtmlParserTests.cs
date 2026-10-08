using System.Text;
using Contexo.Core.Abstractions;
using Contexo.Core.Parsing.Text;
using static Contexo.Core.Tests.Parsing.TextParsers.TextParserTestHelper;

namespace Contexo.Core.Tests.Parsing.TextParsers;

public sealed class HtmlParserTests
{
    static HtmlParserTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private readonly HtmlParser _parser = new();

    [Fact]
    public async Task Unwanted_elements_are_removed()
    {
        const string html = """
            <html><head><title>標題列</title><style>body{color:red}</style></head>
            <body>
            <header>網站抬頭</header><nav><a href="/">首頁</a></nav>
            <script>var secret = "不要出現";</script><noscript>請開啟 JavaScript</noscript>
            <p>這是正文第一段。</p><p>這是正文第二段。</p>
            <footer>版權所有</footer>
            </body></html>
            """;

        var result = await ParseAsync(_parser, "a.html", html);

        var text = AllText(result);
        Assert.Contains("這是正文第一段。\n這是正文第二段。", text);
        foreach (var unwanted in new[] { "不要出現", "color:red", "網站抬頭", "首頁", "請開啟", "版權所有", "標題列" })
        {
            Assert.DoesNotContain(unwanted, text);
        }
    }

    [Fact]
    public async Task Entities_are_decoded_and_nbsp_becomes_space()
    {
        var result = await ParseAsync(_parser, "a.htm", "<p>A&amp;B&nbsp;C &lt;tag&gt; &#20013;&#25991;</p>");

        Assert.Equal("A&B C <tag> 中文", Assert.Single(result.Sections).Text);
    }

    [Fact]
    public async Task Headings_split_sections_and_build_the_heading_path()
    {
        const string html = """
            <body>
            <p>前言</p>
            <h1>採購規範</h1><p>總則內容</p>
            <h2>第 3 章 驗收</h2><p>驗收說明<br>第二行</p>
            <h3>3.2 標準</h3><ul><li>項目一</li><li>項目二</li></ul>
            <h2>第 4 章 付款</h2><div><span>付款</span><span>內容</span></div>
            <h1>附錄</h1><p>附錄內容</p>
            </body>
            """;

        var result = await ParseAsync(_parser, "a.html", html);

        Assert.Equal(6, result.Sections.Count);
        Assert.Null(result.Sections[0].Location.HeadingPath);
        Assert.Equal("前言", result.Sections[0].Text);
        Assert.Equal(["採購規範"], result.Sections[1].Location.HeadingPath);
        Assert.Equal("採購規範\n總則內容", result.Sections[1].Text);
        Assert.Equal(["採購規範", "第 3 章 驗收"], result.Sections[2].Location.HeadingPath);
        Assert.Equal("第 3 章 驗收\n驗收說明\n第二行", result.Sections[2].Text);
        Assert.Equal(["採購規範", "第 3 章 驗收", "3.2 標準"], result.Sections[3].Location.HeadingPath);
        Assert.Equal("3.2 標準\n項目一\n項目二", result.Sections[3].Text);
        Assert.Equal(["採購規範", "第 4 章 付款"], result.Sections[4].Location.HeadingPath);
        Assert.Contains("付款內容", result.Sections[4].Text);
        Assert.Equal(["附錄"], result.Sections[5].Location.HeadingPath);
    }

    [Fact]
    public async Task Table_with_rowspan_colspan_and_th_becomes_a_whole_table_section()
    {
        const string html = """
            <h1>報價</h1>
            <p>請看下表</p>
            <table>
              <caption>報價單</caption>
              <tr><th rowspan="2">品名</th><th colspan="2">價格</th></tr>
              <tr><th>單價</th><th>總價</th></tr>
              <tr><td>筆記本</td><td>30</td><td>90</td></tr>
              <tr><td colspan="3">備註：含稅 &amp; 運費</td></tr>
            </table>
            <p>表格之後</p>
            """;

        var result = await ParseAsync(_parser, "a.html", html);

        Assert.Equal(3, result.Sections.Count);
        Assert.Equal(SectionKind.Prose, result.Sections[0].Kind);
        Assert.Equal("報價\n請看下表", result.Sections[0].Text);

        var table = result.Sections[1];
        Assert.Equal(SectionKind.Table, table.Kind);
        Assert.True(table.KeepWhole);
        Assert.Equal(["報價"], table.Location.HeadingPath);
        const string expected =
            "<table>\n<caption>報價單</caption>\n<thead>\n" +
            "<tr><th rowspan=\"2\">品名</th><th colspan=\"2\">價格</th></tr>\n" +
            "<tr><th>單價</th><th>總價</th></tr>\n</thead>\n<tbody>\n" +
            "<tr><td>筆記本</td><td>30</td><td>90</td></tr>\n" +
            "<tr><td colspan=\"3\">備註：含稅 &amp; 運費</td></tr>\n</tbody>\n</table>";
        Assert.Equal(expected, table.Text);

        Assert.Equal(SectionKind.Prose, result.Sections[2].Kind);
        Assert.Equal(["報價"], result.Sections[2].Location.HeadingPath);
        Assert.Equal("表格之後", result.Sections[2].Text);
    }

    [Fact]
    public async Task Thead_marks_header_rows_and_cells_covered_by_rowspan_are_skipped()
    {
        const string html = """
            <table>
              <thead><tr><td>部門</td><td>人員</td></tr></thead>
              <tbody>
                <tr><td rowspan="2">業務部</td><td>甲</td></tr>
                <tr><td>乙</td></tr>
              </tbody>
            </table>
            """;

        var result = await ParseAsync(_parser, "a.html", html);

        var table = Assert.Single(result.Sections);
        Assert.Equal(
            "<table>\n<thead>\n<tr><th>部門</th><th>人員</th></tr>\n</thead>\n<tbody>\n" +
            "<tr><td rowspan=\"2\">業務部</td><td>甲</td></tr>\n<tr><td>乙</td></tr>\n</tbody>\n</table>",
            table.Text);
    }

    [Fact]
    public async Task Nested_table_is_flattened_into_its_cell()
    {
        const string html = "<table><tr><td>外層</td><td><table><tr><td>內一</td><td>內二</td></tr></table></td></tr></table>";

        var result = await ParseAsync(_parser, "a.html", html);

        var table = Assert.Single(result.Sections);
        Assert.Equal(SectionKind.Table, table.Kind);
        Assert.Contains("<td>外層</td>", table.Text);
        Assert.Contains("內一", table.Text);
        Assert.Contains("內二", table.Text);
        Assert.Equal(1, table.Text.Split("<tr>").Length - 1);
    }

    [Fact]
    public async Task Pre_keeps_its_line_breaks()
    {
        var result = await ParseAsync(_parser, "a.html", "<pre>第一行\n  第二行縮排</pre>");

        Assert.Equal("第一行\n  第二行縮排", Assert.Single(result.Sections).Text);
    }

    [Fact]
    public async Task Meta_charset_is_used_before_detection()
    {
        const string body = "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=big5\"></head><body><p>驗收標準與付款條件，請於三十日內完成匯款。</p></body></html>";

        var result = await ParseAsync(_parser, "a.html", Encoding.GetEncoding(950).GetBytes(body));

        Assert.Equal("驗收標準與付款條件，請於三十日內完成匯款。", Assert.Single(result.Sections).Text);
    }

    [Fact]
    public async Task Html5_meta_charset_with_gb2312_is_honoured()
    {
        const string body = "<!doctype html><meta charset=\"gb2312\"><p>简体中文内容</p>";

        var result = await ParseAsync(_parser, "a.html", Encoding.GetEncoding("gb2312").GetBytes(body));

        Assert.Equal("简体中文内容", Assert.Single(result.Sections).Text);
    }

    [Fact]
    public async Task Without_meta_the_text_decoder_is_used()
    {
        const string body = "<p>驗收標準與付款條件，請於三十日內完成匯款。</p>";

        var utf8Bom = await ParseAsync(_parser, "a.html", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(body)]);
        var big5 = await ParseAsync(_parser, "a.html", Encoding.GetEncoding(950).GetBytes(body));

        Assert.Equal("驗收標準與付款條件，請於三十日內完成匯款。", Assert.Single(utf8Bom.Sections).Text);
        Assert.Equal("驗收標準與付款條件，請於三十日內完成匯款。", Assert.Single(big5.Sections).Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n ")]
    [InlineData("<html><head><script>x()</script></head><body>  <p> </p> </body></html>")]
    public async Task Empty_or_textless_html_returns_empty(string html)
    {
        Assert.Same(ParsedDocument.Empty, await ParseAsync(_parser, "a.html", html));
    }

    [Fact]
    public async Task EmbeddedPath_is_copied_to_prose_and_table_sections()
    {
        string[] path = ["外層.docx", "內嵌.html"];

        var result = await ParseAsync(_parser, "內嵌.html", "<h1>標題</h1><p>內容</p><table><tr><td>格</td></tr></table>", embeddedPath: path);

        Assert.Equal(2, result.Sections.Count);
        Assert.All(result.Sections, s => Assert.Equal(path, s.Location.EmbeddedPath));
    }

    [Fact]
    public async Task Deeply_nested_markup_does_not_overflow_the_stack()
    {
        var html = string.Concat(Enumerable.Repeat("<div>", 5000)) + "深處的內容" + string.Concat(Enumerable.Repeat("</div>", 5000));

        var result = await ParseAsync(_parser, "a.html", html);

        Assert.Contains("深處的內容", AllText(result));
    }
}
