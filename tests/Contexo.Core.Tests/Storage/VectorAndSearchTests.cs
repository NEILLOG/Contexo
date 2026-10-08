using Contexo.Core.Abstractions;
using Contexo.Core.Storage;

namespace Contexo.Core.Tests.Storage;

public sealed class VectorAndSearchTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public void Serializer_round_trips_bit_for_bit()
    {
        float[] values = [0f, -0f, 1.5f, float.MaxValue, float.Epsilon, float.NaN, float.PositiveInfinity, -3.25e-7f];

        var bytes = VectorSerializer.ToBytes(values);
        var back = VectorSerializer.FromBytes(bytes);

        Assert.Equal(values.Length * 4, bytes.Length);
        Assert.Equal(values.Select(BitConverter.SingleToInt32Bits), back.Select(BitConverter.SingleToInt32Bits));
        Assert.Equal([0x00, 0x00, 0xC0, 0x3F], bytes.Skip(8).Take(4)); // 1.5f little-endian
    }

    [Fact]
    public void Serializer_rejects_truncated_blobs()
    {
        Assert.Throws<InvalidDataException>(() => VectorSerializer.FromBytes(new byte[5]));
    }

    [Fact]
    public async Task Vectors_round_trip_exactly_and_only_the_requested_model_is_returned()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var path = f.PathOf("docs", "a.txt");
        float[] v0 = [0.1f, -0.2f, 1e-20f, 3.4028235e38f];
        float[] v1 = [float.NaN, 0f, -0f, 7f];
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, path, [StoreFixture.WithVector(0, "a", v0), StoreFixture.WithVector(1, "b", v1)]), Ct);
        var ids = await f.ChunkIdsAsync(path);
        await f.Store.SaveVectorsAsync("other-model", [new StoredVector(ids[0], [5f, 5f])], Ct);

        var stored = await StoreFixture.ToListAsync(f.Store.ReadVectorsAsync(StoreFixture.Model, Ct));

        Assert.Equal(2, stored.Count);
        Assert.Equal(ids[0], stored[0].ChunkId);
        Assert.Equal(v0.Select(BitConverter.SingleToInt32Bits), stored[0].Vector.Select(BitConverter.SingleToInt32Bits));
        Assert.Equal(v1.Select(BitConverter.SingleToInt32Bits), stored[1].Vector.Select(BitConverter.SingleToInt32Bits));
        var other = Assert.Single(await StoreFixture.ToListAsync(f.Store.ReadVectorsAsync("other-model", Ct)));
        Assert.Equal([5f, 5f], other.Vector);
        Assert.Empty(await StoreFixture.ToListAsync(f.Store.ReadVectorsAsync("nope", Ct)));
    }

    [Fact]
    public async Task ReadVectors_can_be_cancelled()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var chunks = Enumerable.Range(0, 20).Select(i => StoreFixture.WithVector(i, $"c{i}", i)).ToList();
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, f.PathOf("docs", "a.txt"), chunks), Ct);
        using var cts = new CancellationTokenSource();

        var count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in f.Store.ReadVectorsAsync(StoreFixture.Model, cts.Token))
            {
                if (++count == 3)
                {
                    await cts.CancelAsync();
                }
            }
        });
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task Chunks_without_vectors_are_listed_and_can_be_filled_in()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var path = f.PathOf("docs", "a.txt");
        await f.Store.ReplaceDocumentAsync(
            StoreFixture.Write(folder.Id, path, [StoreFixture.WithVector(0, "has vector", 1f), StoreFixture.NoVector(1, "no vector one"), StoreFixture.NoVector(2, "no vector two")]),
            Ct);
        var ids = await f.ChunkIdsAsync(path);

        var missing = await StoreFixture.ToListAsync(f.Store.ReadChunksMissingVectorAsync(StoreFixture.Model, Ct));

        Assert.Equal([ids[1], ids[2]], missing.Select(m => m.ChunkId));
        Assert.Equal(["title › no vector one", "title › no vector two"], missing.Select(m => m.EmbeddingText));

        var version = await f.Store.GetIndexVersionAsync(Ct);
        // Includes an id that no longer exists (deleted while vectors were being computed).
        await f.Store.SaveVectorsAsync(StoreFixture.Model, [new StoredVector(ids[1], [2f]), new StoredVector(987654, [3f]), new StoredVector(ids[2], [4f])], Ct);

        Assert.Empty(await StoreFixture.ToListAsync(f.Store.ReadChunksMissingVectorAsync(StoreFixture.Model, Ct)));
        Assert.Equal(3, (await StoreFixture.ToListAsync(f.Store.ReadVectorsAsync(StoreFixture.Model, Ct))).Count);
        Assert.Equal(version + 1, await f.Store.GetIndexVersionAsync(Ct));

        var forNewModel = await StoreFixture.ToListAsync(f.Store.ReadChunksMissingVectorAsync("another-model", Ct));
        Assert.Equal(ids, forNewModel.Select(m => m.ChunkId));
    }

    [Fact]
    public async Task SaveVectors_replaces_an_existing_vector_for_the_same_model()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var path = f.PathOf("docs", "a.txt");
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, path, [StoreFixture.WithVector(0, "x", 1f)]), Ct);
        var ids = await f.ChunkIdsAsync(path);

        await f.Store.SaveVectorsAsync(StoreFixture.Model, [new StoredVector(ids[0], [8f, 9f])], Ct);

        var vector = Assert.Single(await StoreFixture.ToListAsync(f.Store.ReadVectorsAsync(StoreFixture.Model, Ct)));
        Assert.Equal([8f, 9f], vector.Vector);
    }

    [Fact]
    public async Task Chinese_text_is_found_by_the_trigram_index()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var path = f.PathOf("docs", "quote.docx");
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, path, [StoreFixture.NoVector(0, "監視系統建置報價"), StoreFixture.NoVector(1, "完全無關的內容")]), Ct);
        var ids = await f.ChunkIdsAsync(path);

        var hits = await f.Store.KeywordSearchAsync("\"系統建置\"", [], 10, Ct);

        var hit = Assert.Single(hits);
        Assert.Equal(ids[0], hit.ChunkId);
        Assert.True(hit.Rank > 0);
    }

    [Fact]
    public async Task Short_terms_are_found_through_like_terms()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var path = f.PathOf("docs", "quote.docx");
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, path, [StoreFixture.NoVector(0, "監視系統建置報價"), StoreFixture.NoVector(1, "只有報價"), StoreFixture.NoVector(2, "無關")]), Ct);
        var ids = await f.ChunkIdsAsync(path);

        Assert.Empty(await f.Store.KeywordSearchAsync("\"報價\"", [], 10, Ct));
        var hits = await f.Store.KeywordSearchAsync(null, ["報價"], 10, Ct);

        Assert.Equal(new HashSet<long> { ids[0], ids[1] }, hits.Select(h => h.ChunkId).ToHashSet());
    }

    [Fact]
    public async Task More_matching_like_terms_rank_higher_and_fts_hits_are_not_duplicated()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var path = f.PathOf("docs", "a.txt");
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, path, [StoreFixture.NoVector(0, "報價 only"), StoreFixture.NoVector(1, "報價 and 合約 both")]), Ct);
        var ids = await f.ChunkIdsAsync(path);

        var like = await f.Store.KeywordSearchAsync(null, ["報價", "合約"], 10, Ct);
        Assert.Equal([ids[1], ids[0]], like.Select(h => h.ChunkId));
        Assert.True(like[0].Rank > like[1].Rank);

        var merged = await f.Store.KeywordSearchAsync("\"both\"", ["報價"], 10, Ct);
        Assert.Equal(2, merged.Count);
        Assert.Equal(merged.Count, merged.Select(h => h.ChunkId).Distinct().Count());

        Assert.Single(await f.Store.KeywordSearchAsync(null, ["報價"], 1, Ct));
        Assert.Empty(await f.Store.KeywordSearchAsync(null, ["報價"], 0, Ct));
    }

    [Fact]
    public async Task Like_terms_treat_percent_and_underscore_literally()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var path = f.PathOf("docs", "a.txt");
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, path, [StoreFixture.NoVector(0, "折扣 50% off"), StoreFixture.NoVector(1, "折扣 50x off"), StoreFixture.NoVector(2, "a_b"), StoreFixture.NoVector(3, "axb")]), Ct);
        var ids = await f.ChunkIdsAsync(path);

        Assert.Equal([ids[0]], (await f.Store.KeywordSearchAsync(null, ["0%"], 10, Ct)).Select(h => h.ChunkId));
        Assert.Equal([ids[2]], (await f.Store.KeywordSearchAsync(null, ["a_b"], 10, Ct)).Select(h => h.ChunkId));
    }

    [Theory]
    [InlineData("\"unterminated")]
    [InlineData("AND OR")]
    [InlineData("(((")]
    [InlineData("col:thing")]
    public async Task Malformed_match_queries_return_no_hits_and_do_not_throw(string query)
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, f.PathOf("docs", "a.txt"), [StoreFixture.NoVector(0, "something thing")]), Ct);

        var hits = await f.Store.KeywordSearchAsync(query, [], 10, Ct);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task A_malformed_query_does_not_hide_like_hits()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, f.PathOf("docs", "a.txt"), [StoreFixture.NoVector(0, "報價單")]), Ct);

        var hits = await f.Store.KeywordSearchAsync("\"unterminated", ["報價"], 10, Ct);

        Assert.Single(hits);
    }

    [Fact]
    public async Task Cascade_deletes_keep_the_full_text_index_in_step()
    {
        using var f = await StoreFixture.CreateAsync();
        var folder = await f.AddFolderAsync("docs");
        var path = f.PathOf("docs", "a.txt");
        await f.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, path, [StoreFixture.NoVector(0, "cascade victim text")]), Ct);
        Assert.Single(await f.Store.KeywordSearchAsync("\"victim\"", [], 10, Ct));

        // Deleting the document row cascades to chunks; the FTS delete trigger must fire for those rows.
        await f.ExecuteAsync("DELETE FROM documents");

        Assert.Empty(await f.Store.KeywordSearchAsync("\"victim\"", [], 10, Ct));
        await f.ExecuteAsync("INSERT INTO chunks_fts(chunks_fts) VALUES ('integrity-check')");
    }
}
