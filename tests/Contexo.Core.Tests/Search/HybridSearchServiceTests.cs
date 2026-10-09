using Contexo.Core.Abstractions;
using Contexo.Core.Search;
using Contexo.Core.Tests.Common;
using Contexo.Core.Tests.Storage;

namespace Contexo.Core.Tests.Search;

public sealed class HybridSearchServiceTests : IDisposable
{
    private static readonly float[] E0 = [1, 0, 0];
    private static readonly float[] E1 = [0, 1, 0];
    private static readonly float[] Near0 = [0.8f, 0.6f, 0];

    private readonly StoreFixture _fixture = StoreFixture.CreateAsync().GetAwaiter().GetResult();
    private readonly FakeEmbeddingService _embedding = new();
    private readonly TestLogger<HybridSearchService> _logger = new();

    private HybridSearchService Service => new(_fixture.Store, _embedding, _logger);

    public void Dispose() => _fixture.Dispose();

    private Task<SearchResponse> SearchAsync(string query, int topK = 8, params string[] prefixes) =>
        Service.SearchAsync(new SearchRequest(query, topK, prefixes.Length == 0 ? null : prefixes), CancellationToken.None);

    private async Task<long> WriteAsync(string folderPath, string file, params ChunkWrite[] chunks)
    {
        var folder = (await _fixture.Store.GetFoldersAsync(CancellationToken.None)).FirstOrDefault(f => f.Path == folderPath)
            ?? await _fixture.Store.AddFolderAsync(folderPath, CancellationToken.None);
        await _fixture.Store.ReplaceDocumentAsync(StoreFixture.Write(folder.Id, file, chunks), CancellationToken.None);
        return folder.Id;
    }

    [Fact]
    public async Task EmptyQuery_ReturnsEmptyResult()
    {
        await WriteAsync(_fixture.PathOf("docs"), _fixture.PathOf("docs", "a.txt"), StoreFixture.WithVector(0, "報價單", E0));

        var response = await SearchAsync("   ");

        Assert.Empty(response.Hits);
        Assert.False(response.Degraded);
    }

    [Fact]
    public async Task KeywordOnly_SemanticOnly_AndBoth_AreLabelled()
    {
        _embedding.QueryVector = E0;
        var folder = _fixture.PathOf("docs");
        // A: matches the words and the vector. B: vector only. C: words only (no vector stored).
        await WriteAsync(folder, _fixture.PathOf("docs", "a.txt"), StoreFixture.WithVector(0, "這是報價單內容", E0));
        await WriteAsync(folder, _fixture.PathOf("docs", "b.txt"), StoreFixture.WithVector(0, "完全無關的段落", Near0));
        await WriteAsync(folder, _fixture.PathOf("docs", "c.txt"), StoreFixture.NoVector(0, "另一份報價單紀錄"));

        var response = await SearchAsync("報價單");

        Assert.False(response.Degraded);
        var byFile = response.Hits.ToDictionary(h => h.FileName);
        Assert.Equal(MatchKinds.Semantic | MatchKinds.Keyword, byFile["a.txt"].MatchedBy);
        Assert.Equal(MatchKinds.Semantic, byFile["b.txt"].MatchedBy);
        Assert.Equal(MatchKinds.Keyword, byFile["c.txt"].MatchedBy);
        Assert.Equal("a.txt", response.Hits[0].FileName);
        Assert.Equal(1.0, response.Hits[0].Score, 6);
        Assert.All(response.Hits, h => Assert.InRange(h.Score, 0, 1));
        Assert.True(response.Hits.Zip(response.Hits.Skip(1)).All(p => p.First.Score >= p.Second.Score));
    }

    [Fact]
    public async Task KeywordOnlyQuery_FindsShortTermsThroughLike()
    {
        _embedding.QueryVector = E1;
        await WriteAsync(_fixture.PathOf("docs"), _fixture.PathOf("docs", "a.txt"), StoreFixture.NoVector(0, "本季營收成長"));

        var response = await SearchAsync("營收");

        var hit = Assert.Single(response.Hits);
        Assert.Equal(MatchKinds.Keyword, hit.MatchedBy);
    }

    [Fact]
    public async Task EmbeddingUnavailable_IsDegradedButKeywordResultsRemain()
    {
        _embedding.Available = false;
        await WriteAsync(_fixture.PathOf("docs"), _fixture.PathOf("docs", "a.txt"), StoreFixture.WithVector(0, "這是報價單內容", E0));

        var response = await SearchAsync("報價單");

        Assert.True(response.Degraded);
        var hit = Assert.Single(response.Hits);
        Assert.Equal(MatchKinds.Keyword, hit.MatchedBy);
    }

