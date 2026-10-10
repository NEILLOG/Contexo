using Contexo.Core.Abstractions;
using Contexo.Mcp.Tests.Support;
using Contexo.Mcp.Tools;

namespace Contexo.Mcp.Tests.Tools;

public sealed class ContexoToolServiceTests : IAsyncLifetime
{
    private ServiceFixture _fixture = null!;

    public async Task InitializeAsync() => _fixture = await ServiceFixture.CreateAsync();

    public Task DisposeAsync()
    {
        _fixture.Dispose();
        return Task.CompletedTask;
    }

    private ContexoToolService Service => _fixture.Service;

    [Fact]
    public async Task Search_formats_file_name_location_path_and_content()
    {
        var outcome = await Service.SearchAsync(TestDatabase.QuoteQuery, 8, CancellationToken.None);

        Assert.False(outcome.IsError);
        Assert.StartsWith("（目前只使用關鍵字比對）", outcome.Text);
        Assert.Contains("找到 1 筆相關內容：", outcome.Text);
        Assert.Contains("[1] 2025_台中案_報價單.docx — 報價條款 › 付款方式", outcome.Text);
        Assert.Contains("路徑：" + Path.Combine(_fixture.Database.DocsDirectory, "2025_台中案_報價單.docx"), outcome.Text);
        Assert.Contains("內容：", outcome.Text);
        Assert.Contains("報價總計 128 萬元", outcome.Text);
        Assert.DoesNotContain("table_id", outcome.Text);
    }

    [Fact]
    public async Task Search_shows_the_slide_number_and_title()
    {
        var outcome = await Service.SearchAsync(TestDatabase.SlideQuery, 8, CancellationToken.None);

        Assert.Contains("[1] 簡報.pptx — 第 3 張投影片「營收摘要」", outcome.Text);
    }

    [Fact]
    public async Task Search_offers_a_table_id_for_a_large_table()
    {
        var outcome = await Service.SearchAsync("訂單清單", 8, CancellationToken.None);
        var tableId = await TestDatabase.TableIdAsync(_fixture.Search, "訂單清單");

        Assert.Contains($"table_id: {tableId}（這是大型表格，可用 describe_table / query_table 查詢完整資料）", outcome.Text);
        Assert.Contains("訂單.csv — 工作表「", outcome.Text);
    }

    [Fact]
    public async Task Search_does_not_offer_a_table_id_for_a_table_embedded_in_another_file()
    {
        var outcome = await Service.SearchAsync("內嵌訂單明細", 8, CancellationToken.None);

        Assert.Contains("簡報.pptx — 內嵌：內嵌.xlsx › 工作表「Sheet1」 · A1:F20", outcome.Text);
        Assert.DoesNotContain("table_id:", outcome.Text);
        Assert.Contains("內嵌在檔案裡的大型表格", outcome.Text);
    }

    [Fact]
    public async Task Search_cuts_content_longer_than_1500_characters()
    {
        var outcome = await Service.SearchAsync(TestDatabase.LongQuery, 8, CancellationToken.None);

        Assert.Contains("已截斷，原文共 4000 字", outcome.Text);
        var start = outcome.Text.IndexOf("\n內容：\n", StringComparison.Ordinal) + 5;
        var end = outcome.Text.IndexOf("…（內容太長", StringComparison.Ordinal);
        Assert.Equal(1500, end - start);
    }

    [Fact]
    public async Task Search_without_a_match_gives_the_no_results_sentence()
    {
        var outcome = await Service.SearchAsync("完全不存在的字詞", 8, CancellationToken.None);

        Assert.False(outcome.IsError);
        Assert.Equal("沒有找到相關內容。可以換個說法，或確認檔案所在的資料夾已加入 Contexo。", outcome.Text);
    }

    [Fact]
    public async Task Search_clamps_top_k_to_1_through_20()
    {
        var spy = new SpySearchService();
        using var fixture = await ServiceFixture.CreateAsync(searchOverride: spy);

        await fixture.Service.SearchAsync("報價", 0, CancellationToken.None);
        await fixture.Service.SearchAsync("報價", 500, CancellationToken.None);
        await fixture.Service.SearchAsync("報價", 8, CancellationToken.None);

        Assert.Equal([1, 20, 8], spy.TopKs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Search_without_a_query_is_a_plain_error(string? query)
    {
        var outcome = await Service.SearchAsync(query, 8, CancellationToken.None);

        Assert.True(outcome.IsError);
        Assert.Equal(ContexoToolService.EmptyQuery, outcome.Text);
    }

    [Fact]
    public async Task Search_failures_become_a_plain_error_without_the_query()
    {
        using var fixture = await ServiceFixture.CreateAsync(searchOverride: new ThrowingSearchService());

        var outcome = await fixture.Service.SearchAsync("機密查詢內容", 8, CancellationToken.None);

        Assert.True(outcome.IsError);
        Assert.Equal(ContexoToolService.SearchFailed, outcome.Text);
        Assert.DoesNotContain("機密查詢內容", outcome.ErrorDetail);
        Assert.StartsWith("InvalidOperationException：", outcome.ErrorDetail);
    }

    [Fact]
    public async Task Search_cancellation_is_not_swallowed()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.SearchAsync("報價", 8, cancelled.Token));
    }

