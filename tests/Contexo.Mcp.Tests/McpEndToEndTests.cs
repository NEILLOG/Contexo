using System.Text.Json;
using Contexo.Core.Abstractions;
using Contexo.Mcp.Tests.Support;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;

namespace Contexo.Mcp.Tests;

/// <summary>The built Contexo.Mcp started as a real child process and driven by the official SDK client over stdio.</summary>
public sealed class McpEndToEndTests : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync();
        _client = await McpServerProcess.ConnectAsync(_database);
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        _database.Dispose();
    }

    private Task<ModelContextProtocol.Protocol.CallToolResult> CallAsync(string tool, Dictionary<string, object?> arguments) =>
        McpServerProcess.CallAsync(_client, tool, arguments);

    private async Task<string> TableIdAsync(string query = "訂單清單")
    {
        var text = McpServerProcess.TextOf(await CallAsync("search", new() { ["query"] = query }));
        var line = text.Split('\n').First(l => l.StartsWith("table_id: ", StringComparison.Ordinal));
        return line["table_id: ".Length..].Split('（')[0].Trim();
    }

    [Fact]
    public async Task Initialize_reports_the_server_name_and_lists_the_three_tools_with_snake_case_parameters()
    {
        Assert.Equal("contexo", _client.ServerInfo.Name);
        Assert.False(string.IsNullOrWhiteSpace(_client.ServerInfo.Version));

        var tools = (await _client.ListToolsAsync()).ToDictionary(t => t.Name);

        Assert.Equal(["describe_table", "query_table", "search"], tools.Keys.Order());
        Assert.Equal(["query", "top_k"], Properties(tools["search"]));
        Assert.Equal(["table_id"], Properties(tools["describe_table"]));
        Assert.Equal(["max_rows", "sql", "table_id"], Properties(tools["query_table"]));
        Assert.Equal(["query"], Required(tools["search"]));
        Assert.Equal(["sql", "table_id"], Required(tools["query_table"]));
        Assert.All(tools.Values, tool => Assert.False(string.IsNullOrWhiteSpace(tool.Description)));
        Assert.Contains("language", tools["search"].Description);
        Assert.Contains("table_id", tools["search"].Description);
        Assert.Contains("SELECT", tools["query_table"].Description);
        Assert.Contains("double quotes", tools["query_table"].Description);
    }

    [Fact]
    public async Task Search_returns_text_with_file_location_and_path()
    {
        var result = await CallAsync("search", new() { ["query"] = TestDatabase.SlideQuery, ["top_k"] = 3 });

        Assert.NotEqual(true, result.IsError);
        var text = McpServerProcess.TextOf(result);
        Assert.Contains("[1] 簡報.pptx — 第 3 張投影片「營收摘要」", text);
        Assert.Contains("路徑：" + Path.Combine(_database.DocsDirectory, "簡報.pptx"), text);
    }

    [Fact]
    public async Task Search_then_describe_then_query_a_large_table()
    {
        var tableId = await TableIdAsync();

        var describe = await CallAsync("describe_table", new() { ["table_id"] = tableId });
        var query = await CallAsync("query_table", new()
        {
            ["table_id"] = tableId,
            ["sql"] = "SELECT \"客戶\", COUNT(*) AS 筆數 FROM t GROUP BY \"客戶\" ORDER BY \"客戶\"",
            ["max_rows"] = 10,
        });

        Assert.NotEqual(true, describe.IsError);
        Assert.Contains("資料列數：600", McpServerProcess.TextOf(describe));
        Assert.NotEqual(true, query.IsError);
        var text = McpServerProcess.TextOf(query);
        Assert.Contains("| 客戶 | 筆數 |", text);
        Assert.Contains("| 甲 | 200 |", text);
    }

    [Fact]
    public async Task Invalid_sql_is_an_error_result_and_the_server_keeps_working()
    {
        var tableId = await TableIdAsync();

        var bad = await CallAsync("query_table", new() { ["table_id"] = tableId, ["sql"] = "DROP TABLE t" });
        var typo = await CallAsync("query_table", new() { ["table_id"] = tableId, ["sql"] = "SELECT \"客護\" FROM t" });
        var good = await CallAsync("query_table", new() { ["table_id"] = tableId, ["sql"] = "SELECT COUNT(*) FROM t" });

        Assert.True(bad.IsError);
        Assert.False(string.IsNullOrWhiteSpace(McpServerProcess.TextOf(bad)));
        Assert.True(typo.IsError);
        Assert.Contains("\"客戶\"", McpServerProcess.TextOf(typo));
        Assert.NotEqual(true, good.IsError);
        Assert.Contains("| 600 |", McpServerProcess.TextOf(good));
    }

    [Fact]
    public async Task A_table_embedded_in_another_file_gets_a_clear_message_and_the_server_keeps_working()
    {
        var hit = McpServerProcess.TextOf(await CallAsync("search", new() { ["query"] = "內嵌訂單明細" }));
        Assert.DoesNotContain("table_id:", hit);

        // Ids are assigned in write order: the pptx table is the first one the store created.
        var store = _database.CreateStore();
        var search = new Contexo.Core.Search.HybridSearchService(
            store,
            new Contexo.Core.Embedding.OnnxEmbeddingService(_database.ModelsDirectory, Microsoft.Extensions.Logging.Abstractions.NullLogger<Contexo.Core.Embedding.OnnxEmbeddingService>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Contexo.Core.Search.HybridSearchService>.Instance);
        var embeddedId = (await search.SearchAsync(new SearchRequest("內嵌訂單明細", 3), CancellationToken.None)).Hits.Single().TableId!;

        var describe = await CallAsync("describe_table", new() { ["table_id"] = embeddedId });
        var query = await CallAsync("query_table", new() { ["table_id"] = embeddedId, ["sql"] = "SELECT * FROM t" });
        var stillWorks = await CallAsync("search", new() { ["query"] = TestDatabase.QuoteQuery });

        foreach (var result in new[] { describe, query })
        {
            Assert.True(result.IsError);
            Assert.Contains("簡報.pptx", McpServerProcess.TextOf(result));
            Assert.Contains("無法用 describe_table / query_table 查詢", McpServerProcess.TextOf(result));
        }

        Assert.NotEqual(true, stillWorks.IsError);
    }

    [Fact]
    public async Task Out_of_range_arguments_are_clamped_not_rejected()
    {
        var tableId = await TableIdAsync();

        var search = await CallAsync("search", new() { ["query"] = "冗長說明", ["top_k"] = 9999 });
        var query = await CallAsync("query_table", new() { ["table_id"] = tableId, ["sql"] = "SELECT \"金額\" FROM t", ["max_rows"] = 99999 });

        Assert.NotEqual(true, search.IsError);
        Assert.NotEqual(true, query.IsError);
        Assert.Contains("查詢結果共 500 列", McpServerProcess.TextOf(query));
    }

    [Fact]
    public async Task Activity_records_the_client_connection_each_tool_call_and_errors_but_never_the_query()
    {
        const string secretQuery = "祕密查詢字串";
        const string secretSql = "SELECT 祕密欄位 FROM t";
        var tableId = await TableIdAsync();
        await CallAsync("search", new() { ["query"] = secretQuery });
        await CallAsync("describe_table", new() { ["table_id"] = tableId });
        await CallAsync("query_table", new() { ["table_id"] = tableId, ["sql"] = secretSql });
        await _client.DisposeAsync();

        var store = _database.CreateStore();
        var summary = Assert.Single(await store.GetMcpActivitySummariesAsync(CancellationToken.None));
        Assert.Equal(McpServerProcess.ClientName, summary.ClientName);
        Assert.Equal(McpServerProcess.ClientVersion, summary.ClientVersion);
        Assert.NotNull(summary.LastConnectedAt);
        Assert.NotNull(summary.LastToolCallAt);
        Assert.NotNull(summary.LastErrorAt);

        var rows = _database.ReadActivityRows();
        Assert.Single(rows, r => r.Kind == (int)McpEventKind.Connected);
        Assert.Equal(["describe_table", "query_table", "search", "search"], rows.Where(r => r.Kind == (int)McpEventKind.ToolCall).Select(r => r.Tool).Order());
        Assert.Single(rows, r => r.Kind == (int)McpEventKind.Error && r.Tool == "query_table");
        Assert.All(rows, row =>
        {
            Assert.Equal(McpServerProcess.ClientName, row.Client);
            Assert.DoesNotContain("祕密", row.Detail ?? "");
            Assert.DoesNotContain("SELECT", row.Detail ?? "");
        });
        Assert.DoesNotContain(secretQuery, string.Join('\n', rows.Select(r => r.Detail)));
    }

    [Fact]
    public async Task Connected_is_recorded_after_the_handshake_even_before_any_tool_call()
    {
        await _client.ListToolsAsync();

        // The notification is handled in the background, so give it a moment while the client is still connected.
        var rows = _database.ReadActivityRows();
        for (var attempt = 0; attempt < 50 && rows.Count == 0; attempt++)
        {
            await Task.Delay(100);
            rows = _database.ReadActivityRows();
        }

        await _client.DisposeAsync();
        var connected = Assert.Single(rows);
        Assert.Equal((int)McpEventKind.Connected, connected.Kind);
        Assert.Equal(McpServerProcess.ClientName, connected.Client);
    }

    [Fact]
    public async Task Logs_do_not_contain_query_text_or_sql()
    {
        var tableId = await TableIdAsync();
        await CallAsync("search", new() { ["query"] = "祕密查詢字串" });
        await CallAsync("query_table", new() { ["table_id"] = tableId, ["sql"] = "SELECT 祕密欄位 FROM t" });
        await _client.DisposeAsync();

        var logs = string.Concat(Directory.GetFiles(_database.LogsDirectory, "mcp-*.log").Select(File.ReadAllText));
        Assert.Contains("Contexo.Mcp started", logs);
        Assert.DoesNotContain("祕密", logs);
        Assert.DoesNotContain(TestDatabase.QuoteQuery, logs);
    }

    private static string[] Properties(McpClientTool tool) =>
        [.. tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order()];

    private static string[] Required(McpClientTool tool) =>
        tool.JsonSchema.TryGetProperty("required", out var required) ? [.. required.EnumerateArray().Select(e => e.GetString()!).Order()] : [];
}

