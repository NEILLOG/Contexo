using System.Text;
using Contexo.Core.Abstractions;
using Contexo.Core.Parsing.Text;
using static Contexo.Core.Tests.Parsing.TextParsers.TextParserTestHelper;

namespace Contexo.Core.Tests.Parsing.TextParsers;

public sealed class RtfParserTests
{
    private readonly RtfParser _parser = new();

    /// <summary>RTF escape for non-ASCII text: <c>\uN?</c> per UTF-16 code unit.</summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (ch < 128)
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append("\\u").Append((short)ch).Append('?');
            }
        }

        return sb.ToString();
    }

    private static string BuildRtf(string paragraph, string[][] table)
    {
        var sb = new StringBuilder();
        sb.Append("{\\rtf1\\ansi\\ansicpg950\\deff0{\\fonttbl{\\f0\\fnil\\fcharset136 PMingLiU;}}\\viewkind4\\uc1\\pard\\f0\\fs24 ");
        sb.Append(Escape(paragraph)).Append("\\par\n");
        foreach (var row in table)
        {
            sb.Append("\\trowd\\trgaph108");
            for (var i = 1; i <= row.Length; i++)
            {
                sb.Append("\\cellx").Append(i * 2000);
            }

            sb.Append('\n');
            foreach (var cell in row)
            {
                sb.Append("\\pard\\intbl ").Append(Escape(cell)).Append("\\cell\n");
            }

            sb.Append("\\row\n");
        }

        sb.Append("\\pard after\\par\n}");
        return sb.ToString();
    }

    [Fact]
    public async Task Chinese_text_and_table_are_extracted()
    {
        var rtf = BuildRtf("採購規範：驗收標準", [["品名", "數量"], ["筆記本", "3"], ["原子筆", "10"]]);

        var result = await ParseAsync(_parser, "規範.rtf", Encoding.ASCII.GetBytes(rtf));

        var prose = result.Sections.First(s => s.Kind == SectionKind.Prose);
        Assert.Contains("採購規範：驗收標準", prose.Text);

        var table = Assert.Single(result.Sections, s => s.Kind == SectionKind.Table);
        Assert.True(table.KeepWhole);
        Assert.StartsWith("<table>", table.Text);
        foreach (var expected in new[] { "品名", "數量", "筆記本", "3", "原子筆", "10" })
        {
            Assert.Contains($">{expected}</t", table.Text);
        }

        Assert.Contains("after", AllText(result));
    }

    [Fact]
    public async Task Plain_paragraphs_without_table_work()
    {
        var rtf = "{\\rtf1\\ansi\\uc1 " + Escape("第一段文字") + "\\par " + Escape("第二段文字") + "\\par}";

        var result = await ParseAsync(_parser, "a.rtf", Encoding.ASCII.GetBytes(rtf));

        var text = Assert.Single(result.Sections).Text;
        Assert.Contains("第一段文字", text);
        Assert.Contains("第二段文字", text);
        Assert.DoesNotContain("rtf1", text);
    }

    [Fact]
    public async Task EmbeddedPath_is_copied()
    {
        string[] path = ["外層.docx", "內嵌.rtf"];
        var rtf = BuildRtf("內容", [["格"]]);

        var result = await ParseAsync(_parser, "內嵌.rtf", Encoding.ASCII.GetBytes(rtf), embeddedPath: path);

        Assert.True(result.Sections.Count >= 2);
        Assert.All(result.Sections, s => Assert.Equal(path, s.Location.EmbeddedPath));
    }

    [Fact]
    public async Task Empty_file_returns_empty()
    {
        Assert.Same(ParsedDocument.Empty, await ParseAsync(_parser, "a.rtf", Array.Empty<byte>()));
        Assert.Same(ParsedDocument.Empty, await ParseAsync(_parser, "a.rtf", "  \r\n"));
    }

    [Fact]
    public async Task Rtf_with_no_text_returns_empty()
    {
        var result = await ParseAsync(_parser, "a.rtf", "{\\rtf1\\ansi\\deff0 }");

        Assert.Empty(result.Sections);
    }

    [Fact]
    public async Task Non_rtf_content_is_reported_as_corrupted()
    {
        var ex = await Assert.ThrowsAsync<DocumentParseException>(() => ParseAsync(_parser, "a.rtf", "這不是 RTF 檔案"));

        Assert.Equal(DocumentErrorCode.Corrupted, ex.Code);
    }

    [Fact]
    public async Task Truncated_rtf_only_fails_with_a_corrupted_error()
    {
        try
        {
            await ParseAsync(_parser, "a.rtf", "{\\rtf1\\ansi {\\fonttbl{\\f0 Arial;");
        }
        catch (DocumentParseException ex)
        {
            Assert.Equal(DocumentErrorCode.Corrupted, ex.Code);
        }
    }
}
