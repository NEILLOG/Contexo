using Contexo.Core.Tables;

namespace Contexo.Core.Tests.Tables;

public sealed class TableValueParserTests
{
    [Theory]
    [InlineData("123", 123)]
    [InlineData("-123.5", -123.5)]
    [InlineData("1,234,567", 1234567)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("  25.6%  ", 25.6)]
    [InlineData("$1,200", 1200)]
    [InlineData("$ 1,200", 1200)]
    [InlineData("NT$ 3,400", 3400)]
    [InlineData("nt$3400", 3400)]
    [InlineData("￥500", 500)]
    [InlineData("¥500", 500)]
    [InlineData("-$5", -5)]
    [InlineData("$-5", -5)]
    [InlineData("(1,200)", -1200)]
    [InlineData("0", 0)]
    [InlineData("0.5", 0.5)]
    [InlineData("+7", 7)]
    public void Numbers_are_recognised(string text, double expected)
    {
        Assert.True(TableValueParser.TryParseNumber(text, out var value));
        Assert.Equal(expected, value, 9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12abc")]
    [InlineData("1,23")]
    [InlineData("1,2345")]
    [InlineData(",123")]
    [InlineData("1,234,")]
    [InlineData("1.2.3")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("%")]
    [InlineData("$")]
    [InlineData("1e5")]
    [InlineData("007")]
    [InlineData("0912345678")]
    [InlineData("2025/01/05")]
    [InlineData("1 2")]
    public void Non_numbers_are_rejected(string text)
    {
        Assert.False(TableValueParser.TryParseNumber(text, out _));
    }

    [Theory]
    [InlineData("2025/01/05", "2025-01-05 00:00:00")]
    [InlineData("2025/1/5", "2025-01-05 00:00:00")]
    [InlineData("2025-01-05", "2025-01-05 00:00:00")]
    [InlineData("2025-01-05 13:45", "2025-01-05 13:45:00")]
    [InlineData("2025/01/05 13:45:09", "2025-01-05 13:45:09")]
    [InlineData("2025-01-05T08:00:00", "2025-01-05 08:00:00")]
    [InlineData("  2025/12/31  ", "2025-12-31 00:00:00")]
    public void Dates_are_recognised_and_formatted_as_iso(string text, string expected)
    {
        Assert.True(TableValueParser.TryParseDate(text, out var value));
        Assert.Equal(expected, TableValueParser.FormatDate(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2025/13/01")]
    [InlineData("2025/02/30")]
    [InlineData("01/05/2025")]
    [InlineData("2025年1月5日")]
    [InlineData("12:30")]
    [InlineData("20250105")]
    [InlineData("甲公司")]
    public void Non_dates_are_rejected(string text)
    {
        Assert.False(TableValueParser.TryParseDate(text, out _));
    }

    [Fact]
    public void Sql_names_keep_chinese_and_alphanumerics_and_fix_the_rest()
    {
        var names = TableValueParser.BuildSqlNames(["金額(元)", "1月", "a b", "A_B", " ", "", "金額(元)", "Q1-2025", "日期"]);

        Assert.Equal(["金額_元_", "c_1月", "a_b", "A_B_2", "column_5", "column_6", "金額_元__2", "Q1_2025", "日期"], names);
    }

    [Theory]
    [InlineData(1280000.0, "1280000")]
    [InlineData(1.5, "1.5")]
    [InlineData(-0.25, "-0.25")]
    [InlineData(0.30000000000000004, "0.3")]
    [InlineData(0.0, "0")]
    [InlineData(1e20, "1E+20")]
    public void Doubles_lose_extra_decimals(double value, string expected)
    {
        Assert.Equal(expected, TableQueryService.FormatDouble(value));
    }
}
