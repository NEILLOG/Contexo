namespace Contexo.Mcp.Tests;

public sealed class McpArgumentsTests
{
    [Fact]
    public void No_arguments_means_defaults()
    {
        var parsed = McpArguments.Parse([]);

        Assert.Null(parsed.DatabasePath);
        Assert.Null(parsed.ModelsDirectory);
    }

    [Fact]
    public void Reads_db_and_models_with_a_space()
    {
        var parsed = McpArguments.Parse(["--db", "C:\\Data\\contexo.db", "--models", "D:\\models"]);

        Assert.Equal("C:\\Data\\contexo.db", parsed.DatabasePath);
        Assert.Equal("D:\\models", parsed.ModelsDirectory);
    }

    [Fact]
    public void Reads_values_with_an_equals_sign()
    {
        var parsed = McpArguments.Parse(["--db=/tmp/a b.db", "--models=/tmp/models"]);

        Assert.Equal("/tmp/a b.db", parsed.DatabasePath);
        Assert.Equal("/tmp/models", parsed.ModelsDirectory);
    }

    [Fact]
    public void Unknown_arguments_are_ignored_and_a_missing_value_is_null()
    {
        var parsed = McpArguments.Parse(["--verbose", "--db"]);

        Assert.Null(parsed.DatabasePath);
        Assert.Null(parsed.ModelsDirectory);
    }
}
