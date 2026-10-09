using System.ComponentModel;
using Contexo.Mcp.Activity;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Contexo.Mcp.Tools;

/// <summary>
/// The three MCP tools. Descriptions are in English so every model understands when to use them; the answers are Traditional Chinese text
/// that the model relays in the user's own language. Parameter names are snake_case because they become the JSON schema property names.
/// </summary>
[McpServerToolType]
internal sealed class ContexoTools(ContexoToolService service, McpActivityRecorder recorder)
{
    [McpServerTool(Name = "search", Title = "Search the user's documents", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Search the documents on the user's own computer that Contexo has indexed: Word, PowerPoint, Excel, PDF and text files. " +
        "Use it whenever the user asks about their own files or company material, for example quotations, specifications, meeting minutes, contracts, reports or slides. " +
        "Each result lists the file name, its location inside the file (page, slide, worksheet and range, or section) and the full path, " +
        "so cite the file name and location when you answer. " +
        "If a result carries a table_id, it is a large spreadsheet table: use describe_table and query_table to calculate over its complete data instead of guessing from the excerpt. " +
        "Reply to the user in the language they use.")]
    public async Task<CallToolResult> Search(
        McpServer server,
        [Description("What to look for, in natural language or keywords (file names, product names, numbers). Chinese and English both work.")] string query,
        [Description("How many results to return, 1 to 20. Default 8.")] int top_k = ContexoToolService.DefaultTopK,
        CancellationToken cancellationToken = default) =>
        await RunAsync(server, "search", () => service.SearchAsync(query, top_k, cancellationToken), cancellationToken).ConfigureAwait(false);

    [McpServerTool(Name = "describe_table", Title = "Describe a large spreadsheet table", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Show the structure of a large spreadsheet table found by search (a result with a table_id): file, worksheet, cell range, row count, " +
        "the columns with their SQL names, original headers and types, and the first 5 rows. " +
        "Call it before query_table so you know the exact column names. Reply to the user in the language they use.")]
    public async Task<CallToolResult> DescribeTable(
        McpServer server,
        [Description("The table_id shown in a search result, for example t42.")] string table_id,
        CancellationToken cancellationToken = default) =>
        await RunAsync(server, "describe_table", () => service.DescribeTableAsync(table_id, cancellationToken), cancellationToken).ConfigureAwait(false);

    [McpServerTool(Name = "query_table", Title = "Query a large spreadsheet table with SQL", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Run a read-only SQL query over one large spreadsheet table found by search (a result with a table_id). " +
        "Only a single SELECT statement is allowed, the table is always named t, and column names must be written in double quotes, " +
        "for example SELECT \"Customer\", SUM(\"Amount\") FROM t GROUP BY \"Customer\". " +
        "Use it for sums, counts, averages, filtering and sorting that the search excerpt cannot answer. " +
        "Call describe_table first to get the exact column names. The result is a Markdown table. Reply to the user in the language they use.")]
    public async Task<CallToolResult> QueryTable(
        McpServer server,
        [Description("The table_id shown in a search result, for example t42.")] string table_id,
        [Description("A single SELECT statement. The table name is t; quote column names with double quotes.")] string sql,
        [Description("Maximum number of rows to return, 1 to 500. Default 100.")] int max_rows = ContexoToolService.DefaultMaxRows,
        CancellationToken cancellationToken = default) =>
        await RunAsync(server, "query_table", () => service.QueryTableAsync(table_id, sql, max_rows, cancellationToken), cancellationToken).ConfigureAwait(false);

    private async Task<CallToolResult> RunAsync(McpServer server, string tool, Func<Task<ToolOutcome>> work, CancellationToken cancellationToken)
    {
        // The initialized notification normally records "connected" first; this covers a client that skips it.
        await recorder.RecordConnectedAsync(server.ClientInfo).ConfigureAwait(false);
        await recorder.RecordToolCallAsync(server.ClientInfo, tool).ConfigureAwait(false);

        var outcome = await work().ConfigureAwait(false);
        if (outcome.IsError)
        {
            await recorder.RecordErrorAsync(server.ClientInfo, tool, outcome.ErrorDetail ?? "Error").ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new CallToolResult
        {
            IsError = outcome.IsError ? true : null,
            Content = [new TextContentBlock { Text = outcome.Text }],
        };
    }
}
