using System.Text;
using Microsoft.Data.Sqlite;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Contexo.Core.Parsing.Spreadsheet;
using Contexo.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contexo.Mcp.Tests.Support;

/// <summary>A temporary Contexo database filled with generated documents (never real company files), plus its data folder.</summary>
internal sealed class TestDatabase : IDisposable
{
    public const string QuoteQuery = "監視系統建置";
    public const string LongQuery = "冗長說明";
    public const string SlideQuery = "第三季營收";

    private TestDatabase(string root)
    {
        Root = root;
        DataDirectory = Path.Combine(root, "data");
        DatabasePath = Path.Combine(root, "db", "contexo.db");
        DocsDirectory = Path.Combine(root, "docs");
        ModelsDirectory = Path.Combine(root, "no-models");
        Directory.CreateDirectory(DocsDirectory);
    }

    public string Root { get; }

    public string DataDirectory { get; }

    public string DatabasePath { get; }

    public string DocsDirectory { get; }

    /// <summary>Never created, so the embedding model is unavailable and search runs on keywords only.</summary>
    public string ModelsDirectory { get; }

    public string CsvPath => Path.Combine(DocsDirectory, "訂單.csv");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public AppPaths Paths => new(new AppPathsOverrides { DataDirectory = DataDirectory, DatabasePath = DatabasePath, ModelsDirectory = ModelsDirectory });

    public SqliteKnowledgeStore CreateStore() => new(Paths, NullLogger<SqliteKnowledgeStore>.Instance);

    /// <param name="withData">False leaves the database empty (but created and migrated).</param>
    public static async Task<TestDatabase> CreateAsync(bool withData = true)
    {
        var database = new TestDatabase(Directory.CreateTempSubdirectory("contexo-mcp-test-").FullName);
        var store = database.CreateStore();
        await store.InitializeAsync(CancellationToken.None);
        if (withData)
        {
            await database.FillAsync(store);
        }

        return database;
    }

    public IReadOnlyList<(int Kind, string Client, string? Tool, string? Detail)> ReadActivityRows()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, client_name, tool_name, detail FROM mcp_activity ORDER BY id";
        using var reader = command.ExecuteReader();
        var rows = new List<(int, string, string?, string?)>();
        while (reader.Read())
        {
            rows.Add((reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a log file may still be open for a moment.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task FillAsync(IKnowledgeStore store)
    {
        var folder = await store.AddFolderAsync(DocsDirectory, CancellationToken.None);
        var cancellation = CancellationToken.None;

        await store.ReplaceDocumentAsync(Write(folder.Id, "2025_台中案_報價單.docx",
        [
            Chunk(0, SectionKind.Prose, $"{QuoteQuery}報價總計 128 萬元，含稅，交貨期 30 天。",
                new SourceLocation { HeadingPath = ["報價條款", "付款方式"] }),
        ]), cancellation);

        await store.ReplaceDocumentAsync(Write(folder.Id, "簡報.pptx",
        [
            Chunk(0, SectionKind.Slide, $"{SlideQuery}成長百分之十二。", new SourceLocation { Slide = 3, Title = "營收摘要" }),
            Chunk(1, SectionKind.TableSummary, "內嵌訂單明細，欄位：客戶、金額。",
                new SourceLocation { EmbeddedPath = ["內嵌.xlsx"], Sheet = "Sheet1", CellRange = "A1:F20" }, tableKey: "內嵌.xlsx#Sheet1!A1:F20"),
        ],
        [
            new SpreadsheetTable("內嵌.xlsx#Sheet1!A1:F20", "Sheet1", "A1:F20", 1, ["客戶", "金額"], 19, [["甲", "100"]], "內嵌訂單明細，欄位：客戶、金額。"),
        ]), cancellation);

        var csv = new StringBuilder("客戶,金額,日期\n");
        for (var i = 1; i <= 600; i++)
        {
            csv.Append(i % 3 == 0 ? "甲" : i % 3 == 1 ? "乙" : "丙").Append(',').Append(i * 10).Append(",2025-01-").Append((i % 28 + 1).ToString("00")).Append('\n');
        }

        var csvBytes = new UTF8Encoding(false).GetBytes(csv.ToString());
        await File.WriteAllBytesAsync(CsvPath, csvBytes, cancellation);
        using var stream = new MemoryStream(csvBytes);
        var parsed = await new SpreadsheetParser().ParseAsync(new ParseContext(stream, "訂單.csv", new ParserOptions()), cancellation);
        var table = Assert.Single(parsed.Tables);
        await store.ReplaceDocumentAsync(Write(folder.Id, "訂單.csv",
        [
            Chunk(0, SectionKind.TableSummary, "訂單清單，欄位：客戶、金額、日期。",
                new SourceLocation { Sheet = table.Sheet, CellRange = table.CellRange }, tableKey: table.TableKey),
        ],
        [table]), cancellation);

        await store.ReplaceDocumentAsync(Write(folder.Id, "長文.txt",
        [
            Chunk(0, SectionKind.Prose, string.Concat(Enumerable.Repeat($"{LongQuery}，", 800)), SourceLocation.None),
        ]), cancellation);
    }

    private DocumentWrite Write(long folderId, string fileName, IReadOnlyList<Chunk> chunks, IReadOnlyList<SpreadsheetTable>? tables = null) =>
        new(folderId, Path.Combine(DocsDirectory, fileName), new FileFingerprint(1000, DateTimeOffset.UtcNow, "hash-" + fileName), null,
            [.. chunks.Select(c => new ChunkWrite(c, null))], tables ?? []);

    private static Chunk Chunk(int ordinal, SectionKind kind, string text, SourceLocation location, string? tableKey = null) =>
        new(ordinal, kind, text, text, location, tableKey);

    /// <summary>The table id the store gave to the table summary found by searching for <paramref name="query"/>.</summary>
    public static async Task<string> TableIdAsync(ISearchService search, string query)
    {
        var response = await search.SearchAsync(new SearchRequest(query, 10), CancellationToken.None);
        return response.Hits.First(h => h.TableId is not null).TableId!;
    }
}
