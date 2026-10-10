using System.Text.Json;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Contexo.Core.Tests.EndToEnd;

/// <summary>One line of queries.json.</summary>
public sealed record QueryCase(
    string Id,
    string Query,
    string[] ExpectedFiles,
    string Kind,
    string? ExpectedLocation,
    bool NeedsTable,
    bool ObserveOnly);

/// <summary>expected.json: the correct answers computed by the generator.</summary>
public sealed class ExpectedAnswers
{
    public string SalesFile { get; set; } = "";
    public int SalesRowCount { get; set; }
    public long SalesTotalAmount { get; set; }
    public string SalesTopCustomer { get; set; } = "";
    public long SalesTopCustomerAmount { get; set; }
    public string CustomerListFile { get; set; } = "";
    public int CustomerListRowCount { get; set; }
    public int CustomerListNorthCount { get; set; }
    public string OrderCsvFile { get; set; } = "";
    public int OrderCsvRowCount { get; set; }
    public int OrderCsvShippedCount { get; set; }
}

/// <summary>
/// Generated corpus + the full Contexo services (real parsers, chunker, SQLite, embedding model when present) indexed once.
/// Each fixture instance works on its own copy of the corpus, so tests that change files do not disturb the others.
/// </summary>
public sealed class CorpusFixture : IAsyncLifetime
{
    private string _root = "";
    private ServiceProvider? _provider;

    public string CorpusDirectory { get; private set; } = "";

    public string DataDirectory { get; private set; } = "";

    public string DatabasePath => Path.Combine(DataDirectory, "contexo.db");

    public bool ModelAvailable { get; private set; }

    public long FolderId { get; private set; }

    public TimeSpan InitialIndexTime { get; private set; }

    public IReadOnlyList<QueryCase> Queries { get; private set; } = [];

    public ExpectedAnswers Expected { get; private set; } = new();

    public IServiceProvider Services => _provider!;

    public IKnowledgeStore Store => Services.GetRequiredService<IKnowledgeStore>();

    public IIndexingService Indexing => Services.GetRequiredService<IIndexingService>();

    public ISearchService Search => Services.GetRequiredService<ISearchService>();

    public ITableQueryService Tables => Services.GetRequiredService<ITableQueryService>();

    public IEmbeddingService Embedding => Services.GetRequiredService<IEmbeddingService>();

    public async Task InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("contexo-e2e-").FullName;
        var corpusRoot = Path.Combine(_root, "corpus-root");
        E2EEnvironment.CopyCorpusTo(corpusRoot);
        CorpusDirectory = Path.Combine(corpusRoot, "corpus");
        DataDirectory = Path.Combine(_root, "data");

        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        Queries = JsonSerializer.Deserialize<List<QueryCase>>(File.ReadAllText(Path.Combine(corpusRoot, "queries.json")), json)!;
        Expected = JsonSerializer.Deserialize<ExpectedAnswers>(File.ReadAllText(Path.Combine(corpusRoot, "expected.json")), json)!;

        // Without a downloaded model the folder below stays empty and search falls back to keywords.
        var models = E2EEnvironment.FindModelsDirectory() ?? Path.Combine(_root, "no-models");
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(new AppPaths(new AppPathsOverrides { DataDirectory = DataDirectory, ModelsDirectory = models }));
        services.AddContexoCore();
        _provider = services.BuildServiceProvider();

        await Store.InitializeAsync(CancellationToken.None);
        FolderId = (await Store.AddFolderAsync(CorpusDirectory, CancellationToken.None)).Id;
        ModelAvailable = Embedding.IsAvailable;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Indexing.StartAsync(CancellationToken.None);
        await WaitUntilAsync(IsFullyIndexedAsync, TimeSpan.FromMinutes(10), "the first indexing run");
        clock.Stop();
        InitialIndexTime = clock.Elapsed;
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            try
            {
                await Indexing.StopAsync(CancellationToken.None);
            }
            finally
            {
                await _provider.DisposeAsync();
            }
        }

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

    /// <summary>True once every file of the corpus has a row and the indexing service has nothing left to do.</summary>
    public async Task<bool> IsFullyIndexedAsync()
    {
        var documents = await Store.GetDocumentsAsync(FolderId, CancellationToken.None);
        var snapshot = Indexing.Current;
        return snapshot.State == IndexingState.Idle
            && documents.Count >= Directory.GetFiles(CorpusDirectory).Length
            && snapshot.Folders.All(f => f.PendingFiles == 0);
    }

    /// <summary>Polls until <paramref name="condition"/> holds three times in a row (so a passing glimpse between two states does not count).</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string what)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var stable = 0;
        while (clock.Elapsed < timeout)
        {
            await Task.Delay(250);
            stable = await condition() ? stable + 1 : 0;
            if (stable >= 3)
            {
                return;
            }
        }

        throw new TimeoutException($"Timed out after {timeout} waiting for {what}.");
    }

    public async Task<SearchResponse> SearchAsync(string query, int topK = 10) =>
        await Search.SearchAsync(new SearchRequest(query, topK), CancellationToken.None);

    /// <summary>Runs a read-only query directly on the database file (for checks the public contracts cannot answer).</summary>
    public List<object?[]> Sql(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row.Select(v => v is DBNull ? null : v).ToArray());
        }

        return rows;
    }
}
