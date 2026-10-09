using Contexo.Core.Abstractions;

namespace Contexo.Core.Tests.Indexing;

/// <summary>Initial scan, changes, renames, deletions and the protections against deleting by mistake.</summary>
public sealed class ReconciliationTests
{
    private static readonly DateTime Old = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<IndexingFixture> StartWithFilesAsync(int count, TimeSpan? parseDelay = null, string extension = ".txt")
    {
        var fixture = await IndexingFixture.CreateAsync();
        if (parseDelay is { } delay)
        {
            fixture.Parser.Delay = delay;
        }

        await fixture.AddFolderAsync("docs");
        for (var i = 0; i < count; i++)
        {
            fixture.WriteFile("docs", $"file{i:00}{extension}", $"content number {i}", Old.AddMinutes(i));
        }

        return fixture;
    }

    [Fact]
    public async Task Initial_scan_writes_every_file_and_goes_from_indexing_to_idle()
    {
        await using var fixture = await StartWithFilesAsync(10, TimeSpan.FromMilliseconds(40));
        var folder = (await fixture.Store.GetFoldersAsync(CancellationToken.None)).Single();

        await fixture.StartAsync();
        var states = new List<IndexingState>();
        var peakTotal = 0;
        var peakProcessed = 0;
        await IndexingFixture.WaitUntilAsync(
            () =>
            {
                var snapshot = fixture.Service.Current;
                states.Add(snapshot.State);
                peakTotal = Math.Max(peakTotal, snapshot.TotalFiles);
                peakProcessed = Math.Max(peakProcessed, snapshot.ProcessedFiles);
                return snapshot.State == IndexingState.Idle && states.Contains(IndexingState.Indexing);
            },
            what: "indexing then idle");

        Assert.Contains(IndexingState.Indexing, states);
        Assert.Equal(10, peakTotal);
        Assert.InRange(peakProcessed, 1, 9);
        Assert.Equal(0, fixture.Service.Current.TotalFiles);
        Assert.Equal(0, fixture.Service.Current.ProcessedFiles);
        Assert.Equal(10, fixture.Parser.TotalCalls);

        var documents = await fixture.DocumentsAsync(folder.Id);
        Assert.Equal(10, documents.Count);
        Assert.All(documents, d =>
        {
            Assert.Equal(DocumentStatus.Indexed, d.Status);
            Assert.True(d.ChunkCount > 0);
        });

        // Statistics and the activity feed arrive in the published snapshots.
        await IndexingFixture.WaitUntilAsync(
            () =>
            {
                var current = fixture.Service.Current;
                return current.Folders.Count == 1
                    && current.Folders[0].TotalFiles == 10
                    && current.Folders[0].IndexedFiles == 10
                    && current.Folders[0].FailedFiles == 0
                    && current.Folders[0].PendingFiles == 0;
            },
            what: "folder statistics");
        Assert.Contains(fixture.Service.Current.RecentActivity, a => a.Kind == ActivityKind.Added && a.FileCount == 10);
        lock (fixture.Snapshots)
        {
            Assert.Equal(IndexingState.Idle, fixture.Snapshots[^1].State);
        }

        Assert.Equal(FolderState.Active, (await fixture.FolderAsync(folder.Id)).State);
        Assert.NotNull((await fixture.FolderAsync(folder.Id)).LastScanAt);
    }

    [Fact]
    public async Task Newest_files_are_processed_first()
    {
        var options = IndexingFixture.FastOptions with { FullSpeedWorkers = 1 };
        await using var single = await IndexingFixture.CreateAsync(options);
        await single.AddFolderAsync("docs");
        single.WriteFile("docs", "oldest.txt", "a", Old);
        single.WriteFile("docs", "middle.txt", "b", Old.AddDays(1));
        single.WriteFile("docs", "newest.txt", "c", Old.AddDays(2));
        var order = new List<string>();
        single.Parser.OnParse = name => order.Add(name);

        await single.StartAsync();
        await single.WaitIdleAsync();

        Assert.Equal(["newest.txt", "middle.txt", "oldest.txt"], order);
    }

