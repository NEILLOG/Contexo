using Contexo.Core.Abstractions;

namespace Contexo.Core.Tests.Storage;

public sealed class DocumentTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Replacing_a_document_twice_keeps_only_the_second_chunks_and_vectors()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var path = f.PathOf("docs", "a.docx");

        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, path, [StoreFixture.WithVector(0, "first version", 1f), StoreFixture.WithVector(1, "first extra", 2f)], hash: "one"), Ct);
        var versionAfterFirst = await f.Store.GetIndexVersionAsync(Ct);
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, path, [StoreFixture.WithVector(0, "second version", 9f)], hash: "two"), Ct);

        var document = await f.Store.GetDocumentByPathAsync(path, Ct);
        Assert.NotNull(document);
        Assert.Equal(1, document.ChunkCount);
        Assert.Equal(DocumentStatus.Indexed, document.Status);
        Assert.Equal("two", document.Fingerprint.ContentHash);
        Assert.Single(await f.Store.GetDocumentsAsync(folder.Id, Ct));
        Assert.Equal(1L, await f.ScalarAsync<long>("SELECT COUNT(*) FROM chunks"));
        Assert.Equal(1L, await f.ScalarAsync<long>("SELECT COUNT(*) FROM embeddings"));
        var vectors = await StoreFixture.ToListAsync(f.Store.ReadVectorsAsync(StoreFixture.Model, Ct));
        Assert.Equal([9f], Assert.Single(vectors).Vector);
        Assert.Empty(await f.Store.KeywordSearchAsync("\"first version\"", [], 10, Ct));
        Assert.Single(await f.Store.KeywordSearchAsync("\"second version\"", [], 10, Ct));
        Assert.Equal(1, versionAfterFirst);
        Assert.Equal(2, await f.Store.GetIndexVersionAsync(Ct));
    }

    [Fact]
    public async Task Replacing_a_document_with_vectors_requires_a_model_id()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var write = StoreFixture.Write(folder.Id, f.PathOf("docs", "a.txt"), [StoreFixture.WithVector(0, "text", 1f)]) with { ModelId = null };

        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.ReplaceDocumentAsync(write, Ct));
        Assert.Equal(0L, await f.ScalarAsync<long>("SELECT COUNT(*) FROM documents"));
    }

    [Fact]
    public async Task A_failed_replace_rolls_back_everything()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var path = f.PathOf("docs", "a.txt");
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, path, [StoreFixture.NoVector(0, "keep me")]), Ct);

        // Unknown folder id violates the foreign key after the old rows were already deleted inside the transaction.
        await Assert.ThrowsAnyAsync<Exception>(() => f.Store.ReplaceDocumentAsync(StoreFixture.Write(9999, path, [StoreFixture.NoVector(0, "new")]), Ct));

        Assert.Single(await f.Store.KeywordSearchAsync("\"keep me\"", [], 10, Ct));
        Assert.NotNull(await f.Store.GetDocumentByPathAsync(path, Ct));
    }

    [Fact]
    public async Task Table_keys_link_chunks_to_tables_and_survive_round_trip()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var path = f.PathOf("docs", "quote.xlsx");
        var table = StoreFixture.MakeTable();
        var location = new SourceLocation { Sheet = "Sheet1", CellRange = "A1:C300", HeadingPath = ["第一章", "報價"], Title = "報價表", EmbeddedPath = ["內嵌.xlsx"] };
        var chunks = new[]
        {
            new ChunkWrite(StoreFixture.MakeChunk(0, "普通段落"), null),
            new ChunkWrite(StoreFixture.MakeChunk(1, table.Description, table.TableKey, SectionKind.TableSummary, location), null),
        };

        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, path, chunks, [table]), Ct);

        var ids = await f.ChunkIdsAsync(path);
        var details = await f.Store.GetChunksAsync([ids[1], ids[0]], Ct);
        Assert.Equal([ids[1], ids[0]], details.Select(d => d.ChunkId));
        var summary = details[0];
        Assert.Equal(SectionKind.TableSummary, summary.Kind);
        Assert.Equal(table.Description, summary.Text);
        Assert.Equal(path, summary.FilePath, ignoreCase: true);
        Assert.NotNull(summary.TableId);
        Assert.StartsWith("t", summary.TableId);
        Assert.Null(details[1].TableId);
        Assert.Equal("Sheet1", summary.Location.Sheet);
        Assert.Equal("A1:C300", summary.Location.CellRange);
        Assert.Equal(["第一章", "報價"], summary.Location.HeadingPath);
        Assert.Equal(["內嵌.xlsx"], summary.Location.EmbeddedPath);
        Assert.Null(summary.Location.Page);

        var record = await f.Store.GetExcelTableAsync(summary.TableId, Ct);
        Assert.NotNull(record);
        Assert.Equal(summary.TableId, record.TableId);
        Assert.Equal(summary.DocumentId, record.DocumentId);
        Assert.Equal(path, record.FilePath, ignoreCase: true);
        Assert.Equal(table.TableKey, record.Table.TableKey);
        Assert.Equal(table.Columns, record.Table.Columns);
        Assert.Equal(table.DataRowCount, record.Table.DataRowCount);
        Assert.Equal(table.HeaderRowCount, record.Table.HeaderRowCount);
        Assert.Equal(table.Description, record.Table.Description);
        Assert.Equal(table.SampleRows.Count, record.Table.SampleRows.Count);
        Assert.Equal(table.SampleRows[1], record.Table.SampleRows[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("t")]
    [InlineData("t-1")]
    [InlineData("tabc")]
    [InlineData("x5")]
    [InlineData("t999999")]
    public async Task Bad_or_unknown_table_ids_return_null(string tableId)
    {
        using var f = await StoreFixture.CreateAsync();

        Assert.Null(await f.Store.GetExcelTableAsync(tableId, Ct));
    }

    [Fact]
    public async Task GetChunks_skips_unknown_ids_and_handles_large_lists()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var path = f.PathOf("docs", "big.txt");
        var chunks = Enumerable.Range(0, 1200).Select(i => StoreFixture.NoVector(i, $"chunk number {i}")).ToList();
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, path, chunks), Ct);
        var ids = await f.ChunkIdsAsync(path);

        var requested = ids.AsEnumerable().Reverse().Append(123456789L).ToList();
        var details = await f.Store.GetChunksAsync(requested, Ct);

        Assert.Equal(ids.AsEnumerable().Reverse(), details.Select(d => d.ChunkId));
        Assert.Empty(await f.Store.GetChunksAsync([], Ct));
    }

    [Fact]
    public async Task Marking_with_keep_preserves_chunks_and_updates_the_fingerprint()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var path = f.PathOf("docs", "a.txt");
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, path, [StoreFixture.WithVector(0, "searchable", 1f)], hash: "old"), Ct);
        var version = await f.Store.GetIndexVersionAsync(Ct);
        var fingerprint = StoreFixture.Fingerprint("new", 77, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var retry = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

        await f.Store.MarkDocumentAsync(folder.Id, path, fingerprint, DocumentStatus.Failed, DocumentErrorCode.Locked, "鎖住了", retry, keepExistingChunks: true, Ct);

        var document = await f.Store.GetDocumentByPathAsync(path, Ct);
        Assert.NotNull(document);
        Assert.Equal(DocumentStatus.Failed, document.Status);
        Assert.Equal(DocumentErrorCode.Locked, document.ErrorCode);
        Assert.Equal("鎖住了", document.ErrorMessage);
        Assert.Equal("new", document.Fingerprint.ContentHash);
        Assert.Equal(77, document.Fingerprint.SizeBytes);
        Assert.Equal(fingerprint.LastWriteUtc, document.Fingerprint.LastWriteUtc);
        Assert.Equal(retry, document.NextRetryAt);
        Assert.Equal(1, document.ChunkCount);
        Assert.Single(await f.Store.KeywordSearchAsync("\"searchable\"", [], 10, Ct));
        Assert.Single(await StoreFixture.ToListAsync(f.Store.ReadVectorsAsync(StoreFixture.Model, Ct)));
        Assert.Equal(version, await f.Store.GetIndexVersionAsync(Ct));
        Assert.Single(await f.Store.GetFailedDocumentsAsync(10, Ct));
    }

    [Fact]
    public async Task Marking_without_keep_removes_content_and_leaves_a_status_row()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var path = f.PathOf("docs", "a.xlsx");
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, path, [StoreFixture.WithVector(0, "searchable", 1f)], [StoreFixture.MakeTable()]), Ct);
        var version = await f.Store.GetIndexVersionAsync(Ct);

        await f.Store.MarkDocumentAsync(folder.Id, path, StoreFixture.Fingerprint("x"), DocumentStatus.Failed, DocumentErrorCode.Corrupted, "壞掉", null, keepExistingChunks: false, Ct);

        var document = await f.Store.GetDocumentByPathAsync(path, Ct);
        Assert.NotNull(document);
        Assert.Equal(0, document.ChunkCount);
        Assert.Equal(DocumentErrorCode.Corrupted, document.ErrorCode);
        Assert.Equal(0L, await f.ScalarAsync<long>("SELECT COUNT(*) FROM chunks"));
        Assert.Equal(0L, await f.ScalarAsync<long>("SELECT COUNT(*) FROM embeddings"));
        Assert.Equal(0L, await f.ScalarAsync<long>("SELECT COUNT(*) FROM excel_tables"));
        Assert.Empty(await f.Store.KeywordSearchAsync("\"searchable\"", [], 10, Ct));
        Assert.Equal(version + 1, await f.Store.GetIndexVersionAsync(Ct));

        // Marking again has nothing left to delete, so the version stays put.
        await f.Store.MarkDocumentAsync(folder.Id, path, StoreFixture.Fingerprint("y"), DocumentStatus.Skipped, DocumentErrorCode.TooLarge, null, null, keepExistingChunks: false, Ct);
        Assert.Equal(version + 1, await f.Store.GetIndexVersionAsync(Ct));
    }

    [Fact]
    public async Task Marking_an_unknown_path_creates_a_row_with_no_chunks()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var path = f.PathOf("docs", "new.pdf");

        await f.Store.MarkDocumentAsync(folder.Id, path, StoreFixture.Fingerprint(), DocumentStatus.Skipped, DocumentErrorCode.TooLarge, null, null, keepExistingChunks: true, Ct);

        var document = await f.Store.GetDocumentByPathAsync(path, Ct);
        Assert.NotNull(document);
        Assert.Equal(0, document.ChunkCount);
        Assert.Equal(DocumentStatus.Skipped, document.Status);
    }

    [Fact]
    public async Task Moving_a_document_changes_only_the_path()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var oldPath = f.PathOf("docs", "old.txt");
        var newPath = f.PathOf("docs", "sub", "new.txt");
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, oldPath, [StoreFixture.WithVector(0, "stay put", 1f)]), Ct);
        var document = await f.Store.GetDocumentByPathAsync(oldPath, Ct);

        await f.Store.MoveDocumentAsync(document!.Id, newPath, Ct);

        Assert.Null(await f.Store.GetDocumentByPathAsync(oldPath, Ct));
        var moved = await f.Store.GetDocumentByPathAsync(newPath, Ct);
        Assert.Equal(document.Id, moved!.Id);
        Assert.Equal(1, moved.ChunkCount);
        var ids = await f.ChunkIdsAsync(newPath);
        var detail = Assert.Single(await f.Store.GetChunksAsync(ids, Ct));
        Assert.Equal(newPath, detail.FilePath, ignoreCase: true);
        Assert.Single(await StoreFixture.ToListAsync(f.Store.ReadVectorsAsync(StoreFixture.Model, Ct)));
    }

    [Fact]
    public async Task Moving_onto_an_occupied_path_removes_the_other_document()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var a = f.PathOf("docs", "a.txt");
        var b = f.PathOf("docs", "b.txt");
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, a, [StoreFixture.NoVector(0, "from a")]), Ct);
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, b, [StoreFixture.NoVector(0, "from b")]), Ct);
        var docA = await f.Store.GetDocumentByPathAsync(a, Ct);

        await f.Store.MoveDocumentAsync(docA!.Id, b, Ct);

        Assert.Single(await f.Store.GetDocumentsAsync(folder.Id, Ct));
        Assert.Single(await f.Store.KeywordSearchAsync("\"from a\"", [], 10, Ct));
        Assert.Empty(await f.Store.KeywordSearchAsync("\"from b\"", [], 10, Ct));
        Assert.Equal(docA.Id, (await f.Store.GetDocumentByPathAsync(b, Ct))!.Id);
    }

    [Fact]
    public async Task Deleting_a_document_removes_everything_and_bumps_the_version()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var path = f.PathOf("docs", "a.txt");
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, path, [StoreFixture.WithVector(0, "going away", 1f)]), Ct);
        var document = await f.Store.GetDocumentByPathAsync(path, Ct);
        var version = await f.Store.GetIndexVersionAsync(Ct);

        await f.Store.DeleteDocumentAsync(document!.Id, Ct);

        Assert.Null(await f.Store.GetDocumentByPathAsync(path, Ct));
        Assert.Empty(await f.Store.KeywordSearchAsync("\"going away\"", [], 10, Ct));
        Assert.Equal(0L, await f.ScalarAsync<long>("SELECT COUNT(*) FROM embeddings"));
        Assert.Equal(version + 1, await f.Store.GetIndexVersionAsync(Ct));
    }

    [Fact]
    public async Task Failed_documents_are_listed_newest_first_up_to_the_limit()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        for (var i = 0; i < 3; i++)
        {
            await f.Store.MarkDocumentAsync(folder.Id, f.PathOf("docs", $"{i}.txt"), StoreFixture.Fingerprint(), DocumentStatus.Failed, DocumentErrorCode.Unknown, "x", null, false, Ct);
            await Task.Delay(5);
        }

        await f.Store.MarkDocumentAsync(folder.Id, f.PathOf("docs", "ok.txt"), StoreFixture.Fingerprint(), DocumentStatus.Indexed, DocumentErrorCode.None, null, null, false, Ct);

        var failed = await f.Store.GetFailedDocumentsAsync(2, Ct);
        Assert.Equal(2, failed.Count);
        Assert.EndsWith("2.txt", failed[0].Path);
        Assert.EndsWith("1.txt", failed[1].Path);
    }
}
