using System.Diagnostics;
using System.Text;
using Contexo.Core.Abstractions;
using Xunit.Abstractions;

namespace Contexo.Core.Tests.EndToEnd;

/// <summary>
/// Changing, deleting and renaming real files in an indexed folder, and a folder that disappears and comes back.
/// The searches use literal words so that they also work in keyword-only mode (no model in CI).
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class FolderSyncEndToEndTests(CorpusFixture corpus, ITestOutputHelper output) : IClassFixture<CorpusFixture>
{
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan WatcherTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Modified_deleted_and_renamed_files_are_reflected()
    {
        var modified = Path.Combine(corpus.CorpusDirectory, "會議室使用規則.txt");
        var deleted = Path.Combine(corpus.CorpusDirectory, "公告_尾牙.html");
        var renamedFrom = Path.Combine(corpus.CorpusDirectory, "新人手冊.md");
        var renamedTo = Path.Combine(corpus.CorpusDirectory, "員工入門手冊.md");

        var before = await corpus.Store.GetDocumentsAsync(corpus.FolderId, CancellationToken.None);
        var renamedBefore = before.Single(d => d.Path == renamedFrom);
        Assert.Contains("2 小時", (await corpus.SearchAsync("會議室 預約")).Hits.First(h => h.FileName == "會議室使用規則.txt").Text);
        Assert.Contains((await corpus.SearchAsync("尾牙活動")).Hits, h => h.FileName == "公告_尾牙.html");

        // Edit the Big5 text file (now saved as UTF-8 with a different time), delete one file, rename another one.
        File.WriteAllText(modified, "會議室使用規則\r\n\r\n1. 會議室採線上預約，單次最長可預約 4 小時。\r\n2. 使用後請恢復桌椅。\r\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(modified, DateTime.UtcNow);
        File.Delete(deleted);
        File.Move(renamedFrom, renamedTo);

        async Task<bool> ReflectsTheChangesAsync()
        {
            var documents = await corpus.Store.GetDocumentsAsync(corpus.FolderId, CancellationToken.None);
            return documents.Count == 39
                && documents.All(d => d.Path != deleted && d.Path != renamedFrom)
                && documents.Any(d => d.Path == renamedTo)
                && corpus.Indexing.Current.State == IndexingState.Idle
                && (await corpus.SearchAsync("會議室 預約")).Hits.Any(h => h.FileName == "會議室使用規則.txt" && h.Text.Contains("4 小時"));
        }

        // First give the folder watcher a chance on its own; if it did not notice in time, ask for a scan (and say so in the output).
        var clock = Stopwatch.StartNew();
        var noticedByWatcher = true;
        try
        {
            await CorpusFixture.WaitUntilAsync(ReflectsTheChangesAsync, WatcherTimeout, "the folder watcher to pick up the changes");
        }
        catch (TimeoutException)
        {
            noticedByWatcher = false;
            corpus.Indexing.RequestRescan(corpus.FolderId);
            await CorpusFixture.WaitUntilAsync(ReflectsTheChangesAsync, SyncTimeout, "the folder to match the changes");
        }

        output.WriteLine($"Changes reflected after {clock.Elapsed.TotalSeconds:0.0} s ({(noticedByWatcher ? "noticed by the folder watcher" : "needed an explicit scan")})");

        // Modified: the new text is found and the old one is gone.
        var meetingRoom = (await corpus.SearchAsync("會議室 預約")).Hits.Where(h => h.FileName == "會議室使用規則.txt").ToList();
        Assert.All(meetingRoom, h => Assert.DoesNotContain("2 小時", h.Text));
        Assert.Contains(meetingRoom, h => h.Text.Contains("4 小時"));

        // Deleted: nothing of it is left in the database.
        Assert.DoesNotContain((await corpus.SearchAsync("尾牙活動")).Hits, h => h.FileName == "公告_尾牙.html");
        Assert.Empty(corpus.Sql("SELECT id FROM documents WHERE path LIKE '%公告_尾牙%'"));

        // Renamed: the same document moved (same row, same chunks, no re-reading), and it is found under the new name.
        var after = await corpus.Store.GetDocumentsAsync(corpus.FolderId, CancellationToken.None);
        var renamedAfter = after.Single(d => d.Path == renamedTo);
        output.WriteLine($"rename: id {renamedBefore.Id} -> {renamedAfter.Id}, chunks {renamedBefore.ChunkCount} -> {renamedAfter.ChunkCount}");
        Assert.Equal(renamedBefore.Id, renamedAfter.Id);
        Assert.Equal(renamedBefore.ChunkCount, renamedAfter.ChunkCount);
        Assert.Equal(renamedBefore.Fingerprint.ContentHash, renamedAfter.Fingerprint.ContentHash);
        Assert.Contains((await corpus.SearchAsync("訪客 Wi-Fi 密碼")).Hits, h => h.FileName == "員工入門手冊.md");

        // The user's other files were never touched (safety rule: Contexo only changes its own database).
        Assert.True(File.Exists(Path.Combine(corpus.CorpusDirectory, "採購規範.docx")));
        Assert.Equal(39, Directory.GetFiles(corpus.CorpusDirectory).Length);
    }

    [SkippableFact]
    public async Task A_folder_that_cannot_be_read_keeps_its_data_and_recovers_when_it_returns()
    {
        // Windows refuses to rename a folder that a FileSystemWatcher is watching, so the "unplugged drive" has to be checked by hand there
        // (tests/manual/CHECKLIST.md). Everywhere else the whole folder is moved away and back.
        Skip.If(OperatingSystem.IsWindows(), "A watched folder cannot be renamed on Windows; see tests/manual/CHECKLIST.md");

        var parked = corpus.CorpusDirectory + "-parked";
        var before = await corpus.Store.GetStatisticsAsync(CancellationToken.None);

        // Like pulling out an external drive: the whole folder is gone.
        Directory.Move(corpus.CorpusDirectory, parked);
        try
        {
            corpus.Indexing.RequestRescan(corpus.FolderId);
            await CorpusFixture.WaitUntilAsync(async () => (await FolderAsync()).State == FolderState.Unavailable, SyncTimeout, "the folder to become unavailable");

            var during = await corpus.Store.GetStatisticsAsync(CancellationToken.None);
            Assert.Equal(before.DocumentCount, during.DocumentCount);
            Assert.Equal(before.ChunkCount, during.ChunkCount);
            Assert.Contains((await corpus.SearchAsync("停車證怎麼換")).Hits, h => h.FileName == "舊版公告_停車證換發.txt");
        }
        finally
        {
            Directory.Move(parked, corpus.CorpusDirectory);
        }

        corpus.Indexing.RequestRescan(corpus.FolderId);
        await CorpusFixture.WaitUntilAsync(async () => (await FolderAsync()).State == FolderState.Active && corpus.Indexing.Current.State == IndexingState.Idle, SyncTimeout, "the folder to become active again");

        // Nothing was rebuilt: same documents and chunks, and the first indexing run's data are still there.
        var after = await corpus.Store.GetStatisticsAsync(CancellationToken.None);
        Assert.Equal(before.DocumentCount, after.DocumentCount);
        Assert.Equal(before.ChunkCount, after.ChunkCount);
    }

    private async Task<WatchedFolder> FolderAsync() =>
        (await corpus.Store.GetFoldersAsync(CancellationToken.None)).Single(f => f.Id == corpus.FolderId);
}
