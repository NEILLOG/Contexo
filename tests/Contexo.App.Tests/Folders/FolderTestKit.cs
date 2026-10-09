using Contexo.App.Folders;
using Contexo.App.Services;
using Contexo.App.Shell;
using Contexo.App.Tests.Shell;
using Contexo.App.ViewModels;
using Contexo.Core.Abstractions;

namespace Contexo.App.Tests.Folders;

/// <summary>In-memory knowledge store that implements only what the folder screens use. Every call is appended to <see cref="Calls"/>.</summary>
internal sealed class FolderTestStore : IKnowledgeStore
{
    private long _nextId = 100;

    public List<string> Calls { get; } = [];

    public List<WatchedFolder> Folders { get; } = [];

    public List<DocumentRecord> Failed { get; } = [];

    public List<(string Path, bool IsFolder)> Exclusions { get; } = [];

    public Dictionary<long, IReadOnlyList<string>> SavedExclusions { get; } = [];

    public WatchedFolder Add(string path, string? name = null, FolderState state = FolderState.Active, IReadOnlyList<string>? excluded = null, bool scanned = true)
    {
        var folder = new WatchedFolder(
            _nextId++,
            path,
            name ?? PathRelations.GetName(path),
            excluded ?? [],
            state,
            DateTimeOffset.UnixEpoch,
            scanned ? DateTimeOffset.UnixEpoch : null);
        Folders.Add(folder);
        return folder;
    }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<WatchedFolder>> GetFoldersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WatchedFolder>>(Folders.ToList());

    public Task<WatchedFolder> AddFolderAsync(string path, CancellationToken cancellationToken)
    {
        Calls.Add("AddFolder:" + path);
        return Task.FromResult(Add(path, scanned: false));
    }

