using Contexo.Core.Parsing.Pdf;

namespace Contexo.Core.Tests.Parsing.Pdf;

public sealed class PdfTextLayoutTests
{
    [Fact]
    public void JoinLines_CjkOnBothSides_JoinsWithoutSpaceOrNewline()
    {
        Assert.Equal("本公司於民國一百一十三年度之營業收入較去年成長", PdfTextLayout.JoinLines(["本公司於民國一百一十三年度之", "營業收入較去年成長"]));
    }

    [Fact]
    public void JoinLines_CjkThenLatin_KeepsNewline()
    {
        Assert.Equal("採用 ISO\n27001 標準", PdfTextLayout.JoinLines(["採用 ISO", "27001 標準"]));
        Assert.Equal("版本\nv2", PdfTextLayout.JoinLines(["版本", "v2"]));
        Assert.Equal("v2\n版本", PdfTextLayout.JoinLines(["v2", "版本"]));
    }

    [Fact]
    public void JoinLines_Latin_KeepsNewline()
    {
        Assert.Equal("first line\nsecond line", PdfTextLayout.JoinLines(["first line", "second line"]));
    }

    [Fact]
    public void JoinLines_Punctuation_IsNotTreatedAsCjkLetter()
    {
        // A sentence end followed by a new line stays a line break.
        Assert.Equal("第一句。\n第二句", PdfTextLayout.JoinLines(["第一句。", "第二句"]));
    }

    [Fact]
    public void JoinLines_TrimsAndSkipsBlankLines()
    {
        Assert.Equal("甲乙丙丁\nabc", PdfTextLayout.JoinLines(["  甲乙  ", "", "   ", "丙丁", "abc  "]));
    }

    [Fact]
    public void JoinLines_SurrogatePairCjk_IsRecognised()
    {
        // U+20000 (extension B ideograph) written as a surrogate pair.
        var rare = char.ConvertFromUtf32(0x20000);
        Assert.Equal(rare + "字", PdfTextLayout.JoinLines([rare, "字"]));
    }

    [Theory]
    [InlineData(0x4E2D, true)]
    [InlineData(0x3042, true)]
    [InlineData(0xAC00, true)]
    [InlineData(0x3105, true)]
    [InlineData(0x3002, false)] // 。
    [InlineData(0xFF0C, false)] // ，
    [InlineData(0x0041, false)]
    [InlineData(0x0031, false)]
    public void IsCjk_ClassifiesCodePoints(int codePoint, bool expected) => Assert.Equal(expected, PdfTextLayout.IsCjk(codePoint));

    [Theory]
    [InlineData("第 12 頁", "第頁")]
    [InlineData("Page 3 of 10", "pageof")]
    [InlineData("- 7 -", "--")]
    [InlineData("42", "")]
    public void NormalizeForComparison_StripsDigitsAndWhitespace(string input, string expected) =>
        Assert.Equal(expected, PdfTextLayout.NormalizeForComparison(input));

    [Fact]
    public void RemoveHeadersAndFooters_RemovesRepeatedRowsOnlyWhenMoreThanHalfOfPages()
    {
        var pages = new[]
        {
            Page(1, Line("公司內部文件", 800), Line("內文一", 500), Line("第 1 頁", 30)),
            Page(2, Line("公司內部文件", 800), Line("內文二", 500), Line("第 2 頁", 30)),
            Page(3, Line("公司內部文件", 800), Line("內文三", 500), Line("附註只在這頁", 30)),
            Page(4, Line("第四頁標題", 800), Line("內文四", 500), Line("第 4 頁", 30)),
        };

        var result = PdfTextLayout.RemoveHeadersAndFooters(pages);

        var texts = result.Select(PdfTextLayout.BuildPageText).ToList();
        Assert.Equal("內文一", texts[0]);
        Assert.Equal("內文二", texts[1]);
        // Header repeats on 3 of 4 pages (removed); footer "附註" appears once only (kept); "第 N 頁" on 3 of 4 is removed elsewhere.
        Assert.Equal("內文三" + PdfTextLayout.ParagraphSeparator + "附註只在這頁", texts[2]);
        // The one-off header on page 4 stays, its footer is the repeated page number and goes.
        Assert.Equal("第四頁標題" + PdfTextLayout.ParagraphSeparator + "內文四", texts[3]);
    }

    [Fact]
    public void RemoveHeadersAndFooters_FewerThanThreePages_ChangesNothing()
    {
        var pages = new[]
        {
            Page(1, Line("Header", 800), Line("Body one", 500)),
            Page(2, Line("Header", 800), Line("Body two", 500)),
        };

        var result = PdfTextLayout.RemoveHeadersAndFooters(pages);

        Assert.Equal(pages, result);
    }

    [Fact]
    public void RemoveHeadersAndFooters_ExactlyHalf_IsNotRemoved()
    {
        var pages = new[]
        {
            Page(1, Line("Header", 800), Line("one", 500)),
            Page(2, Line("Header", 800), Line("two", 500)),
            Page(3, Line("Other", 800), Line("three", 500)),
            Page(4, Line("Another", 800), Line("four", 500)),
        };

        var texts = PdfTextLayout.RemoveHeadersAndFooters(pages).Select(PdfTextLayout.BuildPageText).ToList();

        Assert.Contains("Header", texts[0]);
        Assert.Contains("Header", texts[1]);
    }

    [Fact]
    public void RemoveHeadersAndFooters_HeaderMadeOfSeveralFragmentsOnTheSameRow_IsRemovedWhole()
    {
        var pages = new[]
        {
            Page(1, Line("Company", 800, left: 50), Line("2024-01-05", 800, left: 400), Line("body one", 500)),
            Page(2, Line("Company", 800, left: 50), Line("2024-01-06", 800, left: 400), Line("body two", 500)),
            Page(3, Line("Company", 800, left: 50), Line("2024-01-07", 800, left: 400), Line("body three", 500)),
        };

        var texts = PdfTextLayout.RemoveHeadersAndFooters(pages).Select(PdfTextLayout.BuildPageText).ToList();

        Assert.Equal(["body one", "body two", "body three"], texts);
    }

    [Fact]
    public void BuildPageText_SeparatesBlocksWithBlankLine()
    {
        var page = new PdfPageText(1,
        [
            new PdfBlock([Line("one", 700), Line("two", 685)]),
            new PdfBlock([Line("three", 600)]),
        ]);

        Assert.Equal("one\ntwo" + PdfTextLayout.ParagraphSeparator + "three", PdfTextLayout.BuildPageText(page));
    }

    private static PdfLine Line(string text, double y, double left = 72) => new(text, left, y - 2, y + 10);

    private static PdfPageText Page(int number, params PdfLine[] lines) =>
        new(number, lines.Select(l => new PdfBlock([l])).ToList());
}
