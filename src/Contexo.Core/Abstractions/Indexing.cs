namespace Contexo.Core.Abstractions;

public enum IndexingState
{
    Idle,
    Scanning,
    Indexing,
    Paused,
}

public sealed record FolderProgress(long FolderId, FolderState State, int TotalFiles, int IndexedFiles, int FailedFiles, int PendingFiles);

public enum ActivityKind
{
    Updated,
    Added,
    Removed,
    Moved,
}

public sealed record RecentActivity(ActivityKind Kind, int FileCount, DateTimeOffset At);

/// <param name="EstimatedRemaining">Null until enough files were processed to estimate.</param>
public sealed record IndexingSnapshot(
    IndexingState State,
    int TotalFiles,
    int ProcessedFiles,
    string? CurrentFile,
    TimeSpan? EstimatedRemaining,
    IReadOnlyList<FolderProgress> Folders,
    IReadOnlyList<RecentActivity> RecentActivity)
{
    public static IndexingSnapshot Initial { get; } = new(IndexingState.Idle, 0, 0, null, null, [], []);
}

public sealed record MassDeletionPending(long FolderId, int MissingFileCount, int TotalFileCount);

/// <summary>Owned by Contexo.Desktop. Contexo.Mcp never indexes.</summary>
public interface IIndexingService
{
    IndexingSnapshot Current { get; }

    /// <summary>Raised on any thread, at most a few times per second.</summary>
    event EventHandler<IndexingSnapshot>? SnapshotChanged;

    event EventHandler<MassDeletionPending>? MassDeletionPendingRaised;

    /// <summary>Starts the background loop: initial reconciliation scan, then watcher-assisted periodic scans.</summary>
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);

    void Pause();
    void Resume();

    /// <summary>Queues a full reconciliation of one folder, or all folders when null.</summary>
    void RequestRescan(long? folderId);

    /// <summary>Retries failed documents now, or only the given one.</summary>
    void RequestRetry(long? documentId);

    /// <summary>Answer to <see cref="MassDeletionPendingRaised"/>. When false, data is kept and the folder returns to Active.</summary>
    Task ResolveMassDeletionAsync(long folderId, bool deleteMissing, CancellationToken cancellationToken);
}

/// <summary>Implemented per platform by Contexo.Desktop (Windows: GetLastInputInfo). Core's default implementation always reports idle.</summary>
public interface IUserActivityMonitor
{
    /// <summary>Time since the last keyboard or mouse input.</summary>
    TimeSpan IdleTime { get; }
}
