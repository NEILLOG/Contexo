namespace Contexo.Core.Abstractions;

[Flags]
public enum MatchKinds
{
    None = 0,
    Semantic = 1,
    Keyword = 2,
}

/// <param name="PathPrefixes">Optional filter: only files under these folders.</param>
public sealed record SearchRequest(string Query, int TopK = 8, IReadOnlyList<string>? PathPrefixes = null);

/// <param name="Score">Fused score, 0..1, higher is better. Only comparable within one response.</param>
/// <param name="TableId">Set when the hit is a large-table summary; pass to describe_table / query_table.</param>
public sealed record SearchHit(
    long ChunkId,
    string FilePath,
    string FileName,
    SectionKind Kind,
    string Text,
    SourceLocation Location,
    double Score,
    MatchKinds MatchedBy,
    string? TableId);

/// <param name="Degraded">True when semantic search was unavailable and only keyword results were used.</param>
public sealed record SearchResponse(IReadOnlyList<SearchHit> Hits, bool Degraded);

public interface ISearchService
{
    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
}

public sealed record TableColumn(string SqlName, string Header, string InferredType);

/// <param name="SqlTableName">Always "t": the name to use in FROM clauses.</param>
public sealed record TableDescription(
    string TableId,
    string FilePath,
    string Sheet,
    string CellRange,
    string SqlTableName,
    IReadOnlyList<TableColumn> Columns,
    int RowCount,
    IReadOnlyList<IReadOnlyList<string?>> SampleRows);

public sealed record TableQueryResult(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows, bool Truncated);

/// <summary>Thrown for invalid or non-read-only SQL, unknown table ids, or source files that moved. Message is safe to show an AI client.</summary>
public sealed class TableQueryException(string message, Exception? inner = null) : Exception(message, inner);

public interface ITableQueryService
{
    Task<TableDescription> DescribeAsync(string tableId, int sampleRows, CancellationToken cancellationToken);
    /// <summary>Runs a single read-only SELECT against an in-memory SQLite copy of the table.</summary>
    Task<TableQueryResult> QueryAsync(string tableId, string sql, int maxRows, CancellationToken cancellationToken);
}