    public Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken)
    {
        Calls.Add("RemoveFolder:" + folderId);
        Folders.RemoveAll(f => f.Id == folderId);
        return Task.CompletedTask;
    }

    public Task SetFolderExclusionsAsync(long folderId, IReadOnlyList<string> excludedSubfolders, CancellationToken cancellationToken)
    {
        Calls.Add("SetExclusions:" + folderId);
        SavedExclusions[folderId] = excludedSubfolders.ToList();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DocumentRecord>> GetFailedDocumentsAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DocumentRecord>>(Failed.OrderByDescending(d => d.UpdatedAt).Take(limit).ToList());

    public Task<Exclusion> AddExclusionAsync(string path, bool isFolder, CancellationToken cancellationToken)
    {
        Calls.Add("AddExclusion:" + path);
        Exclusions.Add((path, isFolder));
        Failed.RemoveAll(d => PathRelations.AreSame(d.Path, path));
        return Task.FromResult(new Exclusion(1, path, isFolder, DateTimeOffset.UnixEpoch));
    }

    public DocumentRecord AddFailed(long id, long folderId, string path, DocumentErrorCode code)
    {
        var doc = new DocumentRecord(
            id,
            folderId,
            path,
            new FileFingerprint(1, DateTimeOffset.UnixEpoch, "00"),
            DocumentStatus.Failed,
            code,
            null,
            0,
            DateTimeOffset.UnixEpoch.AddMinutes(id),
            null);
        Failed.Add(doc);
        return doc;
    }

    public Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetMetaAsync(string key, string value, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<long> GetIndexVersionAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetFolderStateAsync(long folderId, FolderState state, DateTimeOffset? lastScanAt, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<DocumentRecord>> GetDocumentsAsync(long folderId, CancellationToken cancellationToken) => throw new NotSupportedException();

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

    public Task<StoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task ClearIndexedDataAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class FolderTestIndexing(List<string> calls) : IIndexingService
{
    public IndexingSnapshot Current { get; private set; } = IndexingSnapshot.Initial;

    public event EventHandler<IndexingSnapshot>? SnapshotChanged;

    public event EventHandler<MassDeletionPending>? MassDeletionPendingRaised;

    public List<(long FolderId, bool DeleteMissing)> Resolved { get; } = [];

    public void Raise(IndexingSnapshot snapshot)
    {
        Current = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    public void RaiseMassDeletion(MassDeletionPending pending) => MassDeletionPendingRaised?.Invoke(this, pending);

    public int SnapshotSubscribers => SnapshotChanged?.GetInvocationList().Length ?? 0;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Pause()
    {
        calls.Add("Pause");
        Current = Current with { State = IndexingState.Paused };
    }

    public void Resume()
    {
        calls.Add("Resume");
        Current = Current with { State = IndexingState.Indexing };
    }

    public void RequestRescan(long? folderId) => calls.Add("RequestRescan:" + (folderId?.ToString() ?? "all"));

    public void RequestRetry(long? documentId) => calls.Add("RequestRetry:" + (documentId?.ToString() ?? "all"));

    public Task ResolveMassDeletionAsync(long folderId, bool deleteMissing, CancellationToken cancellationToken)
    {
        calls.Add($"Resolve:{folderId}:{deleteMissing}");
        Resolved.Add((folderId, deleteMissing));
        return Task.CompletedTask;
    }
}

/// <summary>Answers confirmation dialogs from a queue (false when the queue is empty) and remembers what was asked.</summary>
internal sealed class ScriptedDialogs : IDialogService
{
    public Queue<bool> Answers { get; } = new();

    public List<ConfirmRequest> Requests { get; } = [];

    public List<object> Shown { get; } = [];

    public Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        Requests.Add(request);
        return Task.FromResult(Answers.Count > 0 && Answers.Dequeue());
    }

    public Task ShowAsync(object dialogViewModel)
    {
        Shown.Add(dialogViewModel);
        return Task.CompletedTask;
    }
}

internal sealed class FolderTestPicker : IFolderPicker
{
    public string? Result { get; set; }

    public Task<string?> PickFolderAsync(string? initialPath) => Task.FromResult(Result);
}

internal sealed class FolderTestLauncher : IShellLauncher
{
    public List<string> Calls { get; } = [];

    public void OpenFile(string path) => Calls.Add("OpenFile:" + path);

    public void RevealInFileManager(string path) => Calls.Add("Reveal:" + path);

    public void OpenFolder(string path) => Calls.Add("OpenFolder:" + path);
}

internal sealed class FolderTestNavigation : INavigationService
{
    public List<Type> Targets { get; } = [];

    public void NavigateTo<TViewModel>() where TViewModel : ViewModelBase => Targets.Add(typeof(TViewModel));
}

/// <summary>A fake file system for the sub folder tree: keys are normalized folder paths, values the child folder names.</summary>
internal sealed class FolderTestTree : IFolderTreeReader
{
    public Dictionary<string, string[]> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Missing { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string path, params string[] children) => Children[PathRelations.Normalize(path)] = children;

    public bool DirectoryExists(string path) => !Missing.Contains(PathRelations.Normalize(path));

    public Task<IReadOnlyList<string>> GetChildFoldersAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(Children.TryGetValue(PathRelations.Normalize(path), out var names) ? names : []);
}

internal sealed class FolderTestSettings(List<string> calls) : ISettingsStore
{
    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? Changed;

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        calls.Add("SaveSettings");
        Current = settings;
        Changed?.Invoke(this, settings);
        return Task.CompletedTask;
    }
}

internal sealed class FolderTestKnownFolders(params KnownFolder[] folders) : IKnownFolders
{
    public IReadOnlyList<KnownFolder> GetKnownFolders() => folders;
}

/// <summary>Counts are looked up by path; a path with no entry fails the way an unreadable folder would.</summary>
internal sealed class FolderTestCounter : IFolderFileCounter
{
    public Dictionary<string, FileCount> Counts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<FileCount> CountAsync(string path, IReadOnlySet<string> extensions, int limit, CancellationToken cancellationToken) =>
        Counts.TryGetValue(PathRelations.Normalize(path), out var count)
            ? Task.FromResult(count)
            : Task.FromException<FileCount>(new IOException("unreadable"));
}

internal sealed class FolderTestClients : IAiClientStatusService
{
    public List<AiClientStatus> Statuses { get; } = [];

    public List<string> Added { get; } = [];

    public bool FailAdd { get; set; }

    public IReadOnlyList<IAiClientIntegration> Integrations =>
        Statuses.Select(s => (IAiClientIntegration)new FakeIntegration(this, s.ClientId, s.DisplayName)).ToList();

    public McpServerLaunch CurrentLaunch => new("contexo-mcp", []);

    public Task<IReadOnlyList<AiClientStatus>> GetStatusesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AiClientStatus>>(Statuses.ToList());

    private sealed class FakeIntegration(FolderTestClients owner, string id, string name) : IAiClientIntegration
    {
        public string ClientId => id;

        public string DisplayName => name;

        public IReadOnlyCollection<string> KnownClientNames => [id];

        public ClientConfigState GetConfigState() => ClientConfigState.NotConfigured;

        public void AddOrRepair(McpServerLaunch launch)
        {
            if (owner.FailAdd)
            {
                throw new IOException("cannot write");
            }

            owner.Added.Add(id);
        }

        public void Remove()
        {
        }

        public string BuildManualSnippet(McpServerLaunch launch) => "{}";
    }
}

/// <summary>Everything the folder page needs, wired to fakes.</summary>
internal sealed class FoldersFixture
{
    public FoldersFixture()
    {
        Store = new FolderTestStore();
        Indexing = new FolderTestIndexing(Store.Calls);
        Dialogs = new ScriptedDialogs();
        Picker = new FolderTestPicker();
        Launcher = new FolderTestLauncher();
        Navigation = new FolderTestNavigation();
        Time = new ManualTimeProvider();
        Tree = new FolderTestTree();
        Folders = new FoldersViewModel(Store, Indexing, Dialogs, Picker, Launcher, Navigation, new InlineDispatcher(), Time, null, Tree);
    }

    public FolderTestStore Store { get; }

    public FolderTestIndexing Indexing { get; }

    public ScriptedDialogs Dialogs { get; }

    public FolderTestPicker Picker { get; }

    public FolderTestLauncher Launcher { get; }

    public FolderTestNavigation Navigation { get; }

    public ManualTimeProvider Time { get; }

    public FolderTestTree Tree { get; }

    public FoldersViewModel Folders { get; }

    public List<string> Calls => Store.Calls;

    public static FolderProgress Progress(long id, int total, int indexed, int pending, FolderState state = FolderState.Active, int failed = 0) =>
        new(id, state, total, indexed, failed, pending);

    public static IndexingSnapshot Snapshot(IndexingState state, int total, int processed, params FolderProgress[] folders) =>
        new(state, total, processed, null, null, folders, []);

    /// <summary>The names of the rows in the list, with group headers written as "# text".</summary>
    public static List<string> Describe(FoldersViewModel folders) =>
        folders.ListItems.Select(i => i switch
        {
            FolderGroupHeader h => "# " + h.Text,
            FolderRowViewModel r => r.Name,
            _ => "?",
        }).ToList();
}