    [Fact]
    public async Task Describe_table_lists_columns_and_five_sample_rows()
    {
        var tableId = await TestDatabase.TableIdAsync(_fixture.Search, "訂單清單");

        var outcome = await Service.DescribeTableAsync(tableId, CancellationToken.None);

        Assert.False(outcome.IsError);
        Assert.Contains($"表格 {tableId}", outcome.Text);
        Assert.Contains("檔名：訂單.csv", outcome.Text);
        Assert.Contains("資料列數：600", outcome.Text);
        Assert.Contains("| SQL 名稱 | 原欄名 | 型別 |", outcome.Text);
        Assert.Contains("| 客戶 | 客戶 | text |", outcome.Text);
        Assert.Contains("| 金額 | 金額 | number |", outcome.Text);
        Assert.Contains("前 5 列範例：", outcome.Text);
        Assert.Contains("查詢時資料表名稱為 t，欄名請用雙引號", outcome.Text);
        Assert.Equal(5, outcome.Text.Split('\n').Count(line => line.StartsWith("| 乙 |") || line.StartsWith("| 丙 |") || line.StartsWith("| 甲 |")));
    }

    [Fact]
    public async Task Query_table_returns_a_markdown_table()
    {
        var tableId = await TestDatabase.TableIdAsync(_fixture.Search, "訂單清單");

        var outcome = await Service.QueryTableAsync(tableId, "SELECT \"客戶\", SUM(\"金額\") AS 合計 FROM t GROUP BY \"客戶\" ORDER BY 1", 100, CancellationToken.None);

        Assert.False(outcome.IsError);
        Assert.Contains("查詢結果共 3 列：", outcome.Text);
        Assert.Contains("| 客戶 | 合計 |", outcome.Text);
        Assert.Contains("| --- | --- |", outcome.Text);
        // 甲 holds the multiples of 3: 10 * (3 + 6 + ... + 600).
        Assert.Contains($"| 甲 | {Enumerable.Range(1, 200).Sum(i => i * 3 * 10)} |", outcome.Text);
    }

    [Fact]
    public async Task Query_table_says_when_rows_were_cut()
    {
        var tableId = await TestDatabase.TableIdAsync(_fixture.Search, "訂單清單");

        var outcome = await Service.QueryTableAsync(tableId, "SELECT \"金額\" FROM t", 7, CancellationToken.None);

        Assert.Contains("查詢結果共 7 列：", outcome.Text);
        Assert.Contains("結果超過 7 列，只顯示前 7 列", outcome.Text);
    }

    [Fact]
    public async Task Query_table_clamps_max_rows_to_500()
    {
        var tableId = await TestDatabase.TableIdAsync(_fixture.Search, "訂單清單");

        var outcome = await Service.QueryTableAsync(tableId, "SELECT \"金額\" FROM t", 100_000, CancellationToken.None);

        Assert.Contains("查詢結果共 500 列：", outcome.Text);
        Assert.Contains("只顯示前 500 列", outcome.Text);
    }

    [Fact]
    public async Task Query_table_escapes_pipes_and_line_breaks_in_cells()
    {
        var tableId = await TestDatabase.TableIdAsync(_fixture.Search, "訂單清單");

        var outcome = await Service.QueryTableAsync(tableId, "SELECT 'a|b' || char(10) || 'c' AS x, NULL AS y FROM t LIMIT 1", 5, CancellationToken.None);

        Assert.Contains("| a\\|b c |  |", outcome.Text);
    }

