using Contexo.App.About;

namespace Contexo.App.Tests.About;

public sealed class ChangelogParserTests
{
    private const string Sample = """
        # 版本紀錄

        ## 1.4.2 - 2026-10-06

        - AI 軟體連線狀態改為三種
        - 修正大型 Excel 讀取逾時

        ## 1.4.0 - 2026-09-22
        - 新增匯出問題回報
        * 新增文字大小設定

        ## 1.3.1 - 2026-09-05
        - 修正 Big5 文字檔亂碼
        """;

    [Fact]
    public void Versions_dates_and_items_are_read()
    {
        var entries = ChangelogParser.Parse(Sample);

        Assert.Equal(3, entries.Count);
        Assert.Equal("1.4.2", entries[0].Version);
        Assert.Equal("2026-10-06", entries[0].Date);
        Assert.Equal(["AI 軟體連線狀態改為三種", "修正大型 Excel 讀取逾時"], entries[0].Items);
        Assert.Equal(["新增匯出問題回報", "新增文字大小設定"], entries[1].Items);
        Assert.Equal(["修正 Big5 文字檔亂碼"], entries[2].Items);
    }

    [Fact]
    public void Only_the_five_most_recent_versions_are_kept()
    {
        var text = string.Join("\n", Enumerable.Range(1, 8).Select(i => $"## 1.{9 - i}.0 - 2026-01-0{i}\n- item {i}"));

        var entries = ChangelogParser.Parse(text);

        Assert.Equal(5, entries.Count);
        Assert.Equal(["1.8.0", "1.7.0", "1.6.0", "1.5.0", "1.4.0"], entries.Select(e => e.Version));
        Assert.Equal(["item 5"], entries[^1].Items);
    }

    [Fact]
    public void The_initial_changelog_shows_the_unreleased_section_without_a_date()
    {
        var entries = ChangelogParser.Parse("# 版本紀錄\r\n\r\n## 未發布\r\n\r\n- 第一版開發中\r\n");

        var entry = Assert.Single(entries);
        Assert.Equal("未發布", entry.Version);
        Assert.Null(entry.Date);
        Assert.False(entry.HasDate);
        Assert.Equal(["第一版開發中"], entry.Items);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("# 只有標題\n\n沒有版本")]
    public void Text_without_any_release_gives_an_empty_list(string? text)
    {
        Assert.Empty(ChangelogParser.Parse(text));
    }

    [Fact]
    public void The_embedded_changelog_of_the_repository_can_be_read()
    {
        var text = ChangelogParser.ReadEmbedded();

        Assert.NotNull(text);
        Assert.NotEmpty(ChangelogParser.Parse(text));
    }
}
