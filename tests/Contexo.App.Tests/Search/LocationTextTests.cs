using Contexo.App.Search;
using Contexo.Core.Abstractions;

namespace Contexo.App.Tests.Search;

public sealed class LocationTextTests
{
    [Fact]
    public void Spreadsheet_shows_sheet_and_range()
    {
        var location = new SourceLocation { Sheet = "報價明細", CellRange = "A1:F24" };

        Assert.Equal("工作表「報價明細」 · A1:F24", LocationText.Format(location));
    }

    [Fact]
    public void Spreadsheet_without_range_shows_only_the_sheet()
    {
        Assert.Equal("工作表「Sheet1」", LocationText.Format(new SourceLocation { Sheet = "Sheet1" }));
    }

    [Fact]
    public void Slide_shows_number_and_title()
    {
        var location = new SourceLocation { Slide = 7, Title = "廠商比價結果" };

        Assert.Equal("第 7 張投影片「廠商比價結果」", LocationText.Format(location));
    }

    [Fact]
    public void Slide_without_title_shows_only_the_number()
    {
        Assert.Equal("第 3 張投影片", LocationText.Format(new SourceLocation { Slide = 3 }));
    }

    [Fact]
    public void Pdf_shows_the_page()
    {
        Assert.Equal("第 12 頁", LocationText.Format(new SourceLocation { Page = 12 }));
    }

    [Fact]
    public void Document_shows_the_heading_path()
    {
        var location = new SourceLocation { HeadingPath = ["採購規範", "第 3 章 驗收", "3.2 驗收標準"] };

        Assert.Equal("採購規範 › 第 3 章 驗收 › 3.2 驗收標準", LocationText.Format(location));
    }

    [Fact]
    public void Embedded_file_gets_a_prefix()
    {
        var location = new SourceLocation { EmbeddedPath = ["內嵌.xlsx"], Sheet = "明細", CellRange = "A1:B2" };

        Assert.Equal("內嵌：內嵌.xlsx › 工作表「明細」 · A1:B2", LocationText.Format(location));
    }

    [Fact]
    public void Nested_embedded_files_are_chained()
    {
        var location = new SourceLocation { EmbeddedPath = ["a.docx", "b.pdf"], Page = 2 };

        Assert.Equal("內嵌：a.docx › b.pdf › 第 2 頁", LocationText.Format(location));
    }

    [Fact]
    public void Embedded_file_without_a_position_shows_just_the_prefix()
    {
        Assert.Equal("內嵌：x.xlsx", LocationText.Format(new SourceLocation { EmbeddedPath = ["x.xlsx"] }));
    }

    [Fact]
    public void Table_caption_is_shown_when_nothing_else_names_it()
    {
        Assert.Equal("「年度預算」", LocationText.Format(new SourceLocation { Title = "年度預算" }));
    }

    [Fact]
    public void Empty_location_gives_an_empty_string()
    {
        Assert.Equal(string.Empty, LocationText.Format(SourceLocation.None));
        Assert.Equal(string.Empty, LocationText.Format(null));
    }
}
