using System.Globalization;
using System.Text;
using Contexo.Core.Abstractions;
using Xunit.Abstractions;

namespace Contexo.Core.Tests.EndToEnd;

/// <summary>
/// The whole path (parse, chunk, embed, store, search, SQL) on the generated 40-file corpus of a fictional company.
/// Run with <c>dotnet test --filter Category=EndToEnd</c>. Retrieval quality is only gated when an embedding model is present
/// (<c>bash tools/download-models.sh</c>); without one the numbers are printed for information.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class CorpusEndToEndTests(CorpusFixture corpus, ITestOutputHelper output) : IClassFixture<CorpusFixture>
{
    private const double RecallAt3Bar = 0.8;

    [Fact]
    public async Task Every_file_is_read_without_failures_and_every_kind_of_file_has_chunks()
    {
        var documents = await corpus.Store.GetDocumentsAsync(corpus.FolderId, CancellationToken.None);
        var files = Directory.GetFiles(corpus.CorpusDirectory);
        output.WriteLine($"{documents.Count} files, first indexing run {corpus.InitialIndexTime.TotalSeconds:0.0} s, model available: {corpus.ModelAvailable}");

        Assert.Equal(files.Length, documents.Count);
        Assert.Equal(40, documents.Count);
        var problems = documents.Where(d => d.Status != DocumentStatus.Indexed || d.ChunkCount == 0)
            .Select(d => $"{Path.GetFileName(d.Path)}: {d.Status} {d.ErrorCode} {d.ErrorMessage} chunks={d.ChunkCount}")
            .ToList();
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        var byExtension = documents.GroupBy(d => Path.GetExtension(d.Path).ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(10, byExtension[".docx"]);
        Assert.Equal(8, byExtension[".pptx"]);
        Assert.Equal(10, byExtension[".xlsx"]);
        Assert.Equal(5, byExtension[".pdf"]);
        Assert.Equal(2, byExtension[".txt"]);
        Assert.Equal(1, byExtension[".md"]);
        Assert.Equal(2, byExtension[".html"]);
        Assert.Equal(2, byExtension[".csv"]);

        var statistics = await corpus.Store.GetStatisticsAsync(CancellationToken.None);
        Assert.Equal(0, statistics.FailedDocumentCount);
        Assert.True(statistics.ChunkCount >= 100, "chunk count " + statistics.ChunkCount);
    }

    [Fact]
    public void Parsers_keep_what_matters_and_leave_out_what_does_not()
    {
        string TextOf(string fileNamePart) => string.Join("\n", corpus
            .Sql($"SELECT c.text FROM chunks c JOIN documents d ON d.id = c.document_id WHERE d.path LIKE '%{fileNamePart}%' ORDER BY c.ordinal")
            .Select(r => (string)r[0]!));

        // Tracked changes: the inserted figure stays, the deleted one is gone.
        var minutes = TextOf("會議紀錄_2025Q3");
        Assert.Contains("台中案預算由 420 萬元核定", minutes);
        Assert.DoesNotContain("380", minutes);

        // Footnotes are part of the text.
        Assert.Contains("保固範圍不含人為損壞", TextOf("服務合約範本"));

        // Big5 text files are decoded properly.
        Assert.Contains("停車證自 11 月 1 日起換發新證", TextOf("舊版公告_停車證換發"));

        // A docx embedded in a pptx is read, and remembers where it came from.
        var embedded = corpus.Sql("SELECT c.text, c.location FROM chunks c JOIN documents d ON d.id = c.document_id WHERE d.path LIKE '%內嵌docx的簡報%' AND c.location LIKE '%EmbeddedPath%'");
        Assert.Contains(embedded, r => ((string)r[0]!).Contains("測試環境帳號每月 5 日重設"));

        // Speaker notes and chart values.
        var review = TextOf("2025年度簡報");
        Assert.Contains("毛利率 32%", review);
        Assert.Contains("167", review);

        // Connector lines of the flow chart and the SmartArt hierarchy.
        Assert.Contains("[部門主管審核] --> [採購部詢價比價]", TextOf("請購流程"));
        Assert.Contains("資訊部", TextOf("組織架構"));

        // PDF: the running header and the "Page N of M" footer are not part of the content.
        var pdfText = string.Join("\n", corpus.Sql("SELECT c.text FROM chunks c JOIN documents d ON d.id = c.document_id WHERE d.path LIKE '%.pdf'").Select(r => (string)r[0]!));
        Assert.Contains("Fire drills are held twice a year", pdfText);
        Assert.DoesNotContain("Internal Regulation", pdfText);
        Assert.DoesNotMatch(@"Page \d+ of \d+", pdfText);

        // Master slide, layout, notes master and SmartArt layout points must never leak into the index.
        foreach (var canary in new[] { "母片頁尾機密文字", "母片公司名稱", "版面配置頁尾文字", "備忘稿母片文字", "版面點不該出現" })
        {
            Assert.Empty(corpus.Sql($"SELECT c.id FROM chunks c WHERE c.text LIKE '%{canary}%'"));
        }

        // Large tables are registered for SQL: customer list, sales detail, order export and the workbook embedded in the proposal.
        var tables = corpus.Sql("SELECT d.path FROM excel_tables t JOIN documents d ON d.id = t.document_id").Select(r => Path.GetFileName((string)r[0]!)).ToList();
        Assert.Contains("客戶清單.xlsx", tables);
        Assert.Contains("銷售明細.xlsx", tables);
        Assert.Contains("客戶訂單匯出.csv", tables);
        Assert.Contains("智慧監控提案書.docx", tables);
    }

    [Fact]
    public async Task Retrieval_quality_on_the_generated_queries()
    {
        var outcomes = new List<(QueryCase Query, int? Rank, bool LocationMatched, string? TopFile)>();
        foreach (var query in corpus.Queries)
        {
            var response = await corpus.SearchAsync(query.Query);
            Assert.Equal(!corpus.ModelAvailable, response.Degraded);
            int? rank = null;
            var locationMatched = false;
            for (var i = 0; i < response.Hits.Count; i++)
            {
                var hit = response.Hits[i];
                if (!query.ExpectedFiles.Contains(hit.FileName))
                {
                    continue;
                }

                rank ??= i + 1;
                locationMatched |= query.ExpectedLocation is { } wanted && Describe(hit.Location).Contains(wanted, StringComparison.OrdinalIgnoreCase);
            }

            outcomes.Add((query, rank, locationMatched, response.Hits.FirstOrDefault()?.FileName));
        }

        var gate = outcomes.Where(o => !o.Query.ObserveOnly).ToList();
        double RecallAt(int k) => gate.Count(o => o.Rank is { } r && r <= k) / (double)gate.Count;
        var recall3 = RecallAt(3);
        var mrr = gate.Average(o => o.Rank is { } r ? 1.0 / r : 0);

        var report = new StringBuilder();
        report.AppendLine($"Model: {(corpus.ModelAvailable ? corpus.Embedding.ModelId : "none (keyword only)")}");
        report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Queries (gated): {gate.Count}  Recall@1 {RecallAt(1):0.00}  Recall@3 {recall3:0.00}  Recall@5 {RecallAt(5):0.00}  MRR {mrr:0.000}"));
        foreach (var o in outcomes)
        {
            report.AppendLine($"{o.Query.Id} [{o.Query.Kind}{(o.Query.ObserveOnly ? ", observe" : "")}] \"{o.Query.Query}\" -> rank {(o.Rank?.ToString() ?? "miss")} (top: {o.TopFile})");
        }

        output.WriteLine(report.ToString());

        if (!corpus.ModelAvailable)
        {
            output.WriteLine("No embedding model: numbers are recorded only (run tools/download-models.sh to enable the Recall@3 bar).");
            return;
        }

        var missed = gate.Where(o => o.Rank is null or > 3).Select(o => $"{o.Query.Id} \"{o.Query.Query}\" (rank {(o.Rank?.ToString() ?? "miss")})");
        Assert.True(recall3 >= RecallAt3Bar, $"Recall@3 {recall3:0.00} is below {RecallAt3Bar:0.0}. Missed: {string.Join("; ", missed)}");

        // Where a location was expected and the file was found in the top 3, the location must be the right one.
        var wrongLocation = gate.Where(o => o.Query.ExpectedLocation is not null && o.Rank is <= 3 && !o.LocationMatched).Select(o => o.Query.Id);
        Assert.Empty(wrongLocation);
    }

    [Fact]
    public async Task Table_queries_return_a_table_id_and_the_sql_answers_match_the_generator()
    {
        foreach (var query in corpus.Queries.Where(q => q.NeedsTable && !q.ObserveOnly))
        {
            var tableId = await TableIdForAsync(query);
            Assert.True(tableId is not null, $"{query.Id} \"{query.Query}\": no hit of {string.Join("/", query.ExpectedFiles)} carries a TableId");
        }

        var expected = corpus.Expected;

        // Sales detail: total, and the customer with the highest amount.
        var salesId = await TableIdAsync("q43", expected.SalesFile);
        var sales = await corpus.Tables.DescribeAsync(salesId, 3, CancellationToken.None);
        Assert.Equal(expected.SalesRowCount, sales.RowCount);
        var amount = Column(sales, "金額");
        var customer = Column(sales, "客戶");

        var total = await corpus.Tables.QueryAsync(salesId, $"SELECT SUM(\"{amount}\") FROM t", 10, CancellationToken.None);
        Assert.Equal(expected.SalesTotalAmount, ParseNumber(total.Rows.Single()[0]));

        var top = await corpus.Tables.QueryAsync(salesId, $"SELECT \"{customer}\", SUM(\"{amount}\") AS total FROM t GROUP BY \"{customer}\" ORDER BY total DESC LIMIT 1", 10, CancellationToken.None);
        Assert.Equal(expected.SalesTopCustomer, top.Rows.Single()[0]);
        Assert.Equal(expected.SalesTopCustomerAmount, ParseNumber(top.Rows.Single()[1]));

        // Customer list: customers in the north.
        var customersId = await TableIdAsync("q24", expected.CustomerListFile);
        var customers = await corpus.Tables.DescribeAsync(customersId, 3, CancellationToken.None);
        Assert.Equal(expected.CustomerListRowCount, customers.RowCount);
        var region = Column(customers, "地區");
        var north = await corpus.Tables.QueryAsync(customersId, $"SELECT COUNT(*) FROM t WHERE \"{region}\" = '北部'", 10, CancellationToken.None);
        Assert.Equal(expected.CustomerListNorthCount, ParseNumber(north.Rows.Single()[0]));

        // CSV export: shipped orders.
        var ordersId = await TableIdAsync("q41", expected.OrderCsvFile);
        var orders = await corpus.Tables.DescribeAsync(ordersId, 3, CancellationToken.None);
        Assert.Equal(expected.OrderCsvRowCount, orders.RowCount);
        var status = Column(orders, "狀態");
        var shipped = await corpus.Tables.QueryAsync(ordersId, $"SELECT COUNT(*) FROM t WHERE \"{status}\" = '已出貨'", 10, CancellationToken.None);
        Assert.Equal(expected.OrderCsvShippedCount, ParseNumber(shipped.Rows.Single()[0]));
    }

    [Fact]
    public async Task A_table_embedded_in_another_file_is_found_but_does_not_crash_the_table_service()
    {
        // Known gap (task/README.md, decision A-1): the workbook inside the proposal is registered as a table,
        // but the table service reads tables from disk paths only. This test records what happens today and
        // only insists that the failure is a plain TableQueryException (never a crash).
        var response = await corpus.SearchAsync("PTZ-2000 球型攝影機的單價");
        var hit = response.Hits.FirstOrDefault(h => h.FileName == "智慧監控提案書.docx" && h.TableId is not null);
        output.WriteLine("Search hit with TableId in the proposal: " + (hit is null ? "none" : hit.TableId));
        if (hit is null)
        {
            return;
        }

        try
        {
            var description = await corpus.Tables.DescribeAsync(hit.TableId!, 3, CancellationToken.None);
            output.WriteLine($"describe_table works: {description.RowCount} rows");
            var query = await corpus.Tables.QueryAsync(hit.TableId!, "SELECT COUNT(*) FROM t", 10, CancellationToken.None);
            output.WriteLine("query_table works: " + query.Rows.Single()[0]);
        }
        catch (TableQueryException ex)
        {
            output.WriteLine("describe_table / query_table on an embedded table: " + ex.Message);
        }
    }

    private async Task<string> TableIdAsync(string queryId, string file)
    {
        var query = corpus.Queries.Single(q => q.Id == queryId);
        Assert.Contains(file, query.ExpectedFiles);
        return await TableIdForAsync(query) ?? throw new Xunit.Sdk.XunitException($"{queryId}: no TableId for {file}");
    }

    private async Task<string?> TableIdForAsync(QueryCase query)
    {
        var response = await corpus.SearchAsync(query.Query);
        return response.Hits.FirstOrDefault(h => query.ExpectedFiles.Contains(h.FileName) && h.TableId is not null)?.TableId;
    }

    private static string Column(TableDescription description, string header) =>
        description.Columns.Single(c => c.Header == header).SqlName;

    private static long ParseNumber(string? text) =>
        (long)Math.Round(double.Parse(text ?? throw new Xunit.Sdk.XunitException("SQL returned NULL"), CultureInfo.InvariantCulture));

    private static string Describe(SourceLocation location)
    {
        var parts = new List<string>();
        if (location.HeadingPath is { Count: > 0 } path)
        {
            parts.AddRange(path);
        }

        parts.AddRange(new[] { location.Title, location.Sheet }.Where(p => p is not null)!);
        return string.Join(" / ", parts);
    }
}
