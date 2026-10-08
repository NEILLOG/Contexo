using Contexo.Core.Abstractions;

namespace Contexo.Core.Storage;

/// <summary>Stub. Implemented by T02.</summary>
internal sealed class SqliteKnowledgeStore : IKnowledgeStore
{
    public Task InitializeAsync(CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task SetMetaAsync(string key, string value, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task<long> GetIndexVersionAsync(CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task<IReadOnlyList<WatchedFolder>> GetFoldersAsync(CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task<WatchedFolder> AddFolderAsync(string path, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task SetFolderExclusionsAsync(long folderId, IReadOnlyList<string> excludedSubfolders, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task SetFolderStateAsync(long folderId, FolderState state, DateTimeOffset? lastScanAt, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task<IReadOnlyList<DocumentRecord>> GetDocumentsAsync(long folderId, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task<IReadOnlyList<DocumentRecord>> GetFailedDocumentsAsync(int limit, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task<DocumentRecord?> GetDocumentByPathAsync(string path, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task ReplaceDocumentAsync(DocumentWrite write, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task MarkDocumentAsync(long folderId, string path, FileFingerprint fingerprint, DocumentStatus status, DocumentErrorCode errorCode, string? errorMessage, DateTimeOffset? nextRetryAt, bool keepExistingChunks, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task DeleteDocumentAsync(long documentId, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task MoveDocumentAsync(long documentId, string newPath, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public IAsyncEnumerable<StoredVector> ReadVectorsAsync(string modelId, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task<IReadOnlyList<KeywordHit>> KeywordSearchAsync(string? ftsQuery, IReadOnlyList<string> likeTerms, int limit, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task<IReadOnlyList<ChunkDetail>> GetChunksAsync(IReadOnlyList<long> chunkIds, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public IAsyncEnumerable<ChunkForEmbedding> ReadChunksMissingVectorAsync(string modelId, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task SaveVectorsAsync(string modelId, IReadOnlyList<StoredVector> vectors, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task<ExcelTableRecord?> GetExcelTableAsync(string tableId, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task<IReadOnlyList<Exclusion>> GetExclusionsAsync(CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task<Exclusion> AddExclusionAsync(string path, bool isFolder, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task RemoveExclusionAsync(long exclusionId, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task RecordMcpActivityAsync(McpActivity activity, CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task<IReadOnlyList<McpClientActivitySummary>> GetMcpActivitySummariesAsync(CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task<StoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken) => throw new NotImplementedException("T02");

    public Task ClearIndexedDataAsync(CancellationToken cancellationToken) => throw new NotImplementedException("T02");
}