    [Fact]
    public async Task Changed_content_is_parsed_again_but_a_touched_file_is_not()
    {
        await using var fixture = await StartWithFilesAsync(3);
        var folder = (await fixture.Store.GetFoldersAsync(CancellationToken.None)).Single();
        await fixture.StartAsync();
        await fixture.WaitIdleAsync();
        Assert.Equal(3, fixture.Parser.TotalCalls);
        var before = (await fixture.DocumentsAsync(folder.Id)).Single(d => d.Path.EndsWith("file01.txt", StringComparison.Ordinal));

        // Only the modification time changes: the content hash is the same, so nothing is parsed.
        var touched = Old.AddDays(10);
        File.SetLastWriteTimeUtc(Path.Combine(fixture.FolderPath("docs"), "file01.txt"), touched);
        fixture.Service.RequestRescan(folder.Id);
        await fixture.WaitIdleAsync();

        Assert.Equal(3, fixture.Parser.TotalCalls);
        var afterTouch = (await fixture.DocumentsAsync(folder.Id)).Single(d => d.Path.EndsWith("file01.txt", StringComparison.Ordinal));
        Assert.Equal(new DateTimeOffset(touched, TimeSpan.Zero), afterTouch.Fingerprint.LastWriteUtc);
        Assert.Equal(before.Fingerprint.ContentHash, afterTouch.Fingerprint.ContentHash);
        Assert.Equal(before.ChunkCount, afterTouch.ChunkCount);
        Assert.Equal(before.Id, afterTouch.Id);

        // Real edit.
        fixture.WriteFile("docs", "file01.txt", "completely different words", Old.AddDays(11));
        fixture.Service.RequestRescan(folder.Id);
        await fixture.WaitIdleAsync();

        Assert.Equal(4, fixture.Parser.TotalCalls);
        Assert.Equal(1, fixture.Parser.CallsFor("file01.txt") - 1);
        var afterEdit = (await fixture.DocumentsAsync(folder.Id)).Single(d => d.Path.EndsWith("file01.txt", StringComparison.Ordinal));
        Assert.NotEqual(before.Fingerprint.ContentHash, afterEdit.Fingerprint.ContentHash);
        var hits = await fixture.Store.KeywordSearchAsync("\"completely different\"", [], 10, CancellationToken.None);
        Assert.Single(hits);
    }

    [Fact]
    public async Task A_few_deleted_files_are_removed_from_the_database()
    {
        await using var fixture = await StartWithFilesAsync(10);
        var folder = (await fixture.Store.GetFoldersAsync(CancellationToken.None)).Single();
        await fixture.StartAsync();
        await fixture.WaitIdleAsync();

        File.Delete(Path.Combine(fixture.FolderPath("docs"), "file00.txt"));
        File.Delete(Path.Combine(fixture.FolderPath("docs"), "file01.txt"));
        fixture.Service.RequestRescan(folder.Id);
        await fixture.WaitIdleAsync();

        var documents = await fixture.DocumentsAsync(folder.Id);
        Assert.Equal(8, documents.Count);
        Assert.DoesNotContain(documents, d => d.Path.EndsWith("file00.txt", StringComparison.Ordinal));
        Assert.Empty(fixture.MassDeletions);
        Assert.Equal(FolderState.Active, (await fixture.FolderAsync(folder.Id)).State);
    }

    [Fact]
    public async Task A_mass_disappearance_waits_for_the_users_answer_and_keeps_data_when_declined()
    {
        await using var fixture = await StartWithFilesAsync(40);
        var folder = (await fixture.Store.GetFoldersAsync(CancellationToken.None)).Single();
        await fixture.StartAsync();
        await fixture.WaitIdleAsync();

        for (var i = 0; i < 25; i++)
        {
            File.Delete(Path.Combine(fixture.FolderPath("docs"), $"file{i:00}.txt"));
        }

        fixture.Service.RequestRescan(folder.Id);
        await fixture.WaitIdleAsync();

        var asked = Assert.Single(fixture.MassDeletions);
        Assert.Equal(folder.Id, asked.FolderId);
        Assert.Equal(25, asked.MissingFileCount);
        Assert.Equal(40, asked.TotalFileCount);
        Assert.Equal(40, (await fixture.DocumentsAsync(folder.Id)).Count);
        Assert.Equal(FolderState.AwaitingDeletionConfirmation, (await fixture.FolderAsync(folder.Id)).State);

        // Nothing is deleted while the question is open, even if another scan happens.
        fixture.Service.RequestRescan(folder.Id);
        await fixture.WaitIdleAsync();
        Assert.Equal(40, (await fixture.DocumentsAsync(folder.Id)).Count);
        Assert.Single(fixture.MassDeletions);

        await fixture.Service.ResolveMassDeletionAsync(folder.Id, deleteMissing: false, CancellationToken.None);
        Assert.Equal(FolderState.Active, (await fixture.FolderAsync(folder.Id)).State);
        Assert.Equal(40, (await fixture.DocumentsAsync(folder.Id)).Count);

        // The same batch is not asked about again within 24 hours.
        fixture.Service.RequestRescan(folder.Id);
        await fixture.WaitIdleAsync();
        Assert.Single(fixture.MassDeletions);
        Assert.Equal(40, (await fixture.DocumentsAsync(folder.Id)).Count);

        fixture.Time.Advance(TimeSpan.FromHours(25));
        fixture.Service.RequestRescan(folder.Id);
        await fixture.WaitIdleAsync();
        Assert.Equal(2, fixture.MassDeletions.Count);
        Assert.Equal(40, (await fixture.DocumentsAsync(folder.Id)).Count);
    }

