namespace Contexo.Core.Abstractions;

public enum FolderState
{
    Active,
    /// <summary>The folder could not be enumerated (OneDrive signed out, drive disconnected). Data is kept, nothing is deleted.</summary>
    Unavailable,
    /// <summary>Waiting for the user to confirm a mass deletion.</summary>
    AwaitingDeletionConfirmation,
}

public enum DocumentStatus
{
    Indexed,
    Failed,
    /// <summary>Skipped by rule (too large, unsupported); not an error.</summary>
    Skipped,
}

/// <param name="ExcludedSubfolders">Paths relative to <see cref="Path"/>, using '/' separators, unticked in the subfolder picker.</param>
public sealed record WatchedFolder(
    long Id,
    string Path,
    string DisplayName,
    IReadOnlyList<string> ExcludedSubfolders,
    FolderState State,
    DateTimeOffset AddedAt,
    DateTimeOffset? LastScanAt);

/// <param name="ContentHash">Lower-case hex SHA-256 of the file bytes.</param>
public sealed record FileFingerprint(long SizeBytes, DateTimeOffset LastWriteUtc, string ContentHash);

public sealed record DocumentRecord(
    long Id,
    long FolderId,
    string Path,
    FileFingerprint Fingerprint,
    DocumentStatus Status,
    DocumentErrorCode ErrorCode,
    string? ErrorMessage,
    int ChunkCount,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? NextRetryAt);

public sealed record ChunkWrite(Chunk Chunk, float[]? Vector);

/// <summary>Everything about one file, written in a single transaction that replaces any previous data for <see cref="Path"/>.</summary>
/// <param name="ModelId">Model of the vectors; required when any <see cref="ChunkWrite.Vector"/> is non-null.</param>
public sealed record DocumentWrite(
    long FolderId,
    string Path,
    FileFingerprint Fingerprint,
    string? ModelId,
    IReadOnlyList<ChunkWrite> Chunks,
    IReadOnlyList<SpreadsheetTable> Tables);

public sealed record StoredVector(long ChunkId, float[] Vector);

/// <summary>A chunk that has no vector for a given model yet (model changed, or the model was unavailable when it was indexed).</summary>
public sealed record ChunkForEmbedding(long ChunkId, string EmbeddingText);

/// <param name="Rank">bm25 rank or fallback score; higher is better.</param>
public sealed record KeywordHit(long ChunkId, double Rank);

public sealed record ChunkDetail(
    long ChunkId,
    long DocumentId,
    string FilePath,
    SectionKind Kind,
    string Text,
    SourceLocation Location,
    string? TableId);

/// <param name="TableId">Opaque id handed to AI clients, e.g. "t42".</param>
public sealed record ExcelTableRecord(
    string TableId,
    long DocumentId,
    string FilePath,
    SpreadsheetTable Table);

public sealed record Exclusion(long Id, string Path, bool IsFolder, DateTimeOffset CreatedAt);

public enum McpEventKind
{
    /// <summary>An MCP client completed initialize.</summary>
    Connected,
    /// <summary>A search / describe_table / query_table call.</summary>
    ToolCall,
    Error,
}

/// <param name="ClientName">clientInfo.name exactly as sent by the client.</param>
public sealed record McpActivity(McpEventKind Kind, string ClientName, string? ClientVersion, string? ToolName, string? Detail, DateTimeOffset At);

public sealed record McpClientActivitySummary(string ClientName, string? ClientVersion, DateTimeOffset? LastConnectedAt, DateTimeOffset? LastToolCallAt, DateTimeOffset? LastErrorAt, string? LastError);

public sealed record StoreStatistics(int FolderCount, int DocumentCount, int FailedDocumentCount, int ChunkCount, long DatabaseBytes);

/// <summary>
/// The single SQLite database shared by Contexo.Desktop (writer) and Contexo.Mcp (reader).
/// All methods are safe to call from multiple threads and from both processes at once (WAL mode).
/// </summary>
public interface IKnowledgeStore
{
    /// <summary>Creates or migrates the schema, enables WAL and foreign keys. Idempotent.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken);
    Task SetMetaAsync(string key, string value, CancellationToken cancellationToken);

    /// <summary>Monotonic counter bumped by every write that changes chunks or vectors. Readers use it to refresh caches.</summary>
    Task<long> GetIndexVersionAsync(CancellationToken cancellationToken);