    [Fact]
    public async Task EmbeddingThrows_IsDegradedButKeywordResultsRemain()
    {
        _embedding.ThrowOnQuery = true;
        await WriteAsync(_fixture.PathOf("docs"), _fixture.PathOf("docs", "a.txt"), StoreFixture.WithVector(0, "這是報價單內容", E0));

        var response = await SearchAsync("報價單");

        Assert.True(response.Degraded);
        Assert.Single(response.Hits);
    }

    [Fact]
    public async Task NewDocument_IsFoundBySubsequentSearch()
    {
        _embedding.QueryVector = E0;
        var folder = _fixture.PathOf("docs");
        await WriteAsync(folder, _fixture.PathOf("docs", "a.txt"), StoreFixture.WithVector(0, "舊文件內容", E1));
        var service = Service;

        var before = await service.SearchAsync(new SearchRequest("某個查詢"), CancellationToken.None);
        Assert.DoesNotContain(before.Hits, h => h.FileName == "new.txt");

        await WriteAsync(folder, _fixture.PathOf("docs", "new.txt"), StoreFixture.WithVector(0, "剛加入的文件", E0));

        var after = await service.SearchAsync(new SearchRequest("某個查詢"), CancellationToken.None);
        Assert.Equal("new.txt", after.Hits[0].FileName);
        Assert.Equal(MatchKinds.Semantic, after.Hits[0].MatchedBy);

        // A removed document disappears as well.
        var document = await _fixture.Store.GetDocumentByPathAsync(_fixture.PathOf("docs", "new.txt"), CancellationToken.None);
        await _fixture.Store.DeleteDocumentAsync(document!.Id, CancellationToken.None);
        var removed = await service.SearchAsync(new SearchRequest("某個查詢"), CancellationToken.None);
        Assert.DoesNotContain(removed.Hits, h => h.FileName == "new.txt");
    }

    [Fact]
    public async Task ModelChange_ReloadsVectors()
    {
        _embedding.QueryVector = E0;
        var folder = _fixture.PathOf("docs");
        await WriteAsync(folder, _fixture.PathOf("docs", "a.txt"), StoreFixture.WithVector(0, "內容", E0));
        var service = Service;
        Assert.Single((await service.SearchAsync(new SearchRequest("查詢"), CancellationToken.None)).Hits);

        _embedding.ModelId = "another-model";

        var response = await service.SearchAsync(new SearchRequest("查詢"), CancellationToken.None);
        Assert.Empty(response.Hits);
        Assert.False(response.Degraded);
    }

    [Fact]
    public async Task PathPrefixes_RespectDirectoryBoundariesAndIgnoreCase()
    {
        _embedding.QueryVector = E1;
        var folder = _fixture.PathOf("A");
        await WriteAsync(folder, _fixture.PathOf("A", "報價", "one.txt"), StoreFixture.NoVector(0, "關於產品規格說明"));
        await WriteAsync(folder, _fixture.PathOf("A", "報價單", "two.txt"), StoreFixture.NoVector(0, "關於產品規格說明"));
        await WriteAsync(folder, _fixture.PathOf("A", "報價", "sub", "three.txt"), StoreFixture.NoVector(0, "關於產品規格說明"));

        var all = await SearchAsync("產品規格");
        Assert.Equal(3, all.Hits.Count);

        var filtered = await SearchAsync("產品規格", 8, _fixture.PathOf("A", "報價"));
        Assert.Equal(["one.txt", "three.txt"], filtered.Hits.Select(h => h.FileName).Order().ToArray());

        var upper = await SearchAsync("產品規格", 8, _fixture.PathOf("A", "報價").ToUpperInvariant() + Path.DirectorySeparatorChar);
        Assert.Equal(2, upper.Hits.Count);

        var twoFolders = await SearchAsync("產品規格", 8, _fixture.PathOf("A", "報價單"), _fixture.PathOf("A", "報價", "sub"));
        Assert.Equal(["three.txt", "two.txt"], twoFolders.Hits.Select(h => h.FileName).Order().ToArray());
    }

    [Fact]
    public async Task PathPrefixes_MatchBothSeparatorStyles()
    {
        _embedding.Available = false;
        // Paths are stored as given by the indexer; the filter must not care which separator the caller uses.
        var folder = _fixture.PathOf("A");
        await WriteAsync(folder, _fixture.PathOf("A", "報價", "one.txt"), StoreFixture.NoVector(0, "關於產品規格說明"));

        var prefix = _fixture.PathOf("A", "報價").Replace('/', '\\');
        var response = await SearchAsync("產品規格", 8, prefix);

        Assert.Single(response.Hits);
    }

