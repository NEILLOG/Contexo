using System.Diagnostics;
using Contexo.Core.Abstractions;
using Contexo.Core.Tables;
using Xunit.Abstractions;

namespace Contexo.Core.Tests.Tables;

public sealed class TableQueryServiceTests(ITestOutputHelper output) : IDisposable
{
    private readonly TableHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static readonly string[] SalesColumns = ["日期", "客戶", "金額(元)", "毛利率", "編號", "備註"];

    private string AddSales()
    {
        string?[][] rows =
        [
            ["2025/01/05", "甲公司", "1,200", "25.6%", "007", "首單"],
            ["2025-02-10 08:30:00", "乙公司", "$ 3,400", " 10% ", "012", ""],
            ["2025/3/1", "甲公司", "NT$ 5,000", "5%", "0100", null],
            ["", "丙公司", "(500)", "7.5%", "A1", "退貨"],
            ["2025/04/01", "乙公司", "800", "", "B2", "x"],
        ];
        return _h.AddTable(SalesColumns, rows);
    }

    [Fact]
    public async Task Describe_returns_columns_types_row_count_and_sample_rows()
    {
        var id = AddSales();

        var description = await _h.Service.DescribeAsync(id, 3, CancellationToken.None);

        Assert.Equal("t", description.SqlTableName);
        Assert.Equal(id, description.TableId);
        Assert.Equal("Sheet1", description.Sheet);
        Assert.Equal(5, description.RowCount);
        Assert.Equal(["日期", "客戶", "金額(元)", "毛利率", "編號", "備註"], description.Columns.Select(c => c.Header));
        Assert.Equal(["日期", "客戶", "金額_元_", "毛利率", "編號", "備註"], description.Columns.Select(c => c.SqlName));
        Assert.Equal(["date", "text", "number", "number", "text", "text"], description.Columns.Select(c => c.InferredType));
        Assert.Equal(3, description.SampleRows.Count);
        Assert.Equal(["2025-01-05 00:00:00", "甲公司", "1200", "25.6", "007", "首單"], description.SampleRows[0]);
        // Dates keep their time, empty strings become NULL, numbers lose the thousands separator and currency symbol.
        Assert.Equal(["2025-02-10 08:30:00", "乙公司", "3400", "10", "012", null], description.SampleRows[1]);
        Assert.Equal(["2025-03-01 00:00:00", "甲公司", "5000", "5", "0100", null], description.SampleRows[2]);
    }

    [Fact]
    public async Task Describe_caps_sample_rows_at_20_and_allows_zero()
    {
        var id = _h.AddTable(["n"], Enumerable.Range(1, 50).Select(i => new string?[] { i.ToString() }));

        Assert.Equal(20, (await _h.Service.DescribeAsync(id, 100, CancellationToken.None)).SampleRows.Count);
        Assert.Empty((await _h.Service.DescribeAsync(id, 0, CancellationToken.None)).SampleRows);
    }

    [Fact]
    public async Task Query_sums_by_group_and_orders()
    {
        string?[][] rows =
        [
            ["甲", "100"], ["乙", "50"], ["甲", "300"], ["丙", "1,000"], ["乙", "75.5"],
        ];
        var id = _h.AddTable(["客戶", "金額"], rows);

        var result = await _h.Service.QueryAsync(id, "SELECT \"客戶\", SUM(\"金額\") FROM t GROUP BY \"客戶\" ORDER BY 2 DESC", 100, CancellationToken.None);

        Assert.Equal(["客戶", "SUM(\"金額\")"], result.Columns);
        Assert.False(result.Truncated);
        Assert.Collection(
            result.Rows,
            r => Assert.Equal(["丙", "1000"], r),
            r => Assert.Equal(["甲", "400"], r),
            r => Assert.Equal(["乙", "125.5"], r));
    }

