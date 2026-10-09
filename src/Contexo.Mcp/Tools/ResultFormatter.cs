using System.Text;
using Contexo.Core.Abstractions;

namespace Contexo.Mcp.Tools;

/// <summary>Builds the plain-text and Markdown answers handed back to the AI software. All wording is Traditional Chinese.</summary>
internal static class ResultFormatter
{
    internal const int MaxHitChars = 1500;

    internal const string NoResults = "沒有找到相關內容。可以換個說法，或確認檔案所在的資料夾已加入 Contexo。";
    internal const string NoData = "Contexo 還沒有收錄任何資料。請先開啟 Contexo，加入要讓 AI 讀取的資料夾。";
    internal const string DegradedNotice = "（目前只使用關鍵字比對）";

    /// <param name="tableHints">For hits that carry a table id: true when the table can be queried, false when it is embedded in another file.</param>
    public static string Search(SearchResponse response, IReadOnlyDictionary<string, bool> tableHints)
    {
        var builder = new StringBuilder();
        if (response.Degraded)
        {
            builder.AppendLine(DegradedNotice);
        }

        builder.AppendLine($"找到 {response.Hits.Count} 筆相關內容：");

        for (var i = 0; i < response.Hits.Count; i++)
        {
            var hit = response.Hits[i];
            var location = LocationText.Format(hit.Location);

            builder.AppendLine();
            builder.AppendLine(location.Length == 0 ? $"[{i + 1}] {hit.FileName}" : $"[{i + 1}] {hit.FileName} — {location}");
            builder.AppendLine($"路徑：{hit.FilePath}");
            builder.AppendLine("內容：");
            builder.AppendLine(Truncate(hit.Text));

            if (!string.IsNullOrEmpty(hit.TableId))
            {
                if (tableHints.TryGetValue(hit.TableId, out var queryable) && !queryable)
                {
                    builder.AppendLine("（這是內嵌在檔案裡的大型表格，目前無法用 describe_table / query_table 查詢，請依上面的內容回答。）");
                }
                else
                {
                    builder.AppendLine($"table_id: {hit.TableId}（這是大型表格，可用 describe_table / query_table 查詢完整資料）");
                }
            }
        }

        return builder.ToString().TrimEnd();
    }

    internal static string Truncate(string text)
    {
        if (text.Length <= MaxHitChars)
        {
            return text;
        }

        var cut = MaxHitChars;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut] + $"…（內容太長，已截斷，原文共 {text.Length} 字）";
    }

    public static string Describe(TableDescription table)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"表格 {table.TableId}");
        builder.AppendLine($"檔名：{Path.GetFileName(table.FilePath)}");
        builder.AppendLine($"路徑：{table.FilePath}");
        builder.AppendLine($"工作表：{table.Sheet}");
        builder.AppendLine($"範圍：{table.CellRange}");
        builder.AppendLine($"資料列數：{table.RowCount}");
        builder.AppendLine();
        builder.AppendLine("欄位：");
        builder.Append(Markdown(["SQL 名稱", "原欄名", "型別"], table.Columns.Select(c => (IReadOnlyList<string?>)[c.SqlName, c.Header, c.InferredType])));
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine($"前 {table.SampleRows.Count} 列範例：");
        builder.Append(Markdown(table.Columns.Select(c => c.SqlName).ToList(), table.SampleRows));
        builder.AppendLine();
        builder.AppendLine();
        builder.Append($"查詢時資料表名稱為 {table.SqlTableName}，欄名請用雙引號，例如 SELECT \"欄名\" FROM {table.SqlTableName}。");
        return builder.ToString();
    }

    public static string Query(TableQueryResult result, int maxRows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(result.Rows.Count == 0 ? "查詢完成，沒有符合的資料列。" : $"查詢結果共 {result.Rows.Count} 列：");
        builder.AppendLine();
        builder.Append(Markdown(result.Columns, result.Rows));
        if (result.Truncated)
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.Append($"（結果超過 {maxRows} 列，只顯示前 {maxRows} 列。可加上 WHERE、GROUP BY 或聚合函式縮小範圍。）");
        }

        return builder.ToString();
    }

    private static string Markdown(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var builder = new StringBuilder();
        builder.Append("| ").AppendJoin(" | ", headers.Select(Cell)).Append(" |").AppendLine();
        builder.Append("| ").AppendJoin(" | ", headers.Select(_ => "---")).Append(" |");
        foreach (var row in rows)
        {
            builder.AppendLine();
            builder.Append("| ").AppendJoin(" | ", row.Select(Cell)).Append(" |");
        }

        return builder.ToString();
    }

    // A Markdown table cell cannot contain a raw pipe or a line break.
    private static string Cell(string? value) =>
        value is null ? string.Empty : value.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
}
