using Contexo.App.Folders;

namespace Contexo.App.Tests.Folders;

public sealed class PathRelationsTests
{
    [Theory]
    [InlineData(@"C:\A", @"C:\A\報價", true)]
    [InlineData(@"C:\A\報價", @"C:\A\報價單", false)]
    [InlineData(@"C:\A\報價\", @"C:\a\報價\2025", true)]
    [InlineData(@"C:\A", @"C:\A", false)]
    [InlineData(@"C:\A\報價", @"C:\A", false)]
    [InlineData("/Users/a", "/Users/a/Documents", true)]
    [InlineData("/Users/a", "/Users/ab", false)]
    [InlineData(@"C:\", @"C:\Users", true)]
    [InlineData("", @"C:\Users", false)]
    public void Inside_respects_directory_boundaries_and_ignores_case(string parent, string child, bool expected)
    {
        Assert.Equal(expected, PathRelations.IsInside(parent, child));
    }

    [Theory]
    [InlineData(@"C:\Users\chang\", "C:/Users/chang")]
    [InlineData(@"C:\", "C:/")]
    [InlineData("/", "/")]
    [InlineData("  D:\\x\\y\\\\ ", "D:/x/y")]
    public void Normalize_unifies_separators_and_trims_the_end(string path, string expected)
    {
        Assert.Equal(expected, PathRelations.Normalize(path));
    }

    [Fact]
    public void Names_and_sub_paths_are_taken_from_either_separator()
    {
        Assert.Equal("報價", PathRelations.GetName(@"C:\A\報價\"));
        Assert.Equal("a.docx", PathRelations.GetName("/x/y/a.docx"));
        Assert.Equal(["專案資料", "2025 台中案"], PathRelations.GetDirectoriesBelow(@"C:\Docs", @"C:\Docs\專案資料\2025 台中案\a.docx"));
        Assert.Empty(PathRelations.GetDirectoriesBelow(@"C:\Docs", @"C:\Docs\a.docx"));
    }

    [Fact]
    public void Long_paths_are_shortened_in_the_middle()
    {
        var path = @"C:\Users\chang\OneDrive - 公司共用\專案資料\2025 台中案\驗收報告\最後版本";

        var text = PathRelations.ShortenMiddle(path, 40);

        Assert.Equal(40, text.Length);
        Assert.Contains("…", text, StringComparison.Ordinal);
        Assert.StartsWith(@"C:\Users", text, StringComparison.Ordinal);
        Assert.EndsWith("最後版本", text, StringComparison.Ordinal);
        Assert.Equal(@"C:\short", PathRelations.ShortenMiddle(@"C:\short", 40));
    }
}