    [Fact]
    public async Task Query_formats_values_without_extra_decimals_and_keeps_nulls()
    {
        var id = AddSales();

        var result = await _h.Service.QueryAsync(
            id,
            "SELECT SUM(\"金額_元_\"), COUNT(*), AVG(\"毛利率\"), MAX(\"備註\"), 0.1 + 0.2, \"日期\" FROM t WHERE \"客戶\" = '丙公司'",
            10, CancellationToken.None);

        Assert.Equal(["-500", "1", "7.5", "退貨", "0.3", null], result.Rows.Single());
    }

    [Fact]
    public async Task Query_accepts_trailing_semicolon_comments_and_leading_with()
    {
        var id = AddSales();

        var a = await _h.Service.QueryAsync(id, "  SELECT COUNT(*) FROM t ;  ", 10, CancellationToken.None);
        var b = await _h.Service.QueryAsync(id, "-- count\nSELECT /* all */ COUNT(*) FROM t", 10, CancellationToken.None);
        var c = await _h.Service.QueryAsync(id, "with x as (select 1 as n) select n from x", 10, CancellationToken.None);
        var d = await _h.Service.QueryAsync(id, "SELECT 'attach; pragma' AS \"pragma\", 1 FROM t LIMIT 1", 10, CancellationToken.None);
        var e = await _h.Service.QueryAsync(id, "SELECT COUNT(*) FROM t WHERE \"客戶\" = '甲公司';;  -- done", 10, CancellationToken.None);

        Assert.Equal("5", a.Rows.Single().Single());
        Assert.Equal("5", b.Rows.Single().Single());
        Assert.Equal("1", c.Rows.Single().Single());
        Assert.Equal("attach; pragma", d.Rows.Single()[0]);
        Assert.Equal("2", e.Rows.Single().Single());
    }