    [Theory]
    [InlineData("DELETE FROM t")]
    [InlineData("SELECT 1; DROP TABLE t")]
    [InlineData("SELECT 不存在的欄 FROM t")]
    [InlineData("這不是 SQL")]
    public async Task Query_table_with_bad_sql_is_a_readable_error_and_the_next_call_still_works(string sql)
    {
        var tableId = await TestDatabase.TableIdAsync(_fixture.Search, "訂單清單");

        var bad = await Service.QueryTableAsync(tableId, sql, 10, CancellationToken.None);
        var good = await Service.QueryTableAsync(tableId, "SELECT COUNT(*) FROM t", 10, CancellationToken.None);

        Assert.True(bad.IsError);
        Assert.False(string.IsNullOrWhiteSpace(bad.Text));
        Assert.DoesNotContain("Exception", bad.Text);
        Assert.False(good.IsError);
        Assert.Contains("| 600 |", good.Text);
        Assert.DoesNotContain(sql, bad.ErrorDetail ?? "");
    }

    [Fact]
    public async Task A_bad_column_name_lists_the_usable_columns()
    {
        var tableId = await TestDatabase.TableIdAsync(_fixture.Search, "訂單清單");

        var outcome = await Service.QueryTableAsync(tableId, "SELECT \"沒有這欄\" FROM t", 10, CancellationToken.None);

        Assert.True(outcome.IsError);
        Assert.Contains("\"客戶\"", outcome.Text);
        Assert.Contains("\"金額\"", outcome.Text);
    }

    [Fact]
    public async Task An_unknown_table_id_is_a_readable_error()
    {
        var describe = await Service.DescribeTableAsync("t99999", CancellationToken.None);
        var query = await Service.QueryTableAsync("t99999", "SELECT 1 FROM t", 10, CancellationToken.None);

        Assert.True(describe.IsError);
        Assert.Contains("找不到這個表格", describe.Text);
        Assert.True(query.IsError);
        Assert.Contains("找不到這個表格", query.Text);
    }

    [Fact]
    public async Task A_table_whose_original_file_was_deleted_is_a_readable_error()
    {
        var tableId = await TestDatabase.TableIdAsync(_fixture.Search, "訂單清單");
        File.Delete(_fixture.Database.CsvPath);

        var outcome = await Service.DescribeTableAsync(tableId, CancellationToken.None);

        Assert.True(outcome.IsError);
        Assert.Contains("訂單.csv", outcome.Text);
    }

    [Fact]
    public async Task Missing_arguments_are_plain_errors()
    {
        Assert.Equal(ContexoToolService.EmptyTableId, (await Service.DescribeTableAsync(" ", CancellationToken.None)).Text);
        Assert.Equal(ContexoToolService.EmptyTableId, (await Service.QueryTableAsync(null, "SELECT 1", 5, CancellationToken.None)).Text);
        Assert.Equal(ContexoToolService.EmptySql, (await Service.QueryTableAsync("t1", " ", 5, CancellationToken.None)).Text);
    }

    [Fact]
    public async Task An_embedded_table_cannot_be_queried_and_the_message_says_so()
    {
        // Observed behaviour of the known gap: the indexer registers a table found inside a pptx, but the table service can only reopen real spreadsheets.
        var embeddedId = await EmbeddedTableIdAsync();

        var describe = await Service.DescribeTableAsync(embeddedId, CancellationToken.None);
        var query = await Service.QueryTableAsync(embeddedId, "SELECT * FROM t", 10, CancellationToken.None);

        foreach (var outcome in new[] { describe, query })
        {
            Assert.True(outcome.IsError);
            Assert.Contains("簡報.pptx", outcome.Text);
            Assert.Contains("內嵌", outcome.Text);
            Assert.Contains("無法用 describe_table / query_table 查詢", outcome.Text);
            Assert.Contains("搜尋結果裡已經顯示的文字內容", outcome.Text);
            Assert.StartsWith("EmbeddedTable：", outcome.ErrorDetail);
        }
    }

