using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Contexo.Mcp.Tools;

/// <summary>What a tool hands back: the text for the AI software, whether it is an error, and (for errors) a content-free note for the activity log.</summary>
internal sealed record ToolOutcome(string Text, bool IsError = false, string? ErrorDetail = null);

/// <summary>
/// The logic behind the three MCP tools, independent of the MCP SDK so it can be tested directly.
/// Expected failures become <see cref="ToolOutcome"/> errors with plain-language text; only cancellation is allowed to escape.
/// Logs and error details carry tool names, ids and exception types only: never query text, SQL or document content.
/// </summary>
internal sealed class ContexoToolService(
    IKnowledgeStore store,
    ISearchService search,
    ITableQueryService tables,
    ILogger<ContexoToolService> logger)
{
    internal const int DefaultTopK = 8;
    internal const int MaxTopK = 20;
    internal const int DefaultMaxRows = 100;
    internal const int MaxRowsLimit = 500;
    internal const int SampleRows = 5;

    internal const string StoreUnavailable = "目前無法讀取 Contexo 的資料。請先開啟 Contexo 確認它能正常執行，稍後再試一次。";
    internal const string SearchFailed = "搜尋時發生問題，請稍後再試一次。";
    internal const string TableFailed = "讀取表格時發生問題，請稍後再試一次。";
    internal const string EmptyQuery = "請提供要搜尋的內容。";
    internal const string EmptyTableId = "請提供 table_id。table_id 會出現在搜尋結果裡（例如 t42）。";
    internal const string EmptySql = "請提供要執行的 SQL（單一 SELECT 查詢，資料表名稱為 t）。";

    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _initialized;

    /// <summary>Creates or migrates the database once. Idempotent; a failure is remembered only until the next call, which tries again.</summary>
    public async Task<bool> EnsureStoreAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return true;
        }

        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return true;
            }

            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("Could not open the Contexo database ({ExceptionType})", ex.GetType().Name);
            return false;
        }
        finally
        {
            _initGate.Release();
        }
    }

    public async Task<ToolOutcome> SearchAsync(string? query, int topK, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new ToolOutcome(EmptyQuery, IsError: true, ErrorDetail: "InvalidArguments：沒有提供搜尋內容");
        }

        if (!await EnsureStoreAsync(cancellationToken).ConfigureAwait(false))
        {
            return StoreUnavailableOutcome();
        }

        try
        {
            var response = await search.SearchAsync(new SearchRequest(query, Math.Clamp(topK, 1, MaxTopK)), cancellationToken).ConfigureAwait(false);
            if (response.Hits.Count == 0)
            {
                return new ToolOutcome(await IsEmptyAsync(cancellationToken).ConfigureAwait(false) ? ResultFormatter.NoData : ResultFormatter.NoResults);
            }

            var hints = await LookUpTableHintsAsync(response.Hits, cancellationToken).ConfigureAwait(false);
            return new ToolOutcome(ResultFormatter.Search(response, hints));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("search failed ({ExceptionType})", ex.GetType().Name);
            return new ToolOutcome(SearchFailed, IsError: true, ErrorDetail: $"{ex.GetType().Name}：{SearchFailed}");
        }
    }

    public async Task<ToolOutcome> DescribeTableAsync(string? tableId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tableId))
        {
            return new ToolOutcome(EmptyTableId, IsError: true, ErrorDetail: "InvalidArguments：沒有提供 table_id");
        }

        return await RunTableToolAsync("describe_table", tableId, async () =>
        {
            var description = await tables.DescribeAsync(tableId, SampleRows, cancellationToken).ConfigureAwait(false);
            return new ToolOutcome(ResultFormatter.Describe(description));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ToolOutcome> QueryTableAsync(string? tableId, string? sql, int maxRows, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tableId))
        {
            return new ToolOutcome(EmptyTableId, IsError: true, ErrorDetail: "InvalidArguments：沒有提供 table_id");
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            return new ToolOutcome(EmptySql, IsError: true, ErrorDetail: "InvalidArguments：沒有提供 SQL");
        }

        var limit = Math.Clamp(maxRows, 1, MaxRowsLimit);
        return await RunTableToolAsync("query_table", tableId, async () =>
        {
            var result = await tables.QueryAsync(tableId, sql, limit, cancellationToken).ConfigureAwait(false);
            return new ToolOutcome(ResultFormatter.Query(result, limit));
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolOutcome> RunTableToolAsync(string tool, string tableId, Func<Task<ToolOutcome>> work, CancellationToken cancellationToken)
    {
        if (!await EnsureStoreAsync(cancellationToken).ConfigureAwait(false))
        {
            return StoreUnavailableOutcome();
        }

        try
        {
            // Tables embedded in another file (a pptx, a docx) are registered by the indexer, but the table service can only
            // reopen real spreadsheet files, so say so plainly instead of letting it fail with a misleading message.
            var record = await store.GetExcelTableAsync(tableId, cancellationToken).ConfigureAwait(false);
            if (record is not null && IsEmbedded(record))
            {
                var fileName = Path.GetFileName(record.FilePath);
                logger.LogInformation("{Tool}: table {TableId} is embedded in another file", tool, tableId);
                return new ToolOutcome(
                    $"這張表格是內嵌在「{fileName}」裡的 Excel 表格，目前無法用 describe_table / query_table 查詢。" +
                    "請改用搜尋結果裡已經顯示的文字內容回答；如果需要完整計算，可以請使用者把內嵌的 Excel 另存成獨立的檔案，放進已加入 Contexo 的資料夾。",
                    IsError: true,
                    ErrorDetail: "EmbeddedTable：內嵌在其他檔案裡的表格無法查詢");
            }

            return await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TableQueryException ex)
        {
            // The message is written for AI software (and may name columns), but may also quote part of the SQL, so it is not logged or recorded.
            logger.LogInformation("{Tool} refused for table {TableId}", tool, tableId);
            if (await IsEmptyAsync(cancellationToken).ConfigureAwait(false))
            {
                return new ToolOutcome(ResultFormatter.NoData, IsError: true, ErrorDetail: "TableQueryException：Contexo 還沒有收錄資料");
            }

            return new ToolOutcome(ex.Message, IsError: true, ErrorDetail: "TableQueryException：查詢被拒絕或表格不存在");
        }
        catch (Exception ex)
        {
            logger.LogError("{Tool} failed for table {TableId} ({ExceptionType})", tool, tableId, ex.GetType().Name);
            return new ToolOutcome(TableFailed, IsError: true, ErrorDetail: $"{ex.GetType().Name}：{TableFailed}");
        }
    }

    // A table is embedded when its key is not the plain "{sheet}!{range}" of a real worksheet, or its file is not a spreadsheet at all.
    internal static bool IsEmbedded(ExcelTableRecord record)
    {
        var table = record.Table;
        if (table.TableKey != $"{table.Sheet}!{table.CellRange}")
        {
            return true;
        }

        var extension = Path.GetExtension(record.FilePath);
        return !(extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csv", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyDictionary<string, bool>> LookUpTableHintsAsync(IReadOnlyList<SearchHit> hits, CancellationToken cancellationToken)
    {
        var hints = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var id in hits.Select(h => h.TableId).Where(id => !string.IsNullOrEmpty(id)).Distinct())
        {
            try
            {
                var record = await store.GetExcelTableAsync(id!, cancellationToken).ConfigureAwait(false);
                if (record is not null)
                {
                    hints[id!] = !IsEmbedded(record);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Without the hint the table is simply offered as queryable.
                logger.LogWarning("Could not look up table {TableId} ({ExceptionType})", id, ex.GetType().Name);
            }
        }

        return hints;
    }

    private async Task<bool> IsEmptyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await store.GetStatisticsAsync(cancellationToken).ConfigureAwait(false)).ChunkCount == 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not read database statistics ({ExceptionType})", ex.GetType().Name);
            return false;
        }
    }

    private static ToolOutcome StoreUnavailableOutcome() =>
        new(StoreUnavailable, IsError: true, ErrorDetail: $"StoreUnavailable：{StoreUnavailable}");
}