    [Fact]
    public async Task Confirming_a_mass_disappearance_deletes_the_missing_documents_only()
    {
        await using var fixture = await StartWithFilesAsync(40);
        var folder = (await fixture.Store.GetFoldersAsync(CancellationToken.None)).Single();
        await fixture.StartAsync();
        await fixture.WaitIdleAsync();

        for (var i = 0; i < 25; i++)
        {
            File.Delete(Path.Combine(fixture.FolderPath("docs"), $"file{i:00}.txt"));
        }

        fixture.Service.RequestRescan(folder.Id);
        await fixture.WaitIdleAsync();
        Assert.Single(fixture.MassDeletions);
        Assert.Equal(40, (await fixture.DocumentsAsync(folder.Id)).Count);

        await fixture.Service.ResolveMassDeletionAsync(folder.Id, deleteMissing: true, CancellationToken.None);

        var documents = await fixture.DocumentsAsync(folder.Id);
        Assert.Equal(15, documents.Count);
        Assert.All(documents, d => Assert.True(File.Exists(d.Path)));
        Assert.Equal(FolderState.Active, (await fixture.FolderAsync(folder.Id)).State);
    }

    [Fact]
    public async Task A_folder_that_disappears_is_marked_unavailable_and_keeps_all_its_data()
    {
        var options = IndexingFixture.FastOptions with { UnavailableCheckInterval = TimeSpan.FromMilliseconds(150) };
        await using var fixture = await IndexingFixture.CreateAsync(options);
        var folder = await fixture.AddFolderAsync("docs");
        for (var i = 0; i < 30; i++)
        {
            fixture.WriteFile("docs", $"file{i:00}.txt", $"content {i}");
        }

        await fixture.StartAsync();
        await fixture.WaitIdleAsync();
        Assert.Equal(30, fixture.Parser.TotalCalls);

        var hidden = fixture.FolderPath("docs") + "-renamed";
        Directory.Move(fixture.FolderPath("docs"), hidden);
        fixture.Service.RequestRescan(folder.Id);
        await IndexingFixture.WaitUntilAsync(
            async () => (await fixture.FolderAsync(folder.Id)).State == FolderState.Unavailable,
            what: "folder unavailable");
        await fixture.WaitIdleAsync();

        Assert.Equal(30, (await fixture.DocumentsAsync(folder.Id)).Count);
        Assert.Empty(fixture.MassDeletions);

        // Back under the old name: the periodic check notices on its own, without parsing anything again.
        Directory.Move(hidden, fixture.FolderPath("docs"));
        await IndexingFixture.WaitUntilAsync(
            async () => (await fixture.FolderAsync(folder.Id)).State == FolderState.Active,
            what: "folder active again");
        await fixture.WaitIdleAsync();

        Assert.Equal(30, (await fixture.DocumentsAsync(folder.Id)).Count);
        Assert.Equal(30, fixture.Parser.TotalCalls);
    }

    [Fact]
    public async Task A_folder_that_is_missing_at_startup_is_marked_unavailable_and_keeps_its_data()
    {
        await using var fixture = await IndexingFixture.CreateAsync();
        var folder = await fixture.AddFolderAsync("docs");
        fixture.WriteFile("docs", "a.txt", "alpha");
        await fixture.StartAsync();
        await fixture.WaitIdleAsync();
        await fixture.Service.StopAsync(CancellationToken.None);

        Directory.Delete(fixture.FolderPath("docs"), recursive: true);
        var restarted = fixture.CreateService();
        await restarted.StartAsync(CancellationToken.None);
        await IndexingFixture.WaitUntilAsync(
            async () => (await fixture.FolderAsync(folder.Id)).State == FolderState.Unavailable,
            what: "unavailable at startup");
        await restarted.StopAsync(CancellationToken.None);
        restarted.Dispose();

        Assert.Single(await fixture.DocumentsAsync(folder.Id));
    }

