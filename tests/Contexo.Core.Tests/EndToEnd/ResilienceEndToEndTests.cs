using Contexo.Core.Abstractions;
using Xunit.Abstractions;

namespace Contexo.Core.Tests.EndToEnd;

/// <summary>Broken files in a real folder: each one is reported on its own, everything else keeps working.</summary>
[Trait("Category", "EndToEnd")]
public sealed class BrokenFilesEndToEndTests(CorpusFixture corpus, ITestOutputHelper output) : IClassFixture<CorpusFixture>
{
    [Fact]
    public async Task Unreadable_files_are_marked_failed_one_by_one_and_the_good_ones_stay_searchable()
    {
        var original = await File.ReadAllBytesAsync(Path.Combine(corpus.CorpusDirectory, "採購規範.docx"));
        var workbook = await File.ReadAllBytesAsync(Path.Combine(corpus.CorpusDirectory, "客戶清單.xlsx"));
        var broken = new Dictionary<string, byte[]>
        {
            ["損毀的合約.docx"] = [.. "this is not a zip file"u8],
            ["空白檔案.pdf"] = [],
            ["截斷的客戶表.xlsx"] = workbook[..(workbook.Length / 2)],
            ["截斷的簡報.pptx"] = original[..(original.Length / 3)],
        };
        foreach (var (name, content) in broken)
        {
            await File.WriteAllBytesAsync(Path.Combine(corpus.CorpusDirectory, name), content);
        }

        corpus.Indexing.RequestRescan(corpus.FolderId);
        await CorpusFixture.WaitUntilAsync(async () =>
        {
            var documents = await corpus.Store.GetDocumentsAsync(corpus.FolderId, CancellationToken.None);
            return documents.Count == 44 && corpus.Indexing.Current.State == IndexingState.Idle;
        }, TimeSpan.FromMinutes(2), "the broken files to be processed");

        var all = await corpus.Store.GetDocumentsAsync(corpus.FolderId, CancellationToken.None);
        foreach (var name in broken.Keys)
        {
            var record = all.Single(d => Path.GetFileName(d.Path) == name);
            output.WriteLine($"{name}: {record.Status} {record.ErrorCode} \"{record.ErrorMessage}\"");
            Assert.NotEqual(DocumentStatus.Indexed, record.Status);
            Assert.Equal(0, record.ChunkCount);
            Assert.False(string.IsNullOrWhiteSpace(record.ErrorMessage), name + " has no message for the user");
            Assert.DoesNotContain("Exception", record.ErrorMessage);
            Assert.DoesNotContain(corpus.CorpusDirectory, record.ErrorMessage);
        }

        var failed = await corpus.Store.GetFailedDocumentsAsync(100, CancellationToken.None);
        Assert.Equal(broken.Count, failed.Count(f => broken.ContainsKey(Path.GetFileName(f.Path))));

        // Everything else is untouched.
        var good = all.Where(d => !broken.ContainsKey(Path.GetFileName(d.Path))).ToList();
        Assert.Equal(40, good.Count);
        Assert.All(good, d => Assert.Equal(DocumentStatus.Indexed, d.Status));
        Assert.Contains((await corpus.SearchAsync("驗收標準")).Hits, h => h.FileName == "採購規範.docx");
    }
}

/// <summary>Deleting many files at once asks the user first (task T10): a mass deletion must never silently empty the index.</summary>
[Trait("Category", "EndToEnd")]
public sealed class MassDeletionKeepEndToEndTests(CorpusFixture corpus) : IClassFixture<CorpusFixture>
{
    [Fact]
    public async Task Answering_no_keeps_every_document_and_the_folder_returns_to_active()
    {
        var before = await corpus.Store.GetStatisticsAsync(CancellationToken.None);
        var pending = await MassDeletionScenario.DeleteManyAsync(corpus);

        Assert.Equal(21, pending.MissingFileCount);
        Assert.Equal(40, pending.TotalFileCount);
        Assert.Equal(FolderState.AwaitingDeletionConfirmation, (await MassDeletionScenario.FolderAsync(corpus)).State);
        Assert.Equal(before.DocumentCount, (await corpus.Store.GetStatisticsAsync(CancellationToken.None)).DocumentCount);

        await corpus.Indexing.ResolveMassDeletionAsync(corpus.FolderId, deleteMissing: false, CancellationToken.None);

        await CorpusFixture.WaitUntilAsync(async () => (await MassDeletionScenario.FolderAsync(corpus)).State == FolderState.Active, TimeSpan.FromMinutes(1), "the folder to be active again");
        var after = await corpus.Store.GetStatisticsAsync(CancellationToken.None);
        Assert.Equal(before.DocumentCount, after.DocumentCount);
        Assert.Equal(before.ChunkCount, after.ChunkCount);
    }
}

[Trait("Category", "EndToEnd")]
public sealed class MassDeletionConfirmedEndToEndTests(CorpusFixture corpus) : IClassFixture<CorpusFixture>
{
    [Fact]
    public async Task Answering_yes_removes_only_the_missing_documents_from_the_database()
    {
        var remaining = Directory.GetFiles(corpus.CorpusDirectory).Length - 21;
        var pending = await MassDeletionScenario.DeleteManyAsync(corpus);
        Assert.Equal(21, pending.MissingFileCount);

        await corpus.Indexing.ResolveMassDeletionAsync(corpus.FolderId, deleteMissing: true, CancellationToken.None);

        await CorpusFixture.WaitUntilAsync(async () =>
            (await corpus.Store.GetDocumentsAsync(corpus.FolderId, CancellationToken.None)).Count == remaining
            && (await MassDeletionScenario.FolderAsync(corpus)).State == FolderState.Active, TimeSpan.FromMinutes(1), "the missing documents to be removed");
        Assert.Equal(remaining, Directory.GetFiles(corpus.CorpusDirectory).Length);
    }
}

internal static class MassDeletionScenario
{
    /// <summary>Deletes 21 of the 40 files (more than 30 percent and at least 20) and waits for the question.</summary>
    public static async Task<MassDeletionPending> DeleteManyAsync(CorpusFixture corpus)
    {
        var asked = new TaskCompletionSource<MassDeletionPending>(TaskCreationOptions.RunContinuationsAsynchronously);
        corpus.Indexing.MassDeletionPendingRaised += (_, e) => asked.TrySetResult(e);

        foreach (var file in Directory.GetFiles(corpus.CorpusDirectory).Order(StringComparer.Ordinal).Take(21))
        {
            File.Delete(file);
        }

        corpus.Indexing.RequestRescan(corpus.FolderId);
        return await asked.Task.WaitAsync(TimeSpan.FromMinutes(1));
    }

    public static async Task<WatchedFolder> FolderAsync(CorpusFixture corpus) =>
        (await corpus.Store.GetFoldersAsync(CancellationToken.None)).Single(f => f.Id == corpus.FolderId);
}
