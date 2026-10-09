using Contexo.Core.Search;
using Microsoft.Data.Sqlite;

namespace Contexo.Core.Tests.Search;

public sealed class KeywordQueryBuilderTests
{
    [Fact]
    public void ChineseSentence_BecomesTrigramsAndLikeTerms()
    {
        var (fts, like) = KeywordQueryBuilder.Build("去年給客戶的報價單");

        Assert.Equal(
            "\"去年給\" OR \"年給客\" OR \"給客戶\" OR \"客戶的\" OR \"戶的報\" OR \"的報價\" OR \"報價單\"",
            fts);
        Assert.Empty(like);
        AssertValidFts(fts);
    }

    [Fact]
    public void ModelNumber_KeepsHyphenAndShortWordGoesToLike()
    {
        var (fts, like) = KeywordQueryBuilder.Build("ABC-123 規格");

        Assert.Equal("\"abc-123\"", fts);
        Assert.Equal(["規格"], like);
        AssertValidFts(fts);
    }

    [Fact]
    public void TwoCharacterQuery_OnlyUsesLike()
    {
        var (fts, like) = KeywordQueryBuilder.Build("報價");

        Assert.Null(fts);
        Assert.Equal(["報價"], like);
    }

    [Fact]
    public void FullWidthLettersAndDigits_AreNormalised()
    {
        var (fts, like) = KeywordQueryBuilder.Build("Ｑ３營收");

        Assert.Null(fts);
        Assert.Equal(["q3", "營收"], like);
    }

    [Fact]
    public void FullWidthLongWord_BecomesHalfWidthLowerCase()
    {
        var (fts, _) = KeywordQueryBuilder.Build("ＡＢＣ－１２３");

        Assert.Equal("\"abc-123\"", fts);
    }

    [Fact]
    public void Version_KeepsDotBetweenDigits()
    {
        var (fts, _) = KeywordQueryBuilder.Build("版本 v1.2.3");

        Assert.Equal("\"v1.2.3\"", fts);
    }

    [Fact]
    public void ConnectorsOutsideWords_AreDropped()
    {
        var (fts, like) = KeywordQueryBuilder.Build("-- foo- _bar ... 12");

        Assert.Equal("\"foo\" OR \"bar\"", fts);
        Assert.Equal(["12"], like);
    }

    [Theory]
    [InlineData("\"quoted\" text")]
    [InlineData("報價*")]
    [InlineData("foo AND bar OR baz NOT qux")]
    [InlineData("NEAR(a b) col:val ^start (x)")]
    [InlineData("a\"b\"\"c 報價\"單\"")]
    [InlineData("--+*:^()\"\"")]
    public void HostileInput_ProducesValidFtsExpression(string input)
    {
        var (fts, like) = KeywordQueryBuilder.Build(input);

        AssertValidFts(fts);
        Assert.All(like, term => Assert.Equal(2, term.Length));
    }

    [Fact]
    public void QuotesAndOperators_AreNeverEmittedAsSyntax()
    {
        var (fts, _) = KeywordQueryBuilder.Build("foo AND bar NOT baz*");

        Assert.Equal("\"foo\" OR \"and\" OR \"bar\" OR \"not\" OR \"baz\"", fts);
    }

    [Fact]
    public void LimitsNumberOfTerms()
    {
        var longChinese = new string(Enumerable.Range(0, 100).Select(i => (char)(0x4E00 + i)).ToArray());
        var (fts, _) = KeywordQueryBuilder.Build(longChinese);
        Assert.Equal(KeywordQueryBuilder.MaxFtsTerms, fts!.Split(" OR ").Length);
        Assert.StartsWith("\"" + longChinese[..3] + "\"", fts);

        var shortWords = string.Join(' ', Enumerable.Range(10, 20).Select(i => "x" + (char)('a' + i % 26)));
        var (_, like) = KeywordQueryBuilder.Build(shortWords);
        Assert.Equal(KeywordQueryBuilder.MaxLikeTerms, like.Count);

        var longWords = string.Join(' ', Enumerable.Range(0, 50).Select(i => "word" + i));
        var (ftsWords, _) = KeywordQueryBuilder.Build(longWords);
        Assert.Equal(KeywordQueryBuilder.MaxFtsTerms, ftsWords!.Split(" OR ").Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("，。！？")]
    [InlineData("a 1")]
    public void NothingSearchable_GivesNoQuery(string input)
    {
        var (fts, like) = KeywordQueryBuilder.Build(input);

        Assert.Null(fts);
        Assert.Empty(like);
    }

    [Fact]
    public void DuplicateTerms_AreCollapsed()
    {
        var (fts, like) = KeywordQueryBuilder.Build("報價單 報價單 abc ABC 報價 報價");

        Assert.Equal("\"報價單\" OR \"abc\"", fts);
        Assert.Equal(["報價"], like);
    }

    /// <summary>Actually runs the expression against an FTS5 trigram table: a syntax error would throw.</summary>
    private static void AssertValidFts(string? fts)
    {
        if (fts is null)
        {
            return;
        }

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE VIRTUAL TABLE t USING fts5(x, tokenize='trigram'); INSERT INTO t(x) VALUES ('去年給客戶的報價單 abc-123');";
            create.ExecuteNonQuery();
        }

        using var query = connection.CreateCommand();
        query.CommandText = "SELECT count(*) FROM t WHERE t MATCH $q";
        query.Parameters.AddWithValue("$q", fts);
        _ = query.ExecuteScalar();
    }
}