    [Fact]
    public async Task Without_the_guard_the_table_service_alone_fails_with_a_misleading_message()
    {
        // Documents why the guard exists: this is what T12 does with the same table when called directly.
        var embeddedId = await EmbeddedTableIdAsync();
        var tables = new Contexo.Core.Tables.TableQueryService(
            _fixture.Store, new Contexo.Core.Parsing.Spreadsheet.SpreadsheetRegionReader(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Contexo.Core.Tables.TableQueryService>.Instance);
        await File.WriteAllTextAsync(Path.Combine(_fixture.Database.DocsDirectory, "簡報.pptx"), "not a spreadsheet");

        var exception = await Record.ExceptionAsync(() => tables.DescribeAsync(embeddedId, 5, CancellationToken.None));

        Assert.NotNull(exception);
        Assert.IsType<TableQueryException>(exception);
        Assert.Contains("簡報.pptx", exception.Message);
        tables.Dispose();
    }

    [Fact]
    public async Task Unexpected_table_failures_become_a_generic_plain_error()
    {
        using var fixture = await ServiceFixture.CreateAsync(tableOverride: new ThrowingTableService());
        var tableId = await TestDatabase.TableIdAsync(fixture.Search, "訂單清單");

        var describe = await fixture.Service.DescribeTableAsync(tableId, CancellationToken.None);
        var query = await fixture.Service.QueryTableAsync(tableId, "SELECT 機密 FROM t", 5, CancellationToken.None);

        foreach (var outcome in new[] { describe, query })
        {
            Assert.True(outcome.IsError);
            Assert.Equal(ContexoToolService.TableFailed, outcome.Text);
            Assert.StartsWith("IOException：", outcome.ErrorDetail);
            Assert.DoesNotContain("機密", outcome.ErrorDetail);
        }
    }

    private async Task<string> EmbeddedTableIdAsync()
    {
        var response = await _fixture.Search.SearchAsync(new SearchRequest("內嵌訂單明細", 5), CancellationToken.None);
        return response.Hits.Single().TableId!;
    }

    private sealed class SpySearchService : ISearchService
    {
        public List<int> TopKs { get; } = [];

        public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
        {
            TopKs.Add(request.TopK);
            return Task.FromResult(new SearchResponse([], false));
        }
    }

    private sealed class ThrowingSearchService : ISearchService
    {
        public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("機密查詢內容 leaked");
    }

    private sealed class ThrowingTableService : ITableQueryService
    {
        public Task<TableDescription> DescribeAsync(string tableId, int sampleRows, CancellationToken cancellationToken) =>
            throw new IOException("機密 path");

        public Task<TableQueryResult> QueryAsync(string tableId, string sql, int maxRows, CancellationToken cancellationToken) =>
            throw new IOException("機密 sql");
    }
}

public sealed class EmptyDatabaseTests : IAsyncLifetime
{
    private ServiceFixture _fixture = null!;

    public async Task InitializeAsync() => _fixture = await ServiceFixture.CreateAsync(withData: false);

    public Task DisposeAsync()
    {
        _fixture.Dispose();
        return Task.CompletedTask;
    }

    private const string NoData = "Contexo 還沒有收錄任何資料。請先開啟 Contexo，加入要讓 AI 讀取的資料夾。";

    [Fact]
    public async Task Search_on_an_empty_database_says_nothing_is_indexed_yet()
    {
        var outcome = await _fixture.Service.SearchAsync("報價單", 8, CancellationToken.None);

        Assert.False(outcome.IsError);
        Assert.Equal(NoData, outcome.Text);
    }

    [Fact]
    public async Task Table_tools_on_an_empty_database_say_nothing_is_indexed_yet()
    {
        var describe = await _fixture.Service.DescribeTableAsync("t1", CancellationToken.None);
        var query = await _fixture.Service.QueryTableAsync("t1", "SELECT 1 FROM t", 5, CancellationToken.None);

        Assert.Equal(NoData, describe.Text);
        Assert.Equal(NoData, query.Text);
    }

    [Fact]
    public async Task An_unopenable_database_is_a_plain_error_not_a_crash()
    {
        var store = System.Reflection.DispatchProxy.Create<IKnowledgeStore, FailingStore>();
        var service = new ContexoToolService(
            store, new NullSearch(), new NullTables(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ContexoToolService>.Instance);

        var search = await service.SearchAsync("報價單", 8, CancellationToken.None);
        var describe = await service.DescribeTableAsync("t1", CancellationToken.None);

        Assert.True(search.IsError);
        Assert.Equal(ContexoToolService.StoreUnavailable, search.Text);
        Assert.True(describe.IsError);
        Assert.Equal(ContexoToolService.StoreUnavailable, describe.Text);
        Assert.True(((FailingStore)(object)store).InitializeCalls >= 2, "Each call retries opening the database.");
    }

    private sealed class NullSearch : ISearchService
    {
        public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken) => Task.FromResult(new SearchResponse([], false));
    }

    private sealed class NullTables : ITableQueryService
    {
        public Task<TableDescription> DescribeAsync(string tableId, int sampleRows, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TableQueryResult> QueryAsync(string tableId, string sql, int maxRows, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private class FailingStore : System.Reflection.DispatchProxy
    {
        public int InitializeCalls { get; private set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IKnowledgeStore.InitializeAsync))
            {
                InitializeCalls++;
            }

            throw new IOException("unable to open database file");
        }
    }
}