    [Theory]
    [InlineData("DELETE FROM t")]
    [InlineData("DROP TABLE t")]
    [InlineData("SELECT 1; DELETE FROM t")]
    [InlineData("SELECT 1 ; ; DELETE FROM t;")]
    [InlineData("ATTACH DATABASE ':memory:' AS other")]
    [InlineData("select 1 from t where 1 in (select 1); attach database 'x' as y")]
    [InlineData("PRAGMA query_only = OFF")]
    [InlineData("pragma table_info(t)")]
    [InlineData("SELECT load_extension('x')")]
    [InlineData("SELECT * FROM t WHERE x = 'unterminated")]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("UPDATE t SET 客戶 = 'x'")]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM t")]
    [InlineData("WITH x AS (SELECT 1) UPDATE t SET 客戶 = 'x'")]
    [InlineData("")]
    [InlineData("   ;  ")]
    public async Task Writes_and_dangerous_statements_are_rejected_and_data_is_unchanged(string sql)
    {
        var id = AddSales();

        await Assert.ThrowsAsync<TableQueryException>(() => _h.Service.QueryAsync(id, sql, 10, CancellationToken.None));

        var count = await _h.Service.QueryAsync(id, "SELECT COUNT(*) FROM t", 10, CancellationToken.None);
        Assert.Equal("5", count.Rows.Single().Single());
    }

    [Fact]
    public async Task The_connection_is_read_only_even_when_the_sql_check_is_bypassed()
    {
        var id = AddSales();

        // Passes the keyword check (starts with WITH) but must be stopped by PRAGMA query_only.
        var ex = await Assert.ThrowsAsync<TableQueryException>(() =>
            _h.Service.QueryAsync(id, "WITH x AS (SELECT 1) DELETE FROM t", 10, CancellationToken.None));

        Assert.Contains("readonly", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Max_rows_truncates_and_reports_it()
    {
        var id = _h.AddTable(["n"], Enumerable.Range(1, 10).Select(i => new string?[] { i.ToString() }));

        var three = await _h.Service.QueryAsync(id, "SELECT n FROM t ORDER BY n", 3, CancellationToken.None);
        var exact = await _h.Service.QueryAsync(id, "SELECT n FROM t ORDER BY n", 10, CancellationToken.None);
        var more = await _h.Service.QueryAsync(id, "SELECT n FROM t ORDER BY n", 11, CancellationToken.None);

        Assert.Equal(["1", "2", "3"], three.Rows.Select(r => r[0]));
        Assert.True(three.Truncated);
        Assert.Equal(10, exact.Rows.Count);
        Assert.False(exact.Truncated);
        Assert.False(more.Truncated);
    }

    [Fact]
    public async Task Max_rows_is_capped_at_500()
    {
        var id = _h.AddTable(["n"], Enumerable.Range(1, 600).Select(i => new string?[] { i.ToString() }));

        var result = await _h.Service.QueryAsync(id, "SELECT n FROM t", 100_000, CancellationToken.None);

        Assert.Equal(500, result.Rows.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task Unknown_column_error_lists_the_available_columns()
    {
        var id = AddSales();

        var ex = await Assert.ThrowsAsync<TableQueryException>(() =>
            _h.Service.QueryAsync(id, "SELECT 不存在的欄位 FROM t", 10, CancellationToken.None));

        Assert.Contains("no such column", ex.Message);
        Assert.Contains("\"客戶\"", ex.Message);
        Assert.Contains("\"金額_元_\"", ex.Message);
        Assert.Contains("number", ex.Message);
    }

    [Fact]
    public async Task Syntax_error_is_a_table_query_exception()
    {
        var id = AddSales();

        await Assert.ThrowsAsync<TableQueryException>(() => _h.Service.QueryAsync(id, "SELECT FROM FROM t", 10, CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_table_id_has_a_clear_message()
    {
        var describe = await Assert.ThrowsAsync<TableQueryException>(() => _h.Service.DescribeAsync("nope", 5, CancellationToken.None));
        var query = await Assert.ThrowsAsync<TableQueryException>(() => _h.Service.QueryAsync("nope", "SELECT 1", 5, CancellationToken.None));

        Assert.Equal("找不到這個表格，可能已被移除。請重新搜尋。", describe.Message);
        Assert.Equal(describe.Message, query.Message);
    }

    [Fact]
    public async Task Deleted_source_file_has_a_clear_message_and_drops_the_cache()
    {
        var id = AddSales();
        await _h.Service.QueryAsync(id, "SELECT 1", 5, CancellationToken.None);
        File.Delete(_h.PathOf(id));

        var ex = await Assert.ThrowsAsync<TableQueryException>(() => _h.Service.QueryAsync(id, "SELECT 1", 5, CancellationToken.None));

        Assert.Equal("原始檔案已移動或刪除：" + Path.GetFileName(_h.PathOf(id)), ex.Message);
    }

    [Fact]
    public async Task Reader_failures_are_turned_into_table_query_exceptions()
    {
        var id = AddSales();
        var path = _h.PathOf(id);
        _h.Reader.Regions.Remove(path);

        // The fake reader throws KeyNotFoundException for unknown files; wrap with a reader that throws the documented exceptions instead.
        var service = new TableQueryService(_h.Store, new ThrowingReader(new InvalidOperationException("範圍超出")), _h.Logger);
        var ex = await Assert.ThrowsAsync<TableQueryException>(() => service.DescribeAsync(id, 1, CancellationToken.None));
        Assert.Contains("重新搜尋", ex.Message);

        var missing = new TableQueryService(_h.Store, new ThrowingReader(new FileNotFoundException("x")), _h.Logger);
        var ex2 = await Assert.ThrowsAsync<TableQueryException>(() => missing.DescribeAsync(id, 1, CancellationToken.None));
        Assert.StartsWith("原始檔案已移動或刪除：", ex2.Message);

        var broken = new TableQueryService(_h.Store, new ThrowingReader(new DocumentParseException(DocumentErrorCode.Corrupted, "壞了")), _h.Logger);
        await Assert.ThrowsAsync<TableQueryException>(() => broken.DescribeAsync(id, 1, CancellationToken.None));
    }

    [Fact]
    public async Task Second_query_within_60_seconds_does_not_read_the_file_again()
    {
        var id = AddSales();

        await _h.Service.QueryAsync(id, "SELECT COUNT(*) FROM t", 5, CancellationToken.None);
        _h.Time.Advance(TimeSpan.FromSeconds(30));
        await _h.Service.QueryAsync(id, "SELECT COUNT(*) FROM t", 5, CancellationToken.None);
        await _h.Service.DescribeAsync(id, 2, CancellationToken.None);

        Assert.Equal(1, _h.Reader.ReadCount);
    }

    [Fact]
    public async Task Cache_expires_after_60_seconds()
    {
        var id = AddSales();

        await _h.Service.QueryAsync(id, "SELECT 1", 5, CancellationToken.None);
        _h.Time.Advance(TimeSpan.FromSeconds(59));
        await _h.Service.QueryAsync(id, "SELECT 1", 5, CancellationToken.None);
        Assert.Equal(1, _h.Reader.ReadCount);

        _h.Time.Advance(TimeSpan.FromSeconds(2));
        await _h.Service.QueryAsync(id, "SELECT 1", 5, CancellationToken.None);
        Assert.Equal(2, _h.Reader.ReadCount);
    }

    [Fact]
    public async Task Modifying_the_source_file_reloads_it()
    {
        var id = AddSales();
        await _h.Service.QueryAsync(id, "SELECT 1", 5, CancellationToken.None);

        File.SetLastWriteTimeUtc(_h.PathOf(id), DateTime.UtcNow.AddMinutes(5));
        _h.Reader.Regions[_h.PathOf(id)] = new SpreadsheetRegion(["客戶"], [["新"]]);
        var result = await _h.Service.QueryAsync(id, "SELECT COUNT(*) FROM t", 5, CancellationToken.None);

        Assert.Equal(2, _h.Reader.ReadCount);
        Assert.Equal("1", result.Rows.Single().Single());
    }

    [Fact]
    public async Task At_most_four_tables_stay_cached_and_the_least_recently_used_goes_first()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => _h.AddTable(["n"], [["1"]])).ToList();
        foreach (var id in ids)
        {
            await _h.Service.QueryAsync(id, "SELECT 1", 5, CancellationToken.None);
            _h.Time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(5, _h.Reader.ReadCount);

        // The newest four are cached, the first one was evicted.
        await _h.Service.QueryAsync(ids[4], "SELECT 1", 5, CancellationToken.None);
        await _h.Service.QueryAsync(ids[1], "SELECT 1", 5, CancellationToken.None);
        Assert.Equal(5, _h.Reader.ReadCount);

        await _h.Service.QueryAsync(ids[0], "SELECT 1", 5, CancellationToken.None);
        Assert.Equal(6, _h.Reader.ReadCount);
    }

    [Fact]
    public async Task Concurrent_queries_on_one_table_all_succeed_with_a_single_load()
    {
        var id = AddSales();

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i =>
            _h.Service.QueryAsync(id, $"SELECT SUM(\"金額_元_\") + {i} FROM t", 5, CancellationToken.None)));

        Assert.Equal(1, _h.Reader.ReadCount);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (9900 + i).ToString()), results.Select(r => r.Rows.Single().Single()));
    }

    [Fact]
    public async Task A_runaway_query_is_stopped_after_five_seconds()
    {
        var id = AddSales();
        var clock = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<TableQueryException>(() => _h.Service.QueryAsync(
            id, "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT COUNT(*) FROM c", 5, CancellationToken.None));

        output.WriteLine($"Runaway query stopped after {clock.ElapsedMilliseconds} ms");
        Assert.Contains("5 秒", ex.Message);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(10));

        // The connection is still usable afterwards.
        var count = await _h.Service.QueryAsync(id, "SELECT COUNT(*) FROM t", 5, CancellationToken.None);
        Assert.Equal("5", count.Rows.Single().Single());
    }

    [Fact]
    public async Task Cancelling_stops_a_running_query()
    {
        var id = AddSales();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _h.Service.QueryAsync(
            id, "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT COUNT(*) FROM c", 5, cts.Token));
    }

    [Fact]
    public async Task Huge_values_are_refused()
    {
        var id = AddSales();

        await Assert.ThrowsAsync<TableQueryException>(() =>
            _h.Service.QueryAsync(id, "SELECT length(zeroblob(2000000000))", 5, CancellationToken.None));
    }

    [Fact]
    public async Task Duplicate_empty_and_numeric_headers_get_safe_unique_names()
    {
        var id = _h.AddTable(["金額", "金額", "", "1月", "A b", "a_b"], [["1", "2", "3", "4", "5", "6"]]);

        var description = await _h.Service.DescribeAsync(id, 1, CancellationToken.None);
        var result = await _h.Service.QueryAsync(id, "SELECT \"金額\" + \"金額_2\", column_3, \"c_1月\", \"A_b\", \"a_b_2\" FROM t", 5, CancellationToken.None);

        Assert.Equal(["金額", "金額_2", "column_3", "c_1月", "A_b", "a_b_2"], description.Columns.Select(c => c.SqlName));
        Assert.Equal(["金額", "金額", "", "1月", "A b", "a_b"], description.Columns.Select(c => c.Header));
        Assert.Equal(["3", "3", "4", "5", "6"], result.Rows.Single());
    }

    [Fact]
    public async Task Logs_do_not_contain_the_query_text()
    {
        var id = AddSales();

        await _h.Service.QueryAsync(id, "SELECT '祕密查詢字串' FROM t", 5, CancellationToken.None);

        Assert.NotEmpty(_h.Logger.Entries);
        Assert.DoesNotContain(_h.Logger.Entries, e => e.Message.Contains("祕密查詢字串", StringComparison.Ordinal));
        Assert.DoesNotContain(_h.Logger.Entries, e => e.Message.Contains("甲公司", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Large_table_loads_within_five_seconds_and_queries_within_500_ms()
    {
        const int Rows = 100_000;
        var columns = Enumerable.Range(1, 10).Select(i => "欄位" + i).ToArray();
        var rows = new List<string?[]>(Rows);
        for (var i = 0; i < Rows; i++)
        {
            rows.Add(
            [
                "客戶" + (i % 50),
                (i * 3 % 100000).ToString("N0"),
                (i % 100 / 4.0).ToString("0.##") + "%",
                new DateTime(2024, 1, 1).AddDays(i % 700).ToString("yyyy/MM/dd"),
                "品項" + i,
                (i * 1.5).ToString("0.0"),
                i.ToString(),
                i % 7 == 0 ? null : "備註" + (i % 13),
                (i % 1000).ToString(),
                "x",
            ]);
        }

        var id = _h.AddTable(columns, rows);

        var load = Stopwatch.StartNew();
        var description = await _h.Service.DescribeAsync(id, 5, CancellationToken.None);
        load.Stop();

        var query = Stopwatch.StartNew();
        var result = await _h.Service.QueryAsync(id, "SELECT \"欄位1\", SUM(\"欄位2\"), AVG(\"欄位3\") FROM t GROUP BY \"欄位1\" ORDER BY 2 DESC", 100, CancellationToken.None);
        query.Stop();

        output.WriteLine($"100,000 x 10: first load {load.ElapsedMilliseconds} ms, grouped query {query.ElapsedMilliseconds} ms");
        Assert.Equal(Rows, description.RowCount);
        Assert.Equal(["text", "number", "number", "date"], description.Columns.Take(4).Select(c => c.InferredType));
        Assert.Equal(50, result.Rows.Count);
        Assert.True(load.Elapsed < TimeSpan.FromSeconds(5), $"load took {load.Elapsed}");
        Assert.True(query.Elapsed < TimeSpan.FromMilliseconds(500), $"query took {query.Elapsed}");
    }

    private sealed class ThrowingReader(Exception exception) : ISpreadsheetRegionReader
    {
        public Task<SpreadsheetRegion> ReadAsync(string filePath, string sheet, string cellRange, int headerRowCount, CancellationToken cancellationToken) =>
            Task.FromException<SpreadsheetRegion>(exception);
    }
}
