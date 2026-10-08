using Contexo.Core.Parsing.Spreadsheet;

namespace Contexo.Core.Tests.Parsing.SpreadsheetTests;

public sealed class NumberFormatClassifierTests
{
    [Theory]
    [InlineData(14, true, false)]
    [InlineData(15, true, false)]
    [InlineData(22, true, true)]
    [InlineData(18, false, true)]
    [InlineData(21, false, true)]
    [InlineData(45, false, true)]
    [InlineData(47, false, true)]
    [InlineData(31, true, false)]
    public void Built_in_date_and_time_ids(int id, bool hasDate, bool hasTime)
    {
        var style = NumberFormatClassifier.Classify(id, null);

        Assert.Equal(NumberFormatKind.Date, style.Format);
        Assert.Equal(hasDate, style.HasDate);
        Assert.Equal(hasTime, style.HasTime);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void Built_in_percent_ids(int id) =>
        Assert.Equal(NumberFormatKind.Percent, NumberFormatClassifier.Classify(id, null).Format);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(49)]
    public void Other_built_in_ids_are_general(int id) =>
        Assert.Equal(NumberFormatKind.General, NumberFormatClassifier.Classify(id, null).Format);

    [Theory]
    [InlineData("yyyy/mm/dd", true, false)]
    [InlineData("yyyy-mm-dd hh:mm", true, true)]
    [InlineData("yyyy\"年\"m\"月\"d\"日\"", true, false)]
    [InlineData("[$-404]yyyy/m/d", true, false)]
    [InlineData("m/d/yy h:mm AM/PM", true, true)]
    [InlineData("h:mm:ss", false, true)]
    [InlineData("mmm-yy", true, false)]
    [InlineData("[h]:mm", false, true)]
    [InlineData("yyyy/mm/dd;@", true, false)]
    public void Custom_date_formats(string code, bool hasDate, bool hasTime)
    {
        var style = NumberFormatClassifier.Classify(170, code);

        Assert.Equal(NumberFormatKind.Date, style.Format);
        Assert.Equal(hasDate, style.HasDate);
        Assert.Equal(hasTime, style.HasTime);
    }

    [Theory]
    [InlineData("General")]
    [InlineData("0.00")]
    [InlineData("#,##0")]
    [InlineData("0.0\" m\"")]
    [InlineData("\"day\" 0")]
    [InlineData("#,##0 \"hours\";-#,##0")]
    [InlineData("0.00E+00")]
    [InlineData("[Red]0.00")]
    [InlineData("@")]
    [InlineData("0\\d")]
    public void Custom_number_formats_are_not_dates(string code) =>
        Assert.Equal(NumberFormatKind.General, NumberFormatClassifier.Classify(170, code).Format);

    [Theory]
    [InlineData("0.0%")]
    [InlineData("0.00%;-0.00%")]
    public void Custom_percent_formats(string code) =>
        Assert.Equal(NumberFormatKind.Percent, NumberFormatClassifier.Classify(170, code).Format);

    [Theory]
    [InlineData("45731", 14, false, "2025/03/15")]
    [InlineData("45731.5", 22, false, "2025/03/15 12:00")]
    [InlineData("0.75", 21, false, "18:00")]
    [InlineData("44269", 14, true, "2025/03/15")]
    [InlineData("0.256", 9, false, "25.6%")]
    [InlineData("1", 9, false, "100%")]
    [InlineData("1234.5", 0, false, "1234.5")]
    [InlineData("0.30000000000000004", 0, false, "0.3")]
    [InlineData("1E-05", 0, false, "1E-05")]
    [InlineData("-0", 0, false, "0")]
    [InlineData("not a number", 0, false, "not a number")]
    [InlineData("99999999", 14, false, "99999999")]
    public void Numbers_are_shown_by_their_format(string raw, int numFmtId, bool date1904, string expected)
    {
        var style = NumberFormatClassifier.Classify(numFmtId, null);

        Assert.Equal(expected, NumberFormatClassifier.Format(raw, style, date1904));
    }

    [Fact]
    public void Elapsed_time_can_pass_24_hours()
    {
        var style = NumberFormatClassifier.Classify(170, "[h]:mm");

        Assert.Equal("30:30", NumberFormatClassifier.Format((1 + (6.5 / 24)).ToString(System.Globalization.CultureInfo.InvariantCulture), style, false));
    }

    [Theory]
    [InlineData("A", 0)]
    [InlineData("Z", 25)]
    [InlineData("AA", 26)]
    [InlineData("XFD", 16383)]
    public void Column_letters_round_trip(string letters, int index)
    {
        Assert.Equal(index, CellAddress.ParseColumn(letters));
        Assert.Equal(letters, CellAddress.ColumnName(index));
    }

    [Theory]
    [InlineData("A1:F24", 0, 0, 23, 5)]
    [InlineData("C3", 2, 2, 2, 2)]
    [InlineData("F24:A1", 0, 0, 23, 5)]
    [InlineData("$B$2:$C$3", 1, 1, 2, 2)]
    public void Ranges_parse_and_format(string text, int top, int left, int bottom, int right)
    {
        Assert.True(CellRect.TryParse(text, out var rect));
        Assert.Equal(new CellRect(top, left, bottom, right), rect);
        Assert.True(CellRect.TryParse(rect.ToA1(), out var again));
        Assert.Equal(rect, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("1")]
    [InlineData("A0")]
    [InlineData("A1:")]
    [InlineData("XFE1")]
    public void Bad_ranges_are_rejected(string text) => Assert.False(CellRect.TryParse(text, out _));
}
