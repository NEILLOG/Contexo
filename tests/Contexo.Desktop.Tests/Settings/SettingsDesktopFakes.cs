using Contexo.App.About;
using Contexo.App.AiClients;
using Contexo.App.Folders;
using Contexo.App.Search;
using Contexo.App.Services;
using Contexo.App.Settings;
using Contexo.App.Shell;
using Contexo.Core.Abstractions;
using Contexo.Desktop.Platform.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contexo.Desktop.Tests.Settings;

/// <summary>One shared, ordered list of what the fakes were asked to do.</summary>
internal sealed class CallLog
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }

    public void Add(string entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }
}

internal sealed class RecordingIndexing(CallLog log) : IIndexingService
{
    public IndexingSnapshot Current => IndexingSnapshot.Initial;

    public event EventHandler<IndexingSnapshot>? SnapshotChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<MassDeletionPending>? MassDeletionPendingRaised
    {
        add { }
        remove { }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Pause() => log.Add("Pause");

    public void Resume() => log.Add("Resume");

    public void RequestRescan(long? folderId) => log.Add("Rescan:" + (folderId?.ToString() ?? "all"));

    public void RequestRetry(long? documentId) => log.Add("Retry");

    public Task ResolveMassDeletionAsync(long folderId, bool deleteMissing, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Only the maintenance and exclusion members are real; everything else is not used by the settings page.</summary>
internal sealed class FakeKnowledgeStore(CallLog log) : IKnowledgeStore
{
    public List<Exclusion> Exclusions { get; } = [];

    public long DatabaseBytes { get; set; }

    /// <summary>What the size becomes after the data was cleared.</summary>
    public long DatabaseBytesAfterClear { get; set; }

    public Exception? ClearFailure { get; set; }

    public Task<StoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new StoreStatistics(1, 2, 0, 3, DatabaseBytes));

    public async Task ClearIndexedDataAsync(CancellationToken cancellationToken)
    {
        log.Add("Clear");
        await Task.Yield();
        if (ClearFailure is not null)
        {
            throw ClearFailure;
        }

        DatabaseBytes = DatabaseBytesAfterClear;
    }

    public Task<IReadOnlyList<Exclusion>> GetExclusionsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Exclusion>>(Exclusions.ToList());

    public Task RemoveExclusionAsync(long exclusionId, CancellationToken cancellationToken)
    {
        log.Add("RemoveExclusion:" + exclusionId);
        Exclusions.RemoveAll(e => e.Id == exclusionId);
        return Task.CompletedTask;
    }

    public Task InitializeAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task SetMetaAsync(string key, string value, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<long> GetIndexVersionAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<IReadOnlyList<WatchedFolder>> GetFoldersAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<WatchedFolder> AddFolderAsync(string path, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task SetFolderExclusionsAsync(long folderId, IReadOnlyList<string> excludedSubfolders, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task SetFolderStateAsync(long folderId, FolderState state, DateTimeOffset? lastScanAt, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<IReadOnlyList<DocumentRecord>> GetDocumentsAsync(long folderId, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<IReadOnlyList<DocumentRecord>> GetFailedDocumentsAsync(int limit, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<DocumentRecord?> GetDocumentByPathAsync(string path, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task ReplaceDocumentAsync(DocumentWrite write, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task MarkDocumentAsync(long folderId, string path, FileFingerprint fingerprint, DocumentStatus status, DocumentErrorCode errorCode, string? errorMessage, DateTimeOffset? nextRetryAt, bool keepExistingChunks, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task DeleteDocumentAsync(long documentId, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task MoveDocumentAsync(long documentId, string newPath, CancellationToken cancellationToken) => throw new NotImplementedException();

    public IAsyncEnumerable<StoredVector> ReadVectorsAsync(string modelId, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<IReadOnlyList<KeywordHit>> KeywordSearchAsync(string? ftsQuery, IReadOnlyList<string> likeTerms, int limit, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<IReadOnlyList<ChunkDetail>> GetChunksAsync(IReadOnlyList<long> chunkIds, CancellationToken cancellationToken) => throw new NotImplementedException();

    public IAsyncEnumerable<ChunkForEmbedding> ReadChunksMissingVectorAsync(string modelId, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task SaveVectorsAsync(string modelId, IReadOnlyList<StoredVector> vectors, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<ExcelTableRecord?> GetExcelTableAsync(string tableId, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<Exclusion> AddExclusionAsync(string path, bool isFolder, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task RecordMcpActivityAsync(McpActivity activity, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<IReadOnlyList<McpClientActivitySummary>> GetMcpActivitySummariesAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
}

internal sealed class FakeAppPaths : IAppPaths
{
    public string DataDirectory => "/data/Contexo";

    public string DatabasePath => "/data/Contexo/contexo.db";

    public string LogsDirectory => "/data/Contexo/logs";

    public string SettingsPath => "/data/Contexo/settings.json";

    public string ModelsDirectory => "/data/Contexo/models";

    public string McpExecutablePath => "/app/Contexo.Mcp";
}

/// <summary>Scripted dialogs: the confirm answer is fixed, and custom dialogs are driven by <see cref="OnShow"/>.</summary>
internal sealed class ScriptedDialogs : IDialogService
{
    public bool ConfirmAnswer { get; set; }

    public List<ConfirmRequest> ConfirmRequests { get; } = [];

    public List<object> Shown { get; } = [];

    public Action<object>? OnShow { get; set; }

    public Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        ConfirmRequests.Add(request);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task ShowAsync(object dialogViewModel)
    {
        Shown.Add(dialogViewModel);
        OnShow?.Invoke(dialogViewModel);
        return Task.CompletedTask;
    }
}

internal sealed class FakeStartupRegistration : IStartupRegistration
{
    public bool IsEnabled { get; set; }

    public List<bool> Calls { get; } = [];

    public Exception? FailWith { get; set; }

    public void SetEnabled(bool enabled)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        Calls.Add(enabled);
        IsEnabled = enabled;
    }
}

internal sealed class FakeLauncher : IShellLauncher
{
    public List<string> OpenedFolders { get; } = [];

    public void OpenFile(string path)
    {
    }

    public void RevealInFileManager(string path)
    {
    }

    public void OpenFolder(string path) => OpenedFolders.Add(path);
}

internal sealed class FakeClipboard : IClipboardService
{
    public string? Text { get; private set; }

    public bool Available { get; set; } = true;

    public Task<bool> TrySetTextAsync(string text)
    {
        if (!Available)
        {
            return Task.FromResult(false);
        }

        Text = text;
        return Task.FromResult(true);
    }
}

internal sealed class FakeIntegration(string id, string name) : IAiClientIntegration
{
    public string ClientId => id;

    public string DisplayName => name;

    public IReadOnlyCollection<string> KnownClientNames => [];

    public ClientConfigState GetConfigState() => ClientConfigState.NotConfigured;

    public void AddOrRepair(McpServerLaunch launch)
    {
    }

    public void Remove()
    {
    }

    public string BuildManualSnippet(McpServerLaunch launch) => $"{{\"{id}\":\"{launch.ExecutablePath}\"}}";
}

internal sealed class FakeClientStatusService(params IAiClientIntegration[] integrations) : IAiClientStatusService
{
    public IReadOnlyList<IAiClientIntegration> Integrations { get; } = integrations;

    public McpServerLaunch CurrentLaunch => new("/app/Contexo.Mcp", []);

    public Task<IReadOnlyList<AiClientStatus>> GetStatusesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AiClientStatus>>([]);
}

/// <summary>Builds the real shell with the real settings page on top of fakes, wired like the product.</summary>
internal sealed class SettingsShell
{
    public SettingsShell(AppSettings? settings = null, CallLog? log = null)
    {
        Log = log ?? new CallLog();
        Settings = new FakeSettingsStore();
        if (settings is not null)
        {
            Settings.SaveAsync(settings, CancellationToken.None).GetAwaiter().GetResult();
        }

        Indexing = new RecordingIndexing(Log);
        Data = new FakeKnowledgeStore(Log);
        Startup = new FakeStartupRegistration { IsEnabled = Settings.Current.LaunchAtStartup };
        Launcher = new FakeLauncher();
        Clipboard = new FakeClipboard();
        var dispatcher = new AvaloniaUiDispatcher();
        Navigation = new NavigationService();
        Dialogs = new DialogHostViewModel(dispatcher);
        Page = new SettingsViewModel(
            Settings,
            Indexing,
            Data,
            new FakeAppPaths(),
            Dialogs,
            Startup,
            Launcher,
            Clipboard,
            new FakeClientStatusService(new FakeIntegration("claude-desktop", "Claude Desktop"), new FakeIntegration("cursor", "Cursor")),
            dispatcher,
            NullLogger<SettingsViewModel>.Instance);
        var statusBar = new StatusBarViewModel(new FakeIndexingService(), new FakeAiClientStatusService(), dispatcher, TimeProvider.System, NullLogger<StatusBarViewModel>.Instance);
        Shell = new ShellViewModel(
            new FoldersViewModel(),
            new FirstRunViewModel(),
            new SearchViewModel(),
            new AiClientsViewModel(),
            Page,
            new AboutViewModel(),
            statusBar,
            Navigation,
            Dialogs,
            Settings,
            dispatcher);
        Shell.SelectedItem = Shell.NavigationItems.Single(i => ReferenceEquals(i.Page, Page));
    }

    public CallLog Log { get; }

    public FakeSettingsStore Settings { get; }

    public RecordingIndexing Indexing { get; }

    public FakeKnowledgeStore Data { get; }

    public FakeStartupRegistration Startup { get; }

    public FakeLauncher Launcher { get; }

    public FakeClipboard Clipboard { get; }

    public NavigationService Navigation { get; }

    public DialogHostViewModel Dialogs { get; }

    public SettingsViewModel Page { get; }

    public ShellViewModel Shell { get; }

    public MainWindow CreateWindow() => new() { DataContext = Shell };
}