    [Fact]
    public async Task SameFile_ContributesAtMostThreeHits()
    {
        _embedding.Available = false;
        var folder = _fixture.PathOf("docs");
        await WriteAsync(
            folder,
            _fixture.PathOf("docs", "big.txt"),
            Enumerable.Range(0, 6).Select(i => StoreFixture.NoVector(i, $"第{i}段 專案進度報告")).ToArray());
        await WriteAsync(folder, _fixture.PathOf("docs", "other.txt"), StoreFixture.NoVector(0, "另一個專案進度報告"));

        var response = await SearchAsync("專案進度報告");

        Assert.Equal(3, response.Hits.Count(h => h.FileName == "big.txt"));
        Assert.Contains(response.Hits, h => h.FileName == "other.txt");
        Assert.Equal(4, response.Hits.Count);
    }

    [Fact]
    public async Task TopK_LimitsResults()
    {
        _embedding.Available = false;
        var folder = _fixture.PathOf("docs");
        for (var i = 0; i < 5; i++)
        {
            await WriteAsync(folder, _fixture.PathOf("docs", $"f{i}.txt"), StoreFixture.NoVector(0, "會議紀錄內容"));
        }

        var response = await SearchAsync("會議紀錄", topK: 2);

        Assert.Equal(2, response.Hits.Count);
    }

    [Fact]
    public async Task TableSummaryHit_CarriesTableId()
    {
        _embedding.Available = false;
        var table = StoreFixture.MakeTable();
        var chunk = new ChunkWrite(StoreFixture.MakeChunk(0, "報價表，欄位：品名、數量、單價", table.TableKey, SectionKind.TableSummary), null);
        var folder = await _fixture.AddFolderAsync("sheets");
        await _fixture.Store.ReplaceDocumentAsync(
            StoreFixture.Write(folder.Id, _fixture.PathOf("sheets", "price.xlsx"), [chunk], [table]),
            CancellationToken.None);
        await WriteAsync(_fixture.PathOf("sheets"), _fixture.PathOf("sheets", "note.txt"), StoreFixture.NoVector(0, "報價表的使用說明"));

        var response = await SearchAsync("報價表 欄位");

        var summary = Assert.Single(response.Hits, h => h.Kind == SectionKind.TableSummary);
        Assert.False(string.IsNullOrEmpty(summary.TableId));
        Assert.Equal("price.xlsx", summary.FileName);
        Assert.All(response.Hits.Where(h => h.Kind != SectionKind.TableSummary), h => Assert.Null(h.TableId));
    }

    [Fact]
    public async Task HostileQuery_DoesNotThrow()
    {
        await WriteAsync(_fixture.PathOf("docs"), _fixture.PathOf("docs", "a.txt"), StoreFixture.WithVector(0, "foo bar \"baz\"", E0));

        var response = await SearchAsync("\"foo\" AND (bar* NOT baz) col:x ^");

        Assert.Contains(response.Hits, h => h.FileName == "a.txt");
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public async Task Logs_NeverContainTheQueryText()
    {
        const string secret = "極機密預算查詢句子";
        await WriteAsync(_fixture.PathOf("docs"), _fixture.PathOf("docs", "a.txt"), StoreFixture.WithVector(0, secret + "內容", E0));

        await SearchAsync(secret);
        _embedding.ThrowOnQuery = true;
        await SearchAsync(secret);

        Assert.NotEmpty(_logger.Entries);
        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("極機密", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentSearches_AllSucceed()
    {
        _embedding.QueryVector = E0;
        var folder = _fixture.PathOf("docs");
        for (var i = 0; i < 10; i++)
        {
            await WriteAsync(folder, _fixture.PathOf("docs", $"f{i}.txt"), StoreFixture.WithVector(0, $"文件{i} 報價單", i % 2 == 0 ? E0 : Near0));
        }

        var service = Service;
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                await WriteAsync(folder, _fixture.PathOf("docs", $"late{i}.txt"), StoreFixture.WithVector(0, "後來的文件 報價單", E0));
            }
        });
        var searches = Enumerable.Range(0, 24)
            .Select(_ => Task.Run(() => service.SearchAsync(new SearchRequest("報價單", 5), CancellationToken.None)))
            .ToArray();

        await Task.WhenAll(searches.Append(writer));

        Assert.All(searches, t => Assert.Equal(5, t.Result.Hits.Count));
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service.SearchAsync(new SearchRequest("報價單"), cts.Token));
    }

    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public string ModelId { get; set; } = StoreFixture.Model;

        public int Dimensions => 3;

        public bool Available { get; set; } = true;

        public bool ThrowOnQuery { get; set; }

        public float[] QueryVector { get; set; } = [1, 0, 0];

        public bool IsAvailable => Available;

        public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => QueryVector).ToList());

        public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ThrowOnQuery ? throw new InvalidOperationException("boom") : Task.FromResult(QueryVector);
        }
    }
}