    [Fact]
    public async Task A_renamed_file_is_moved_without_parsing_it_again()
    {
        await using var fixture = await StartWithFilesAsync(5);
        var folder = (await fixture.Store.GetFoldersAsync(CancellationToken.None)).Single();
        await fixture.StartAsync();
        await fixture.WaitIdleAsync();
        var before = (await fixture.DocumentsAsync(folder.Id)).Single(d => d.Path.EndsWith("file02.txt", StringComparison.Ordinal));
        var chunkIds = await fixture.StoreFx.ChunkIdsAsync(before.Path);

        var renamed = Path.Combine(fixture.FolderPath("docs"), "renamed.txt");
        File.Move(before.Path, renamed);
        fixture.Service.RequestRescan(folder.Id);
        await fixture.WaitIdleAsync();

        Assert.Equal(5, fixture.Parser.TotalCalls);
        var documents = await fixture.DocumentsAsync(folder.Id);
        Assert.Equal(5, documents.Count);
        var after = documents.Single(d => d.Id == before.Id);
        Assert.Equal(renamed, after.Path);
        Assert.Equal(chunkIds, await fixture.StoreFx.ChunkIdsAsync(renamed));
        Assert.Contains(fixture.Service.Current.RecentActivity, a => a.Kind == ActivityKind.Moved);
    }

    [Fact]
    public async Task A_file_moved_into_a_sub_folder_is_also_recognised()
    {
        await using var fixture = await StartWithFilesAsync(3);
        var folder = (await fixture.Store.GetFoldersAsync(CancellationToken.None)).Single();
        await fixture.StartAsync();
        await fixture.WaitIdleAsync();
        var before = (await fixture.DocumentsAsync(folder.Id)).Single(d => d.Path.EndsWith("file00.txt", StringComparison.Ordinal));

        var target = Path.Combine(fixture.FolderPath("docs"), "archive", "file00.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(before.Path, target);
        fixture.Service.RequestRescan(folder.Id);
        await fixture.WaitIdleAsync();

        Assert.Equal(3, fixture.Parser.TotalCalls);
        Assert.Equal(target, (await fixture.DocumentsAsync(folder.Id)).Single(d => d.Id == before.Id).Path);
    }

    [Fact]
    public async Task A_new_file_with_the_same_size_but_different_content_is_not_mistaken_for_a_move()
    {
        await using var fixture = await IndexingFixture.CreateAsync();
        var folder = await fixture.AddFolderAsync("docs");
        var original = fixture.WriteFile("docs", "a.txt", "aaaa");
        await fixture.StartAsync();
        await fixture.WaitIdleAsync();

        File.Delete(original);
        fixture.WriteFile("docs", "b.txt", "bbbb");
        fixture.Service.RequestRescan(folder.Id);
        await fixture.WaitIdleAsync();

        Assert.Equal(2, fixture.Parser.TotalCalls);
        var documents = await fixture.DocumentsAsync(folder.Id);
        Assert.Single(documents);
        Assert.EndsWith("b.txt", documents[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nested_watched_folders_do_not_read_the_same_file_twice()
    {
        await using var fixture = await IndexingFixture.CreateAsync();
        var outer = await fixture.AddFolderAsync("A");
        var inner = await fixture.AddFolderAsync(Path.Combine("A", "B"));
        fixture.WriteFile("A", "top.txt", "top level");
        fixture.WriteFile("A", Path.Combine("B", "inner.txt"), "inside B");
        fixture.WriteFile("A", Path.Combine("C", "other.txt"), "inside C");

        await fixture.StartAsync();
        await fixture.WaitIdleAsync();

        var outerDocuments = await fixture.DocumentsAsync(outer.Id);
        var innerDocuments = await fixture.DocumentsAsync(inner.Id);
        Assert.Equal(["other.txt", "top.txt"], outerDocuments.Select(d => Path.GetFileName(d.Path)).Order().ToArray());
        Assert.Equal(["inner.txt"], innerDocuments.Select(d => Path.GetFileName(d.Path)).ToArray());
        Assert.Equal(1, fixture.Parser.CallsFor("inner.txt"));
    }

    [Fact]
    public async Task Adding_an_inner_folder_later_moves_ownership_without_leaving_duplicates()
    {
        await using var fixture = await IndexingFixture.CreateAsync();
        var outer = await fixture.AddFolderAsync("A");
        fixture.WriteFile("A", Path.Combine("B", "inner.txt"), "inside B");
        await fixture.StartAsync();
        await fixture.WaitIdleAsync();
        Assert.Single(await fixture.DocumentsAsync(outer.Id));

        var inner = await fixture.AddFolderAsync(Path.Combine("A", "B"));
        fixture.Service.RequestRescan(null);
        await fixture.WaitIdleAsync();

        Assert.Empty(await fixture.DocumentsAsync(outer.Id));
        Assert.Single(await fixture.DocumentsAsync(inner.Id));
    }
}
