using Contexo.App.Services;
using Contexo.App.ViewModels;
using Contexo.Core.Abstractions;

namespace Contexo.App.Tests.Search;

/// <summary>A search service the test controls: it can answer at once or wait until the test lets it finish.</summary>
internal sealed class FakeSearchService : ISearchService
{
    private readonly List<(SearchRequest Request, TaskCompletionSource<SearchResponse> Gate, CancellationToken Token)> _calls = [];

    public SearchResponse Response { get; set; } = new([], false);

    public bool Manual { get; set; }

    public Exception? Failure { get; set; }

    public List<SearchRequest> Requests { get; } = [];

    public IReadOnlyList<CancellationToken> Tokens => _calls.Select(c => c.Token).ToList();

    public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (Failure is not null)
        {
            return Task.FromException<SearchResponse>(Failure);
        }

        if (!Manual)
        {
            return Task.FromResult(Response);
        }

        var gate = new TaskCompletionSource<SearchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls.Add((request, gate, cancellationToken));
        cancellationToken.Register(() => gate.TrySetCanceled(cancellationToken));
        return gate.Task;
    }

    public void Complete(int call, SearchResponse response) => _calls[call].Gate.TrySetResult(response);

    public async Task WaitForCallsAsync(int count)
    {
        for (var i = 0; i < 500 && _calls.Count < count; i++)
        {
            await Task.Delay(5);
        }

        Assert.True(_calls.Count >= count, "The search service was not called");
    }
}

/// <summary>Only the two members the search page uses are implemented.</summary>
internal sealed class FakeKnowledgeStore : IKnowledgeStore
{
    public int ChunkCount { get; set; } = 100;

    public List<(string Path, bool IsFolder)> Exclusions { get; } = [];

    public Exception? ExclusionFailure { get; set; }

    public Task<StoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new StoreStatistics(1, ChunkCount == 0 ? 0 : 5, 0, ChunkCount, 0));

    public Task<Exclusion> AddExclusionAsync(string path, bool isFolder, CancellationToken cancellationToken)
    {
        if (ExclusionFailure is not null)
        {
            return Task.FromException<Exclusion>(ExclusionFailure);
        }

        Exclusions.Add((path, isFolder));
        return Task.FromResult(new Exclusion(Exclusions.Count, path, isFolder, DateTimeOffset.UnixEpoch));
    }

    public Task InitializeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetMetaAsync(string key, string value, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<long> GetIndexVersionAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<WatchedFolder>> GetFoldersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<WatchedFolder> AddFolderAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetFolderExclusionsAsync(long folderId, IReadOnlyList<string> excludedSubfolders, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetFolderStateAsync(long folderId, FolderState state, DateTimeOffset? lastScanAt, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<DocumentRecord>> GetDocumentsAsync(long folderId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<DocumentRecord>> GetFailedDocumentsAsync(int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<DocumentRecord?> GetDocumentByPathAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task ReplaceDocumentAsync(DocumentWrite write, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task MarkDocumentAsync(long folderId, string path, FileFingerprint fingerprint, DocumentStatus status, DocumentErrorCode errorCode, string? errorMessage, DateTimeOffset? nextRetryAt, bool keepExistingChunks, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DeleteDocumentAsync(long documentId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task MoveDocumentAsync(long documentId, string newPath, CancellationToken cancellationToken) => throw new NotSupportedException();

    public IAsyncEnumerable<StoredVector> ReadVectorsAsync(string modelId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<KeywordHit>> KeywordSearchAsync(string? ftsQuery, IReadOnlyList<string> likeTerms, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<ChunkDetail>> GetChunksAsync(IReadOnlyList<long> chunkIds, CancellationToken cancellationToken) => throw new NotSupportedException();

    public IAsyncEnumerable<ChunkForEmbedding> ReadChunksMissingVectorAsync(string modelId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SaveVectorsAsync(string modelId, IReadOnlyList<StoredVector> vectors, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ExcelTableRecord?> GetExcelTableAsync(string tableId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<Exclusion>> GetExclusionsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task RemoveExclusionAsync(long exclusionId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task RecordMcpActivityAsync(McpActivity activity, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<McpClientActivitySummary>> GetMcpActivitySummariesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task ClearIndexedDataAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class FakeShellLauncher : IShellLauncher
{
    public List<string> Opened { get; } = [];

    public List<string> Revealed { get; } = [];

    public bool Throw { get; set; }

    public void OpenFile(string path)
    {
        if (Throw)
        {
            throw new InvalidOperationException("no program");
        }

        Opened.Add(path);
    }

    public void RevealInFileManager(string path) => Revealed.Add(path);

    public void OpenFolder(string path)
    {
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public bool Answer { get; set; } = true;

    public List<ConfirmRequest> Requests { get; } = [];

    public Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        Requests.Add(request);
        return Task.FromResult(Answer);
    }

    public Task ShowAsync(object dialogViewModel) => Task.CompletedTask;
}

internal sealed class FakeNavigation : INavigationService
{
    public List<Type> Visited { get; } = [];

    public void NavigateTo<TViewModel>() where TViewModel : ViewModelBase => Visited.Add(typeof(TViewModel));
}
