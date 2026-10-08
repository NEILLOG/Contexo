using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Contexo.Core.Storage;
using Contexo.Core.Tests.Common;
using Microsoft.Data.Sqlite;

namespace Contexo.Core.Tests.Storage;

/// <summary>A store on a fresh temporary database plus helpers for building test data.</summary>
internal sealed class StoreFixture : IDisposable
{
    public const string Model = "test-model";

    private readonly TempDirectory _directory = new();
    private readonly AppPaths _paths;

    public StoreFixture()
    {
        _paths = new AppPaths(new AppPathsOverrides { DatabasePath = _directory.Combine("data", "contexo.db") });
        Store = CreateStore();
    }

    public SqliteKnowledgeStore Store { get; }

    public string DatabasePath => _paths.DatabasePath;

    public string Root => _directory.Path;

    public SqliteKnowledgeStore CreateStore() => new(_paths, new TestLogger<SqliteKnowledgeStore>());

    public static async Task<StoreFixture> CreateAsync()
    {
        var fixture = new StoreFixture();
        await fixture.Store.InitializeAsync(CancellationToken.None);
        return fixture;
    }

    public string PathOf(params string[] parts) => _directory.Combine(parts);

    public async Task<WatchedFolder> AddFolderAsync(params string[] parts) =>
        await Store.AddFolderAsync(PathOf(parts), CancellationToken.None);

    public static FileFingerprint Fingerprint(string hash = "hash", long size = 10, DateTimeOffset? lastWrite = null) =>
        new(size, lastWrite ?? new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), hash);

    public static Chunk MakeChunk(int ordinal, string text, string? tableKey = null, SectionKind kind = SectionKind.Prose, SourceLocation? location = null) =>
        new(ordinal, kind, text, "title › " + text, location ?? new SourceLocation { Page = ordinal + 1 }, tableKey);

    public static DocumentWrite Write(long folderId, string path, IReadOnlyList<ChunkWrite> chunks, IReadOnlyList<SpreadsheetTable>? tables = null, string? hash = null) =>
        new(folderId, path, Fingerprint(hash ?? "h"), chunks.Any(c => c.Vector is not null) ? Model : null, chunks, tables ?? []);

    public static ChunkWrite WithVector(int ordinal, string text, params float[] vector) => new(MakeChunk(ordinal, text), vector);

    public static ChunkWrite NoVector(int ordinal, string text) => new(MakeChunk(ordinal, text), null);

    public static SpreadsheetTable MakeTable(string key = "Sheet1!A1:C300") => new(
        key,
        "Sheet1",
        "A1:C300",
        1,
        ["品名", "數量", "單價"],
        299,
        [["螺絲", "10", "1.5"], ["螺帽", "20", "0.5"]],
        "報價表，欄位：品名、數量、單價");

    public async Task<List<long>> ChunkIdsAsync(string path)
    {
        var ids = new List<long>();
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.id FROM chunks c JOIN documents d ON d.id = c.document_id WHERE d.path = $p ORDER BY c.ordinal";
        command.Parameters.AddWithValue("$p", path);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    /// <summary>Runs a raw scalar query on a separate connection (for checks the public API cannot express).</summary>
    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; " + sql;
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }

    private SqliteConnection Open() => new($"Data Source={DatabasePath}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _directory.Dispose();
    }
}