public sealed class McpEmptyDatabaseEndToEndTests
{
    private const string NoData = "Contexo 還沒有收錄任何資料。請先開啟 Contexo，加入要讓 AI 讀取的資料夾。";

    [Fact]
    public async Task An_empty_database_answers_every_tool_in_plain_language()
    {
        using var database = await TestDatabase.CreateAsync(withData: false);
        await using var client = await McpServerProcess.ConnectAsync(database);

        var search = await McpServerProcess.CallAsync(client, "search", new() { ["query"] = "報價單" });
        var describe = await McpServerProcess.CallAsync(client, "describe_table", new() { ["table_id"] = "t1" });
        var query = await McpServerProcess.CallAsync(client, "query_table", new() { ["table_id"] = "t1", ["sql"] = "SELECT 1 FROM t" });

        Assert.Equal(NoData, McpServerProcess.TextOf(search));
        Assert.Equal(NoData, McpServerProcess.TextOf(describe));
        Assert.Equal(NoData, McpServerProcess.TextOf(query));
    }

    [Fact]
    public async Task A_database_path_that_does_not_exist_yet_is_created_empty_and_answers_the_same()
    {
        using var database = await TestDatabase.CreateAsync(withData: false);
        var missing = Path.Combine(database.Root, "not-there-yet", "new.db");
        Assert.False(File.Exists(missing));
        await using var client = await McpServerProcess.ConnectAsync(database, missing);

        var search = await McpServerProcess.CallAsync(client, "search", new() { ["query"] = "報價單" });

        Assert.Equal(NoData, McpServerProcess.TextOf(search));
        Assert.True(File.Exists(missing), "Only an empty database is created.");
        Assert.Equal(["new.db"], Directory.GetFiles(Path.GetDirectoryName(missing)!).Select(Path.GetFileName).Where(n => !n!.EndsWith("-wal") && !n.EndsWith("-shm")));
    }

    [Fact]
    public async Task A_database_that_cannot_be_opened_is_a_plain_error_and_the_server_stays_up()
    {
        using var database = await TestDatabase.CreateAsync(withData: false);
        // A folder where the database file should be: SQLite cannot open it.
        var blocked = Path.Combine(database.Root, "blocked.db");
        Directory.CreateDirectory(blocked);
        await using var client = await McpServerProcess.ConnectAsync(database, blocked);

        var first = await McpServerProcess.CallAsync(client, "search", new() { ["query"] = "報價單" });
        var second = await McpServerProcess.CallAsync(client, "describe_table", new() { ["table_id"] = "t1" });

        Assert.True(first.IsError);
        Assert.Contains("無法讀取 Contexo 的資料", McpServerProcess.TextOf(first));
        Assert.True(second.IsError);
    }
}
