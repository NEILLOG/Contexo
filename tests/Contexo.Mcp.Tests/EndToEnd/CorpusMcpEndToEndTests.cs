using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Contexo.Core;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Contexo.Mcp.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Xunit.Abstractions;

namespace Contexo.Mcp.Tests.EndToEnd;

/// <summary>
/// The real chain on the generated 40-file corpus: the corpus is indexed in this process (real parsers, SQLite, embedding model when present),
/// then the built Contexo.Mcp is started as a child process and driven by the official SDK client.
/// Run with <c>dotnet test --filter Category=EndToEnd</c>.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class CorpusMcpEndToEndTests(ITestOutputHelper output) : IAsyncLifetime
{
    private string _root = "";
    private string _modelsDirectory = "";
    private string _databasePath = "";
    private bool _modelAvailable;
    private JsonElement _expected;
    private ServiceProvider _provider = null!;
    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("contexo-mcp-e2e-").FullName;
        var corpusRoot = Path.Combine(_root, "corpus-root");
        CorpusProcess.Generate(corpusRoot);
        var corpusDirectory = Path.Combine(corpusRoot, "corpus");
        _expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(corpusRoot, "expected.json"))).RootElement.Clone();

        var dataDirectory = Path.Combine(_root, "data");
        _databasePath = Path.Combine(dataDirectory, "contexo.db");
        _modelsDirectory = CorpusProcess.FindModelsDirectory() ?? Path.Combine(_root, "no-models");

        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(new AppPaths(new AppPathsOverrides { DataDirectory = dataDirectory, ModelsDirectory = _modelsDirectory }));
        services.AddContexoCore();
        _provider = services.BuildServiceProvider();

        var store = _provider.GetRequiredService<IKnowledgeStore>();
        await store.InitializeAsync(CancellationToken.None);
        var folder = await store.AddFolderAsync(corpusDirectory, CancellationToken.None);
        _modelAvailable = _provider.GetRequiredService<IEmbeddingService>().IsAvailable;

        var indexing = _provider.GetRequiredService<IIndexingService>();
        await indexing.StartAsync(CancellationToken.None);
        var clock = Stopwatch.StartNew();
        var stable = 0;
        while (stable < 3)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromMinutes(10), "indexing did not finish in 10 minutes");
            await Task.Delay(250);
            var documents = await store.GetDocumentsAsync(folder.Id, CancellationToken.None);
            var idle = indexing.Current.State == IndexingState.Idle && documents.Count == 40 && indexing.Current.Folders.All(f => f.PendingFiles == 0);
            stable = idle ? stable + 1 : 0;
        }

        await indexing.StopAsync(CancellationToken.None);
        output.WriteLine($"Indexed 40 files in {clock.Elapsed.TotalSeconds:0.0} s (model available: {_modelAvailable})");

        // Contexo.Mcp is a separate process that only reads the database the desktop program wrote.
        _client = await CorpusProcess.ConnectAsync(dataDirectory, _databasePath, _modelsDirectory);
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Search_then_describe_table_then_query_table_all_succeed_and_the_activity_log_shows_the_client()
    {
        // 1. search: a plain-text answer that names the file and, for a large table, hands out a table_id.
        var firstSearch = Stopwatch.StartNew();
        var found = await CallAsync("search", new() { ["query"] = "驗收標準", ["top_k"] = 5 });
        firstSearch.Stop();
        var foundText = McpServerProcess.TextOf(found);
        Assert.NotEqual(true, found.IsError);
        Assert.Contains("採購規範.docx", foundText);
        Assert.Contains("路徑：", foundText);

        var secondSearch = Stopwatch.StartNew();
        var tableSearch = await CallAsync("search", new() { ["query"] = "銷售明細表", ["top_k"] = 5 });
        secondSearch.Stop();
        output.WriteLine($"First search through MCP (loads the model and the vectors): {firstSearch.ElapsedMilliseconds} ms; second search: {secondSearch.ElapsedMilliseconds} ms");

        var tableText = McpServerProcess.TextOf(tableSearch);
        Assert.NotEqual(true, tableSearch.IsError);
        var tableLine = tableText.Split('\n').FirstOrDefault(l => l.StartsWith("table_id: ", StringComparison.Ordinal));
        Assert.True(tableLine is not null, "search for the sales detail returned no table_id:" + Environment.NewLine + tableText);
        var tableId = tableLine["table_id: ".Length..].Split('（')[0].Trim();

        // 2. describe_table
        var describe = await CallAsync("describe_table", new() { ["table_id"] = tableId });
        var describeText = McpServerProcess.TextOf(describe);
        Assert.NotEqual(true, describe.IsError);
        Assert.Contains("資料列數：" + _expected.GetProperty("SalesRowCount").GetInt32(), describeText);
        Assert.Contains("金額", describeText);

        // 3. query_table: the total and the best customer match the numbers the generator computed.
        var total = _expected.GetProperty("SalesTotalAmount").GetInt64();
        var sum = await CallAsync("query_table", new() { ["table_id"] = tableId, ["sql"] = "SELECT SUM(\"金額\") AS total FROM t" });
        Assert.NotEqual(true, sum.IsError);
        Assert.Contains("| " + total.ToString(CultureInfo.InvariantCulture) + " |", McpServerProcess.TextOf(sum));

        var top = await CallAsync("query_table", new()
        {
            ["table_id"] = tableId,
            ["sql"] = "SELECT \"客戶\", SUM(\"金額\") AS total FROM t GROUP BY \"客戶\" ORDER BY total DESC LIMIT 1",
            ["max_rows"] = 5,
        });
        Assert.NotEqual(true, top.IsError);
        var topText = McpServerProcess.TextOf(top);
        Assert.Contains("| " + _expected.GetProperty("SalesTopCustomer").GetString() + " |", topText);
        Assert.Contains(_expected.GetProperty("SalesTopCustomerAmount").GetInt64().ToString(CultureInfo.InvariantCulture), topText);

        // 4. The desktop program's "AI software" page reads these records: one Connected and the tool calls, under the client's own name.
        var store = _provider.GetRequiredService<IKnowledgeStore>();
        var summary = (await store.GetMcpActivitySummariesAsync(CancellationToken.None)).SingleOrDefault(s => s.ClientName == McpServerProcess.ClientName);
        Assert.NotNull(summary);
        Assert.NotNull(summary.LastConnectedAt);
        Assert.NotNull(summary.LastToolCallAt);
        Assert.Null(summary.LastErrorAt);
        Assert.Equal(McpServerProcess.ClientVersion, summary.ClientVersion);
    }

    [Fact]
    public async Task A_table_embedded_in_another_file_does_not_break_the_conversation()
    {
        // Known gap (task/README.md, decision A-1): observed, not decided. The only promise is a readable answer and a server that keeps working.
        var search = await CallAsync("search", new() { ["query"] = "PTZ-2000 球型攝影機的單價", ["top_k"] = 5 });
        Assert.NotEqual(true, search.IsError);
        var text = McpServerProcess.TextOf(search);
        output.WriteLine(text.Length > 1200 ? text[..1200] + "..." : text);
        Assert.Contains("智慧監控提案書.docx", text);

        var next = await CallAsync("search", new() { ["query"] = "停車證怎麼換", ["top_k"] = 3 });
        Assert.NotEqual(true, next.IsError);
        Assert.Contains("停車證", McpServerProcess.TextOf(next));
    }

    private Task<ModelContextProtocol.Protocol.CallToolResult> CallAsync(string tool, Dictionary<string, object?> arguments) =>
        McpServerProcess.CallAsync(_client, tool, arguments);
}