    // Folders
    Task<IReadOnlyList<WatchedFolder>> GetFoldersAsync(CancellationToken cancellationToken);
    Task<WatchedFolder> AddFolderAsync(string path, CancellationToken cancellationToken);
    /// <summary>Removes the folder and every document, chunk, vector and table under it. Never touches files on disk.</summary>
    Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken);
    Task SetFolderExclusionsAsync(long folderId, IReadOnlyList<string> excludedSubfolders, CancellationToken cancellationToken);
    Task SetFolderStateAsync(long folderId, FolderState state, DateTimeOffset? lastScanAt, CancellationToken cancellationToken);

    // Documents
    Task<IReadOnlyList<DocumentRecord>> GetDocumentsAsync(long folderId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DocumentRecord>> GetFailedDocumentsAsync(int limit, CancellationToken cancellationToken);
    Task<DocumentRecord?> GetDocumentByPathAsync(string path, CancellationToken cancellationToken);
    Task ReplaceDocumentAsync(DocumentWrite write, CancellationToken cancellationToken);
    /// <summary>
    /// Records a status without new content.
    /// When <paramref name="keepExistingChunks"/> is true, existing chunks and vectors stay and the row's fingerprint, status, error and retry time are updated
    /// (used for "content unchanged" and "file temporarily locked" — pass the old fingerprint for the latter so the next scan retries it).
    /// When false, existing chunks, vectors and tables are deleted and only the status row remains.
    /// </summary>
    Task MarkDocumentAsync(long folderId, string path, FileFingerprint fingerprint, DocumentStatus status, DocumentErrorCode errorCode, string? errorMessage, DateTimeOffset? nextRetryAt, bool keepExistingChunks, CancellationToken cancellationToken);
    Task DeleteDocumentAsync(long documentId, CancellationToken cancellationToken);
    /// <summary>Rename/move with identical content: updates the path without touching chunks or vectors.</summary>
    Task MoveDocumentAsync(long documentId, string newPath, CancellationToken cancellationToken);

    // Retrieval
    IAsyncEnumerable<StoredVector> ReadVectorsAsync(string modelId, CancellationToken cancellationToken);
    /// <param name="ftsQuery">An FTS5 MATCH expression already built by the search service.</param>
    /// <param name="likeTerms">Short terms (fewer than 3 characters) matched with LIKE as a fallback.</param>
    Task<IReadOnlyList<KeywordHit>> KeywordSearchAsync(string? ftsQuery, IReadOnlyList<string> likeTerms, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChunkDetail>> GetChunksAsync(IReadOnlyList<long> chunkIds, CancellationToken cancellationToken);

    // Re-embedding without re-parsing
    /// <summary>Streams chunks that have no vector for <paramref name="modelId"/>, in chunk id order.</summary>
    IAsyncEnumerable<ChunkForEmbedding> ReadChunksMissingVectorAsync(string modelId, CancellationToken cancellationToken);
    /// <summary>Inserts or replaces vectors in one transaction. Ignores chunk ids that no longer exist.</summary>
    Task SaveVectorsAsync(string modelId, IReadOnlyList<StoredVector> vectors, CancellationToken cancellationToken);

    // Spreadsheet tables
    Task<ExcelTableRecord?> GetExcelTableAsync(string tableId, CancellationToken cancellationToken);

    // Exclusions (right-click "don't let AI read this")
    Task<IReadOnlyList<Exclusion>> GetExclusionsAsync(CancellationToken cancellationToken);
    /// <summary>Adds the exclusion and deletes the matching documents' data in the same transaction.</summary>
    Task<Exclusion> AddExclusionAsync(string path, bool isFolder, CancellationToken cancellationToken);
    Task RemoveExclusionAsync(long exclusionId, CancellationToken cancellationToken);

    // MCP activity
    Task RecordMcpActivityAsync(McpActivity activity, CancellationToken cancellationToken);
    Task<IReadOnlyList<McpClientActivitySummary>> GetMcpActivitySummariesAsync(CancellationToken cancellationToken);

    // Maintenance
    Task<StoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken);
    /// <summary>Deletes all documents, chunks, vectors, tables and MCP activity; keeps folders, exclusions and meta. Then VACUUMs.</summary>
    Task ClearIndexedDataAsync(CancellationToken cancellationToken);
}
