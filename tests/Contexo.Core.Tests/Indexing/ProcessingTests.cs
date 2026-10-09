using Contexo.Core.Abstractions;
using Contexo.Core.Indexing;
using Contexo.Core.Tests.Storage;

namespace Contexo.Core.Tests.Indexing;

/// <summary>What happens to a single file: errors, embedded files, vectors, exclusions and categories.</summary>
public sealed class ProcessingTests
{
    private static readonly DateTime Old = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<(IndexingFixture Fixture, WatchedFolder Folder)> CreateAsync(IndexingOptions? options = null)
    {
        var fixture = await IndexingFixture.CreateAsync(options);
        var folder = await fixture.AddFolderAsync("docs");
        return (fixture, folder);
    }

    private static async Task<DocumentRecord> OnlyDocumentAsync(IndexingFixture fixture, WatchedFolder folder, string fileName) =>
        (await fixture.DocumentsAsync(folder.Id)).Single(d => Path.GetFileName(d.Path) == fileName);

    [Fact]
    public async Task A_locked_file_keeps_its_old_data_and_is_retried_after_five_minutes()
    {
        var (fixture, folder) = await CreateAsync();
        await using var _ = fixture;
        var path = fixture.WriteFile("docs", "contract.txt", "original wording", Old);
        await fixture.StartAsync();
        await fixture.WaitIdleAsync();
        var before = await OnlyDocumentAsync(fixture, folder, "contract.txt");
        Assert.Equal(DocumentStatus.Indexed, before.Status);

        // Somebody has the file open exclusively while it is being edited.
        FileStream? locker = null;
        try
        {
            File.WriteAllText(path, "edited wording that is longer than before");
            File.SetLastWriteTimeUtc(path, Old.AddDays(1));
            locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            fixture.Service.RequestRescan(folder.Id);
            await fixture.WaitIdleAsync();

            var locked = await OnlyDocumentAsync(fixture, folder, "contract.txt");
            Assert.Equal(DocumentStatus.Failed, locked.Status);
            Assert.Equal(DocumentErrorCode.Locked, locked.ErrorCode);
            Assert.NotNull(locked.NextRetryAt);
            Assert.True(locked.NextRetryAt > fixture.Time.GetUtcNow().AddMinutes(4));
            Assert.Equal(before.ChunkCount, locked.ChunkCount);
            Assert.Equal(before.Fingerprint, locked.Fingerprint);
            Assert.Single(await fixture.Store.KeywordSearchAsync("\"original wording\"", [], 10, CancellationToken.None));

            // Still locked and the waiting time is not over: nothing happens.
            var calls = fixture.Parser.TotalCalls;
            fixture.Service.RequestRescan(folder.Id);
            await fixture.WaitIdleAsync();
            Assert.Equal(calls, fixture.Parser.TotalCalls);
        }
        finally
        {
            locker?.Dispose();
        }

        fixture.Time.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(5)));
        fixture.Service.RequestRescan(folder.Id);
        await fixture.WaitIdleAsync();

        var after = await OnlyDocumentAsync(fixture, folder, "contract.txt");
        Assert.Equal(DocumentStatus.Indexed, after.Status);
        Assert.Empty(await fixture.Store.KeywordSearchAsync("\"original wording\"", [], 10, CancellationToken.None));
        Assert.Single(await fixture.Store.KeywordSearchAsync("\"edited wording\"", [], 10, CancellationToken.None));
    }

    [Fact]
    public async Task A_new_file_that_is_locked_is_recorded_without_content_and_retried_later()
    {
        var (fixture, folder) = await CreateAsync();
        await using var _ = fixture;
        var path = fixture.WriteFile("docs", "new.txt", "fresh text", Old);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await fixture.StartAsync();
            await fixture.WaitIdleAsync();

            var locked = await OnlyDocumentAsync(fixture, folder, "new.txt");
            Assert.Equal(DocumentErrorCode.Locked, locked.ErrorCode);
            Assert.Equal(0, locked.ChunkCount);
            Assert.Equal(string.Empty, locked.Fingerprint.ContentHash);
        }

        fixture.Time.Advance(TimeSpan.FromMinutes(6));
        fixture.Service.RequestRescan(folder.Id);
        await fixture.WaitIdleAsync();

        Assert.Equal(DocumentStatus.Indexed, (await OnlyDocumentAsync(fixture, folder, "new.txt")).Status);
    }

    [Fact]
    public async Task Parser_failures_are_recorded_with_the_right_codes()
    {
        var options = IndexingFixture.FastOptions with { ParseTimeout = TimeSpan.FromMilliseconds(400) };
        var (fixture, folder) = await CreateAsync(options);
        await using var _ = fixture;
        fixture.WriteFile("docs", "corrupt.txt", "CORRUPT data");
        fixture.WriteFile("docs", "hang.txt", "HANG forever");
        fixture.WriteFile("docs", "todo.txt", "NOTIMPL parser");
        fixture.WriteFile("docs", "boom.txt", "BOOM");
        fixture.WriteFile("docs", "fine.txt", "perfectly fine");

        await fixture.StartAsync();
        await fixture.WaitIdleAsync();

        var corrupt = await OnlyDocumentAsync(fixture, folder, "corrupt.txt");
        Assert.Equal((DocumentStatus.Failed, DocumentErrorCode.Corrupted), (corrupt.Status, corrupt.ErrorCode));
        var hang = await OnlyDocumentAsync(fixture, folder, "hang.txt");
        Assert.Equal((DocumentStatus.Failed, DocumentErrorCode.Timeout), (hang.Status, hang.ErrorCode));
        var todo = await OnlyDocumentAsync(fixture, folder, "todo.txt");
        Assert.Equal((DocumentStatus.Skipped, DocumentErrorCode.Unsupported), (todo.Status, todo.ErrorCode));
        var boom = await OnlyDocumentAsync(fixture, folder, "boom.txt");
        Assert.Equal((DocumentStatus.Failed, DocumentErrorCode.Unknown), (boom.Status, boom.ErrorCode));
        Assert.Equal(DocumentStatus.Indexed, (await OnlyDocumentAsync(fixture, folder, "fine.txt")).Status);

        // The log names the exception type, never the content.
        Assert.Contains(fixture.Logger.Entries, e => e.Message.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Logger.Entries, e => e.Message.Contains("HANG forever", StringComparison.Ordinal) || e.Message.Contains("CORRUPT data", StringComparison.Ordinal));

        // Failures are not retried on every scan ...
        var calls = fixture.Parser.TotalCalls;
        fixture.Service.RequestRescan(folder.Id);
        await fixture.WaitIdleAsync();
        Assert.Equal(calls, fixture.Parser.TotalCalls);

        // ... but the user can ask for a retry.
        fixture.Service.RequestRetry(null);
        await IndexingFixture.WaitUntilAsync(() => fixture.Parser.TotalCalls > calls, what: "retry parsed again");
        await fixture.WaitIdleAsync();
        Assert.True(fixture.Parser.TotalCalls >= calls + 3);
    }

    [Fact]
    public async Task Retry_of_one_document_reads_it_again_after_the_cause_is_gone()
    {
        var (fixture, folder) = await CreateAsync();
        await using var _ = fixture;
        fixture.WriteFile("docs", "a.txt", "first file");
        fixture.WriteFile("docs", "b.txt", "second file");
        fixture.Parser.ForceCorrupt = true;
        await fixture.StartAsync();
        await fixture.WaitIdleAsync();
        var failed = (await fixture.DocumentsAsync(folder.Id)).Where(d => d.Status == DocumentStatus.Failed).ToList();
        Assert.Equal(2, failed.Count);

        fixture.Parser.ForceCorrupt = false;
        fixture.Service.RequestRetry(failed[0].Id);
        await IndexingFixture.WaitUntilAsync(
            async () => (await fixture.DocumentsAsync(folder.Id)).Count(d => d.Status == DocumentStatus.Indexed) == 1,
            what: "one document recovered");
        await fixture.WaitIdleAsync();

        var documents = await fixture.DocumentsAsync(folder.Id);
        Assert.Equal(DocumentStatus.Indexed, documents.Single(d => d.Path == failed[0].Path).Status);
        Assert.Equal(DocumentStatus.Failed, documents.Single(d => d.Path == failed[1].Path).Status);
    }

    [Fact]
    public async Task An_embedded_file_adds_its_content_after_the_container_with_a_prefixed_table_key()
    {
        var (fixture, folder) = await CreateAsync();
        await using var _ = fixture;
        var path = fixture.WriteFile("docs", "報告.docx", "EMBED main text");
        await fixture.StartAsync();
        await fixture.WaitIdleAsync();

        var document = (await fixture.DocumentsAsync(folder.Id)).Single();
        Assert.Equal(DocumentStatus.Indexed, document.Status);
        var ids = await fixture.StoreFx.ChunkIdsAsync(path);
        var chunks = await fixture.Store.GetChunksAsync(ids, CancellationToken.None);
        Assert.Equal(3, chunks.Count);

        var ordered = ids.Select(id => chunks.Single(c => c.ChunkId == id)).ToList();
        Assert.Null(ordered[0].Location.EmbeddedPath);
        Assert.Contains("main text", ordered[0].Text, StringComparison.Ordinal);
        Assert.Equal(["內嵌.xlsx"], ordered[1].Location.EmbeddedPath);
        Assert.Equal(["內嵌.xlsx"], ordered[2].Location.EmbeddedPath);
        Assert.Equal(SectionKind.TableSummary, ordered[2].Kind);

        Assert.NotNull(ordered[2].TableId);
        var table = await fixture.Store.GetExcelTableAsync(ordered[2].TableId!, CancellationToken.None);
        Assert.NotNull(table);
        Assert.Equal("內嵌.xlsx#Sheet1!A1:B2", table.Table.TableKey);
        Assert.Equal(1, fixture.Parser.CallsFor("內嵌.xlsx"));
    }

    [Fact]
    public async Task Embedded_files_are_followed_three_levels_deep_and_no_further()
    {
        var (fixture, folder) = await CreateAsync();
        await using var _ = fixture;
        var path = fixture.WriteFile("docs", "deep.docx", "NEST:0");
        await fixture.StartAsync();
        await fixture.WaitIdleAsync();

        Assert.Equal(1 + 3, fixture.Parser.TotalCalls);
        var ids = await fixture.StoreFx.ChunkIdsAsync(path);
        var chunks = await fixture.Store.GetChunksAsync(ids, CancellationToken.None);
        var depths = ids.Select(id => chunks.Single(c => c.ChunkId == id).Location.EmbeddedPath?.Count ?? 0).ToList();
        Assert.Equal([0, 1, 2, 3], depths);
        var last = chunks.Single(c => c.ChunkId == ids[^1]);
        Assert.Equal(["e.docx", "e.docx", "e.docx"], last.Location.EmbeddedPath);
        Assert.Single(await fixture.DocumentsAsync(folder.Id));
    }

    [Fact]
    public async Task Embedded_files_beyond_the_size_budget_are_ignored_but_the_file_is_still_read()
    {
        var options = IndexingFixture.FastOptions with { MaxEmbeddedBytes = 1000 };
        var (fixture, folder) = await CreateAsync(options);
        await using var _ = fixture;
        fixture.WriteFile("docs", "heavy.docx", "BIG attachment");
        await fixture.StartAsync();
        await fixture.WaitIdleAsync();

        var document = (await fixture.DocumentsAsync(folder.Id)).Single();
        Assert.Equal(DocumentStatus.Indexed, document.Status);
        Assert.Equal(0, fixture.Parser.CallsFor("big.xlsx"));
    }

    [Fact]
    public async Task Files_over_the_size_limit_are_listed_but_never_read_until_the_limit_is_raised()
    {
        var (fixture, folder) = await CreateAsync();
        await using var _ = fixture;
        await fixture.Settings.SaveAsync(fixture.Settings.Current with { MaxFileSizeMb = 1 }, CancellationToken.None);
        fixture.WriteFile("docs", "huge.txt", new string('a', 1_200_000));
        fixture.WriteFile("docs", "small.txt", "small");
        await fixture.StartAsync();
        await fixture.WaitIdleAsync();

        var huge = await OnlyDocumentAsync(fixture, folder, "huge.txt");
        Assert.Equal((DocumentStatus.Skipped, DocumentErrorCode.TooLarge), (huge.Status, huge.ErrorCode));
        Assert.Equal(0, fixture.Parser.CallsFor("huge.txt"));
        Assert.Equal(1, fixture.Parser.CallsFor("small.txt"));

        // Changing the limit triggers a reconciliation on its own.
        await fixture.Settings.SaveAsync(fixture.Settings.Current with { MaxFileSizeMb = 5 }, CancellationToken.None);
        await IndexingFixture.WaitUntilAsync(
            async () => (await OnlyDocumentAsync(fixture, folder, "huge.txt")).Status == DocumentStatus.Indexed,
            what: "huge file read after the limit was raised");
        Assert.Equal(1, fixture.Parser.CallsFor("huge.txt"));

        await fixture.Settings.SaveAsync(fixture.Settings.Current with { MaxFileSizeMb = 1 }, CancellationToken.None);
        await IndexingFixture.WaitUntilAsync(
            async () => (await OnlyDocumentAsync(fixture, folder, "huge.txt")).Status == DocumentStatus.Skipped,
            what: "huge file skipped again");
        await fixture.WaitIdleAsync();
        Assert.Equal(0, (await OnlyDocumentAsync(fixture, folder, "huge.txt")).ChunkCount);
    }

    [Fact]
    public async Task Built_in_exclusions_excluded_sub_folders_and_user_exclusions_are_never_read()
    {
        var (fixture, folder) = await CreateAsync();
        await using var _ = fixture;
        fixture.WriteFile("docs", "keep.txt", "keep me");
        fixture.WriteFile("docs", "~$lock.docx", "Office temporary file");
        fixture.WriteFile("docs", Path.Combine(".git", "config.txt"), "inside git");
        fixture.WriteFile("docs", Path.Combine("node_modules", "x.txt"), "inside node_modules");
        fixture.WriteFile("docs", "program.exe", "binary");
        fixture.WriteFile("docs", "image.png", "not a supported type");
        fixture.WriteFile("docs", Path.Combine("skip", "inside.txt"), "excluded sub-folder");
        fixture.WriteFile("docs", Path.Combine("skip", "deeper", "inside.txt"), "excluded sub-folder, deeper");
        fixture.WriteFile("docs", Path.Combine("never", "x.txt"), "excluded by the user as a folder");
        fixture.WriteFile("docs", "private.txt", "excluded by the user as a file");
        var hidden = fixture.WriteFile("docs", OperatingSystem.IsWindows() ? "hidden.txt" : ".hidden.txt", "hidden file");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        }

        await fixture.Store.SetFolderExclusionsAsync(folder.Id, ["skip"], CancellationToken.None);
        await fixture.Store.AddExclusionAsync(Path.Combine(fixture.FolderPath("docs"), "never"), isFolder: true, CancellationToken.None);
        await fixture.Store.AddExclusionAsync(Path.Combine(fixture.FolderPath("docs"), "private.txt"), isFolder: false, CancellationToken.None);

        await fixture.StartAsync();
        await fixture.WaitIdleAsync();

        var names = (await fixture.DocumentsAsync(folder.Id)).Select(d => Path.GetFileName(d.Path)).ToArray();
        Assert.Equal(["keep.txt"], names);
        Assert.Equal(1, fixture.Parser.TotalCalls);
    }

    [Fact]
    public async Task Disabling_a_category_removes_its_data_without_a_mass_deletion_question()
    {
        var (fixture, folder) = await CreateAsync();
        await using var _ = fixture;
        for (var i = 0; i < 25; i++)
        {
            fixture.WriteFile("docs", $"sheet{i:00}.csv", $"a,b,{i}");
        }

        for (var i = 0; i < 3; i++)
        {
            fixture.WriteFile("docs", $"note{i}.txt", $"note {i}");
        }

        await fixture.StartAsync();
        await fixture.WaitIdleAsync();
        Assert.Equal(28, (await fixture.DocumentsAsync(folder.Id)).Count);

        var categories = fixture.Settings.Current.EnabledCategories.Where(c => c != FileCategory.Spreadsheets).ToList();
        await fixture.Settings.SaveAsync(fixture.Settings.Current with { EnabledCategories = categories }, CancellationToken.None);
        await IndexingFixture.WaitUntilAsync(
            async () => (await fixture.DocumentsAsync(folder.Id)).Count == 3,
            what: "spreadsheets removed");

        Assert.Empty(fixture.MassDeletions);
        Assert.Equal(FolderState.Active, (await fixture.FolderAsync(folder.Id)).State);

        // Turning it back on reads the files again.
        await fixture.Settings.SaveAsync(fixture.Settings.Current with { EnabledCategories = [.. categories, FileCategory.Spreadsheets] }, CancellationToken.None);
        await IndexingFixture.WaitUntilAsync(
            async () => (await fixture.DocumentsAsync(folder.Id)).Count == 28,
            what: "spreadsheets read again");
    }

    [Fact]
    public async Task Vectors_are_written_when_the_model_is_available()
    {
        var (fixture, folder) = await CreateAsync();
        await using var _ = fixture;
        fixture.WriteFile("docs", "a.txt", "text with a vector");
        await fixture.StartAsync();
        await fixture.WaitIdleAsync();

        Assert.Equal("fake-model", await fixture.Store.GetMetaAsync("embedding_model", CancellationToken.None));
        Assert.Equal(1, (await fixture.DocumentsAsync(folder.Id)).Single().ChunkCount);
        var vectors = await StoreFixture.ToListAsync(fixture.Store.ReadVectorsAsync("fake-model", CancellationToken.None));
        Assert.Single(vectors);
        Assert.Empty(await StoreFixture.ToListAsync(fixture.Store.ReadChunksMissingVectorAsync("fake-model", CancellationToken.None)));
    }

    [Fact]
    public async Task Chunks_written_without_a_model_get_their_vectors_in_the_background_later()
    {
        var (fixture, folder) = await CreateAsync();
        await using var _ = fixture;
        fixture.Embedding.Available = false;
        for (var i = 0; i < 5; i++)
        {
            fixture.WriteFile("docs", $"f{i}.txt", $"text number {i}");
        }

        await fixture.StartAsync();
        await fixture.WaitIdleAsync();

        Assert.Equal(5, (await fixture.DocumentsAsync(folder.Id)).Count);
        Assert.Equal(0, fixture.Embedding.Calls);
        Assert.Null(await fixture.Store.GetMetaAsync("embedding_model", CancellationToken.None));
        Assert.Equal(5, (await StoreFixture.ToListAsync(fixture.Store.ReadChunksMissingVectorAsync("fake-model", CancellationToken.None))).Count);

        // The model shows up (the user downloaded it): the next idle moment fills the gaps without parsing anything.
        fixture.Embedding.Available = true;
        var parsed = fixture.Parser.TotalCalls;
        fixture.Service.RequestRescan(null);
        await IndexingFixture.WaitUntilAsync(
            async () => (await StoreFixture.ToListAsync(fixture.Store.ReadChunksMissingVectorAsync("fake-model", CancellationToken.None))).Count == 0,
            what: "vectors filled in");
        await fixture.WaitIdleAsync();

        Assert.Equal(parsed, fixture.Parser.TotalCalls);
        Assert.Equal("fake-model", await fixture.Store.GetMetaAsync("embedding_model", CancellationToken.None));
        Assert.Equal(5, (await StoreFixture.ToListAsync(fixture.Store.ReadVectorsAsync("fake-model", CancellationToken.None))).Count);
    }

    [Fact]
    public async Task A_changed_model_is_recorded_and_all_chunks_get_new_vectors()
    {
        var (fixture, folder) = await CreateAsync();
        await using var _ = fixture;
        fixture.WriteFile("docs", "a.txt", "some text");
        await fixture.Store.InitializeAsync(CancellationToken.None);
        await fixture.Store.SetMetaAsync("embedding_model", "older-model", CancellationToken.None);
        await fixture.StartAsync();
        await fixture.WaitIdleAsync();

        Assert.Equal("fake-model", await fixture.Store.GetMetaAsync("embedding_model", CancellationToken.None));
        Assert.Single(await fixture.DocumentsAsync(folder.Id));
        Assert.Empty(await StoreFixture.ToListAsync(fixture.Store.ReadChunksMissingVectorAsync("fake-model", CancellationToken.None)));
    }
}
