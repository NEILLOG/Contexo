using Contexo.Core.Abstractions;

namespace Contexo.Core.Tests.Storage;

public sealed class ExclusionActivityMaintenanceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Excluding_a_folder_removes_only_documents_below_it()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("A");
        var inside = f.PathOf("A", "報價", "x.txt");
        var nested = f.PathOf("A", "報價", "deeper", "y.txt");
        var sibling = f.PathOf("A", "報價單", "z.txt");
        var other = f.PathOf("A", "other.txt");
        foreach (var path in new[] { inside, nested, sibling, other })
        {
            await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, path, [StoreFixture.NoVector(0, "body of " + Path.GetFileName(path))]), Ct);
        }

        var version = await f.Store.GetIndexVersionAsync(Ct);
        var exclusion = await f.Store.AddExclusionAsync(f.PathOf("A", "報價"), isFolder: true, Ct);

        Assert.True(exclusion.IsFolder);
        Assert.Null(await f.Store.GetDocumentByPathAsync(inside, Ct));
        Assert.Null(await f.Store.GetDocumentByPathAsync(nested, Ct));
        Assert.NotNull(await f.Store.GetDocumentByPathAsync(sibling, Ct));
        Assert.NotNull(await f.Store.GetDocumentByPathAsync(other, Ct));
        Assert.Empty(await f.Store.KeywordSearchAsync("\"body of x.txt\"", [], 10, Ct));
        Assert.Equal(2, (await f.Store.GetDocumentsAsync(folder.Id, Ct)).Count);
        Assert.Equal(version + 1, await f.Store.GetIndexVersionAsync(Ct));
        Assert.Equal([exclusion.Path], (await f.Store.GetExclusionsAsync(Ct)).Select(e => e.Path));
    }

    [Fact]
    public async Task Excluding_a_file_removes_that_document_and_exclusions_can_be_removed()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("A");
        var secret = f.PathOf("A", "secret.docx");
        var fine = f.PathOf("A", "fine.docx");
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, secret, [StoreFixture.NoVector(0, "secret")]), Ct);
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, fine, [StoreFixture.NoVector(0, "fine")]), Ct);

        var exclusion = await f.Store.AddExclusionAsync(secret, isFolder: false, Ct);
        var again = await f.Store.AddExclusionAsync(secret, isFolder: false, Ct);

        Assert.Equal(exclusion.Id, again.Id);
        Assert.Null(await f.Store.GetDocumentByPathAsync(secret, Ct));
        Assert.NotNull(await f.Store.GetDocumentByPathAsync(fine, Ct));
        Assert.Single(await f.Store.GetExclusionsAsync(Ct));

        await f.Store.RemoveExclusionAsync(exclusion.Id, Ct);
        Assert.Empty(await f.Store.GetExclusionsAsync(Ct));
        Assert.NotNull(await f.Store.GetDocumentByPathAsync(fine, Ct));
    }

    [SkippableFact]
    [Trait("Category", "Windows")]
    public async Task Windows_paths_with_either_separator_are_matched()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.Store.AddFolderAsync(@"C:\A", Ct);
        foreach (var path in new[] { @"C:\A\報價\x.txt", @"C:\A\報價單\z.txt" })
        {
            await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, path, [StoreFixture.NoVector(0, "body")]), Ct);
        }

        await f.Store.AddExclusionAsync("C:/A/報價", isFolder: true, Ct);

        Assert.Null(await f.Store.GetDocumentByPathAsync(@"C:\A\報價\x.txt", Ct));
        Assert.NotNull(await f.Store.GetDocumentByPathAsync(@"c:\a\報價單\z.txt", Ct));
    }

    [Fact]
    public async Task Activity_summaries_group_by_client()
    {
        using var f = await StoreFixture.CreateAsync();
        var t0 = DateTimeOffset.UtcNow.AddHours(-5);
        async Task Record(McpEventKind kind, string client, string? version, string? tool, string? detail, int minutes) =>
            await f.Store.RecordMcpActivityAsync(new McpActivity(kind, client, version, tool, detail, t0.AddMinutes(minutes)), Ct);

        await Record(McpEventKind.Connected, "claude-ai", "1.0", null, null, 0);
        await Record(McpEventKind.ToolCall, "claude-ai", "1.0", "search", null, 10);
        await Record(McpEventKind.Connected, "claude-ai", "1.1", null, null, 20);
        await Record(McpEventKind.Error, "claude-ai", "1.1", "search", "first error", 30);
        await Record(McpEventKind.Error, "claude-ai", "1.1", "search", "second error", 40);
        await Record(McpEventKind.ToolCall, "claude-ai", "1.1", "search", null, 50);
        await Record(McpEventKind.Connected, "cursor", null, null, null, 5);

        var summaries = await f.Store.GetMcpActivitySummariesAsync(Ct);

        Assert.Equal(2, summaries.Count);
        var claude = summaries.Single(s => s.ClientName == "claude-ai");
        Assert.Equal("1.1", claude.ClientVersion);
        Assert.Equal(t0.AddMinutes(20), claude.LastConnectedAt);
        Assert.Equal(t0.AddMinutes(50), claude.LastToolCallAt);
        Assert.Equal(t0.AddMinutes(40), claude.LastErrorAt);
        Assert.Equal("second error", claude.LastError);
        var cursor = summaries.Single(s => s.ClientName == "cursor");
        Assert.Null(cursor.ClientVersion);
        Assert.Equal(t0.AddMinutes(5), cursor.LastConnectedAt);
        Assert.Null(cursor.LastToolCallAt);
        Assert.Null(cursor.LastErrorAt);
        Assert.Null(cursor.LastError);
    }

    [Fact]
    public async Task Activity_older_than_thirty_days_is_purged_when_recording()
    {
        using var f = await StoreFixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await f.ExecuteAsync(
            $"INSERT INTO mcp_activity(kind, client_name, at) VALUES (0, 'old-client', '{now.AddDays(-31).UtcDateTime:O}'), (0, 'edge-client', '{now.AddDays(-29).UtcDateTime:O}')");
        Assert.Equal(2, (await f.Store.GetMcpActivitySummariesAsync(Ct)).Count);

        var fresh = f.CreateStore(); // a new instance has not purged yet
        await fresh.RecordMcpActivityAsync(new McpActivity(McpEventKind.Connected, "new-client", "1", null, null, now), Ct);

        var names = (await f.Store.GetMcpActivitySummariesAsync(Ct)).Select(s => s.ClientName).Order().ToList();
        Assert.Equal(["edge-client", "new-client"], names);
    }

    [Fact]
    public async Task Purging_happens_at_most_once_a_day_per_instance()
    {
        using var f = await StoreFixture.CreateAsync();
        var clock = new FakeTime(DateTimeOffset.UtcNow);
        var store = new Contexo.Core.Storage.SqliteKnowledgeStore(
            new Contexo.Core.Common.AppPaths(new Contexo.Core.Common.AppPathsOverrides { DatabasePath = f.DatabasePath }),
            new Common.TestLogger<Contexo.Core.Storage.SqliteKnowledgeStore>(),
            clock);
        await store.RecordMcpActivityAsync(new McpActivity(McpEventKind.Connected, "a", null, null, null, clock.GetUtcNow()), Ct);

        await f.ExecuteAsync($"INSERT INTO mcp_activity(kind, client_name, at) VALUES (0, 'old', '{clock.GetUtcNow().AddDays(-40).UtcDateTime:O}')");
        clock.Advance(TimeSpan.FromHours(1));
        await store.RecordMcpActivityAsync(new McpActivity(McpEventKind.Connected, "a", null, null, null, clock.GetUtcNow()), Ct);
        Assert.Contains(await f.Store.GetMcpActivitySummariesAsync(Ct), s => s.ClientName == "old");

        clock.Advance(TimeSpan.FromHours(24));
        await store.RecordMcpActivityAsync(new McpActivity(McpEventKind.Connected, "a", null, null, null, clock.GetUtcNow()), Ct);
        Assert.DoesNotContain(await f.Store.GetMcpActivitySummariesAsync(Ct), s => s.ClientName == "old");
    }

    [Fact]
    public async Task Clearing_indexed_data_keeps_folders_exclusions_and_meta_but_shrinks_the_file()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("A");
        await f.Store.AddExclusionAsync(f.PathOf("A", "skip"), isFolder: true, Ct);
        await f.Store.SetMetaAsync("embedding_model", "bge", Ct);
        var filler = string.Concat(Enumerable.Repeat("這是一段很長的測試文字用來撐大資料庫檔案。", 40));
        for (var d = 0; d < 20; d++)
        {
            var chunks = Enumerable.Range(0, 30).Select(i => StoreFixture.WithVector(i, $"{filler} {d}-{i}", Enumerable.Range(0, 96).Select(x => (float)x).ToArray())).ToList();
            await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, f.PathOf("A", $"{d}.txt"), chunks, [StoreFixture.MakeTable($"S!A1:B{d + 100}")]), Ct);
        }

        await f.Store.RecordMcpActivityAsync(new McpActivity(McpEventKind.Connected, "client", null, null, null, DateTimeOffset.UtcNow), Ct);
        var before = await f.Store.GetStatisticsAsync(Ct);
        var versionBefore = await f.Store.GetIndexVersionAsync(Ct);
        Assert.Equal(20, before.DocumentCount);
        Assert.Equal(600, before.ChunkCount);

        await f.Store.ClearIndexedDataAsync(Ct);

        var after = await f.Store.GetStatisticsAsync(Ct);
        Assert.Equal(0, after.DocumentCount);
        Assert.Equal(0, after.ChunkCount);
        Assert.Equal(0, after.FailedDocumentCount);
        Assert.Equal(1, after.FolderCount);
        Assert.True(after.DatabaseBytes < before.DatabaseBytes, $"{after.DatabaseBytes} should be < {before.DatabaseBytes}");
        Assert.Single(await f.Store.GetExclusionsAsync(Ct));
        Assert.Single(await f.Store.GetFoldersAsync(Ct));
        Assert.Equal("bge", await f.Store.GetMetaAsync("embedding_model", Ct));
        Assert.Empty(await f.Store.GetMcpActivitySummariesAsync(Ct));
        Assert.Empty(await f.Store.KeywordSearchAsync("\"測試文字\"", [], 10, Ct));
        Assert.Equal(versionBefore + 1, await f.Store.GetIndexVersionAsync(Ct));

        // The store is still usable afterwards.
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, f.PathOf("A", "again.txt"), [StoreFixture.NoVector(0, "fresh start")]), Ct);
        Assert.Single(await f.Store.KeywordSearchAsync("\"fresh start\"", [], 10, Ct));
    }

    [Fact]
    public async Task Statistics_count_failed_documents_and_include_the_wal_file()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("A");
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, f.PathOf("A", "ok.txt"), [StoreFixture.NoVector(0, "ok")]), Ct);
        await f.Store.MarkDocumentAsync(folder.Id, f.PathOf("A", "bad.txt"), StoreFixture.Fingerprint(), DocumentStatus.Failed, DocumentErrorCode.Corrupted, "x", null, false, Ct);

        var stats = await f.Store.GetStatisticsAsync(Ct);

        Assert.Equal(1, stats.FolderCount);
        Assert.Equal(2, stats.DocumentCount);
        Assert.Equal(1, stats.FailedDocumentCount);
        Assert.Equal(1, stats.ChunkCount);
        var expected = new FileInfo(f.DatabasePath).Length + (File.Exists(f.DatabasePath + "-wal") ? new FileInfo(f.DatabasePath + "-wal").Length : 0);
        Assert.Equal(expected, stats.DatabaseBytes);
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
