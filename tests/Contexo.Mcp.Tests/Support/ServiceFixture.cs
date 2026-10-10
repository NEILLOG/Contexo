using Contexo.Core.Abstractions;
using Contexo.Core.Embedding;
using Contexo.Core.Parsing.Spreadsheet;
using Contexo.Core.Search;
using Contexo.Core.Storage;
using Contexo.Core.Tables;
using Contexo.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contexo.Mcp.Tests.Support;

/// <summary>The real store, search and table services over a <see cref="TestDatabase"/>, without a model (keyword search only).</summary>
internal sealed class ServiceFixture : IDisposable
{
    private readonly OnnxEmbeddingService _embedding;
    private readonly TableQueryService _tables;

    private ServiceFixture(TestDatabase database, SqliteKnowledgeStore store, ITableQueryService? tableOverride, ISearchService? searchOverride)
    {
        Database = database;
        Store = store;
        _embedding = new OnnxEmbeddingService(database.ModelsDirectory, NullLogger<OnnxEmbeddingService>.Instance);
        Search = searchOverride ?? new HybridSearchService(store, _embedding, NullLogger<HybridSearchService>.Instance);
        _tables = new TableQueryService(store, new SpreadsheetRegionReader(), NullLogger<TableQueryService>.Instance);
        Service = new ContexoToolService(store, Search, tableOverride ?? _tables, NullLogger<ContexoToolService>.Instance);
    }

    public TestDatabase Database { get; }

    public SqliteKnowledgeStore Store { get; }

    public ISearchService Search { get; }

    public ContexoToolService Service { get; }

    public static async Task<ServiceFixture> CreateAsync(bool withData = true, ITableQueryService? tableOverride = null, ISearchService? searchOverride = null)
    {
        var database = await TestDatabase.CreateAsync(withData);
        return new ServiceFixture(database, database.CreateStore(), tableOverride, searchOverride);
    }

    public void Dispose()
    {
        _tables.Dispose();
        _embedding.Dispose();
        Database.Dispose();
    }
}
