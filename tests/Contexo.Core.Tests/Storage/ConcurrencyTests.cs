using System.Diagnostics;

namespace Contexo.Core.Tests.Storage;

public sealed class ConcurrencyTests
{
    [Fact]
    public async Task One_instance_writing_while_another_reads_and_searches_never_fails()
    {
        using var f = await StoreFixture.CreateAsync();
        var writer = f.Store;
        var reader = f.CreateStore();
        await reader.InitializeAsync(CancellationToken.None);
        var folder = await f.AddFolderAsync("docs");
        var duration = TimeSpan.FromSeconds(3);
        var stop = Stopwatch.StartNew();

        var writes = Task.Run(async () =>
        {
            var n = 0;
            while (stop.Elapsed < duration)
            {
                var path = f.PathOf("docs", $"{n % 5}.txt");
                var chunks = Enumerable.Range(0, 20).Select(i => StoreFixture.WithVector(i, $"監視系統建置報價 round {n} chunk {i}", i, n, 0.5f)).ToList();
                await writer.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, path, chunks), CancellationToken.None);
                n++;
            }

            return n;
        });

        var reads = Task.Run(async () =>
        {
            var n = 0;
            while (stop.Elapsed < duration)
            {
                var vectors = await StoreFixture.ToListAsync(reader.ReadVectorsAsync(StoreFixture.Model, CancellationToken.None));
                Assert.All(vectors, v => Assert.Equal(3, v.Vector.Length));
                var hits = await reader.KeywordSearchAsync("\"系統建置\"", ["報價"], 20, CancellationToken.None);
                _ = await reader.GetChunksAsync(hits.Select(h => h.ChunkId).ToList(), CancellationToken.None);
                _ = await reader.GetIndexVersionAsync(CancellationToken.None);
                n++;
            }

            return n;
        });

        var counts = await Task.WhenAll(writes, reads);

        Assert.True(counts[0] > 0);
        Assert.True(counts[1] > 0);
    }

    [Fact]
    public async Task Two_writers_wait_for_each_other_instead_of_failing()
    {
        using var f = await StoreFixture.CreateAsync();
        var other = f.CreateStore();
        var folder = await f.AddFolderAsync("docs");

        async Task Work(Contexo.Core.Storage.SqliteKnowledgeStore store, string prefix)
        {
            for (var i = 0; i < 25; i++)
            {
                await store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, f.PathOf("docs", $"{prefix}{i}.txt"), [StoreFixture.WithVector(0, $"{prefix} {i}", 1f)]), CancellationToken.None);
            }
        }

        await Task.WhenAll(Work(f.Store, "a"), Work(other, "b"));

        Assert.Equal(50, (await f.Store.GetDocumentsAsync(folder.Id, CancellationToken.None)).Count);
        Assert.Equal(50L, await f.Store.GetIndexVersionAsync(CancellationToken.None));
    }
}
