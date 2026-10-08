using Contexo.Core.Abstractions;

namespace Contexo.Core.Tests.Storage;

public sealed class SchemaAndFolderTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Initialize_is_repeatable_and_sets_schema_version_and_wal()
    {
        using var f = new StoreFixture();

        await f.Store.InitializeAsync(Ct);
        await f.Store.InitializeAsync(Ct);
        await f.CreateStore().InitializeAsync(Ct);

        Assert.Equal(1L, await f.ScalarAsync<long>("PRAGMA user_version"));
        Assert.Equal("wal", await f.ScalarAsync<string>("PRAGMA journal_mode"));
        Assert.Equal(0, await f.Store.GetIndexVersionAsync(Ct));
    }

    [Fact]
    public async Task Initialize_refuses_a_database_from_a_newer_version()
    {
        using var f = await StoreFixture.CreateAsync();
        await f.ExecuteAsync("PRAGMA user_version = 99");

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.CreateStore().InitializeAsync(Ct));
    }

    [Fact]
    public async Task Meta_values_round_trip_and_overwrite()
    {
        using var f = await StoreFixture.CreateAsync();

        Assert.Null(await f.Store.GetMetaAsync("model", Ct));
        await f.Store.SetMetaAsync("model", "a", Ct);
        await f.Store.SetMetaAsync("model", "b", Ct);

        Assert.Equal("b", await f.Store.GetMetaAsync("model", Ct));
    }

    [Fact]
    public async Task Adding_the_same_folder_twice_returns_the_same_record()
    {
        using var f = await StoreFixture.CreateAsync();
        var path = f.PathOf("報價資料");

        var first = await f.Store.AddFolderAsync(path, Ct);
        var second = await f.Store.AddFolderAsync(path + Path.DirectorySeparatorChar, Ct);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("報價資料", first.DisplayName);
        Assert.Equal(FolderState.Active, first.State);
        Assert.Null(first.LastScanAt);
        Assert.Single(await f.Store.GetFoldersAsync(Ct));
    }

    [Fact]
    public async Task Folder_paths_are_matched_case_insensitively()
    {
        using var f = await StoreFixture.CreateAsync();

        var first = await f.Store.AddFolderAsync(f.PathOf("Docs"), Ct);
        var second = await f.Store.AddFolderAsync(f.PathOf("DOCS"), Ct);

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task Excluded_subfolders_and_state_are_saved()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var scan = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero);

        await f.Store.SetFolderExclusionsAsync(folder.Id, ["舊資料/2019", "草稿"], Ct);
        await f.Store.SetFolderStateAsync(folder.Id, FolderState.Unavailable, scan, Ct);

        var loaded = Assert.Single(await f.Store.GetFoldersAsync(Ct));
        Assert.Equal(["舊資料/2019", "草稿"], loaded.ExcludedSubfolders);
        Assert.Equal(FolderState.Unavailable, loaded.State);
        Assert.Equal(scan, loaded.LastScanAt);

        await f.Store.SetFolderStateAsync(folder.Id, FolderState.Active, null, Ct);
        loaded = Assert.Single(await f.Store.GetFoldersAsync(Ct));
        Assert.Equal(FolderState.Active, loaded.State);
        Assert.Equal(scan, loaded.LastScanAt);
    }

    [Fact]
    public async Task Removing_a_folder_deletes_its_documents_chunks_and_search_entries()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var other = await f.AddFolderAsync("other");
        var path = f.PathOf("docs", "a.txt");
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, path, [StoreFixture.WithVector(0, "alpha bravo charlie", 1f, 2f)]), Ct);
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(other.Id, f.PathOf("other", "b.txt"), [StoreFixture.WithVector(0, "alpha bravo delta", 3f, 4f)]), Ct);
        var versionBefore = await f.Store.GetIndexVersionAsync(Ct);

        await f.Store.RemoveFolderAsync(folder.Id, Ct);

        Assert.Equal([other.Id], (await f.Store.GetFoldersAsync(Ct)).Select(x => x.Id));
        Assert.Null(await f.Store.GetDocumentByPathAsync(path, Ct));
        var hits = await f.Store.KeywordSearchAsync("\"alpha bravo\"", [], 10, Ct);
        Assert.Single(hits);
        Assert.Equal(1L, await f.ScalarAsync<long>("SELECT COUNT(*) FROM chunks"));
        Assert.Equal(1L, await f.ScalarAsync<long>("SELECT COUNT(*) FROM embeddings"));
        Assert.True(await f.Store.GetIndexVersionAsync(Ct) > versionBefore);
        Assert.Equal(0L, await f.ScalarAsync<long>("SELECT COUNT(*) FROM chunks_fts WHERE chunks_fts MATCH '\"charlie\"'"));
    }

    [SkippableFact]
    [Trait("Category", "Windows")]
    public async Task Drive_roots_use_the_drive_letter_as_display_name()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        using var f = await StoreFixture.CreateAsync();

        var folder = await f.Store.AddFolderAsync(@"D:\", Ct);

        Assert.Equal("D:", folder.DisplayName);
    }
}
