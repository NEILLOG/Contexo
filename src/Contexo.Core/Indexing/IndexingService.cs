using System.Diagnostics;
using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging;
using ActivityKind = Contexo.Core.Abstractions.ActivityKind;

namespace Contexo.Core.Indexing;

/// <summary>
/// Keeps the database in step with the watched folders: reconciliation scans (at start, every 30 minutes, after file system
/// events), a prioritised work queue processed by one or two workers, and a background pass that fills in missing vectors.
/// It never writes to, moves or deletes a user file; it only reads them.
/// </summary>
internal sealed class IndexingService : IIndexingService, IDisposable
{
    private const string EmbeddingModelMetaKey = "embedding_model";
    private const string BackfillCurrentFile = "正在更新搜尋資料";

    private enum RequestKind
    {
        All,
        Full,
        Partial,
        Retry,
    }

    private sealed record Request(RequestKind Kind, long? FolderId, IReadOnlyList<string>? Paths, long? DocumentId);

    private sealed class FolderStats
    {
        public int Documents { get; set; }
        public int Indexed { get; set; }
        public int Failed { get; set; }
        public FolderState State { get; set; }
    }

    private sealed class ActivityEntry(ActivityKind kind, DateTimeOffset bucket)
    {
        public ActivityKind Kind { get; } = kind;
        public DateTimeOffset Bucket { get; } = bucket;
        public int Count { get; set; }
        public DateTimeOffset At { get; set; }
    }

    private readonly IKnowledgeStore _store;
    private readonly IEmbeddingService _embedding;
    private readonly IParserRegistry _registry;
    private readonly ISettingsStore _settings;
    private readonly IUserActivityMonitor _activityMonitor;
    private readonly IndexingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Reconciler _reconciler;
    private readonly DocumentProcessor _processor;
    private readonly WorkQueue _queue = new();

    // Everything below is guarded by _gate (never held across an await).
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _requestSignal = new(0);
    private readonly SemaphoreSlim _workSignal = new(0);
    private readonly SemaphoreSlim _backfillSignal = new(0);
    private readonly SemaphoreSlim _publishSignal = new(0);
    private bool _rescanAll;
    private readonly HashSet<long> _fullRequests = [];
    private readonly Dictionary<long, HashSet<string>> _partialRequests = [];
    private readonly List<long?> _retryRequests = [];
    private readonly Dictionary<long, FolderStats> _folderStats = [];
    private readonly HashSet<long> _dirtyFolders = [];
    private readonly Dictionary<int, string> _currentFiles = [];
    private readonly Queue<TimeSpan> _durations = new();
    private readonly List<ActivityEntry> _activity = [];
    private int _running;
    private int _inFlight;
    private int _total;
    private int _processed;
    private bool _backfilling;
    private TaskCompletionSource? _pauseGate;
    private AppSettings _lastSettings;
    private long _lastStatsRefresh;

    private CancellationTokenSource? _cts;
    private List<Task> _tasks = [];
    private FolderWatchers? _watchers;
    private bool _started;
    private bool _disposed;

    public IndexingService(
        IKnowledgeStore store,
        IEmbeddingService embedding,
        IParserRegistry parsers,
        IChunker chunker,
        ISettingsStore settings,
        IUserActivityMonitor activityMonitor,
        ParserOptions parserOptions,
        ChunkingOptions chunkingOptions,
        ILogger<IndexingService> logger,
        TimeProvider? time = null,
        IndexingOptions? options = null)
    {
        _store = store;
        _embedding = embedding;
        _registry = parsers;
        _settings = settings;
        _activityMonitor = activityMonitor;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _options = options ?? new IndexingOptions();
        _lastSettings = settings.Current;

        _reconciler = new Reconciler(store, parsers, settings, new FolderScanner(logger), _options, _time, logger)
        {
            ActivityReported = ReportActivity,
            MassDeletionRaised = pending => MassDeletionPendingRaised?.Invoke(this, pending),
        };
        _processor = new DocumentProcessor(store, parsers, chunker, embedding, parserOptions, chunkingOptions, _options, _time, logger);
    }

    public event EventHandler<IndexingSnapshot>? SnapshotChanged;

    public event EventHandler<MassDeletionPending>? MassDeletionPendingRaised;

    public IndexingSnapshot Current => BuildSnapshot();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            _cts = cts = new CancellationTokenSource();
        }

        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await UpdateModelMetaAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _lastSettings = _settings.Current;
        }

        _settings.Changed += OnSettingsChanged;

        var folders = await _store.GetFoldersAsync(cancellationToken).ConfigureAwait(false);
        if (_options.EnableWatchers)
        {
            _watchers = new FolderWatchers(_time, _options.WatcherDebounce, OnWatcherChange, _logger);
            _watchers.Sync(folders);
        }

        // Queue the initial reconciliation before returning, so the snapshot never claims "idle" before it ran.
        RequestRescan(null);

        var token = cts.Token;
        var tasks = new List<Task>
        {
            Task.Run(() => SchedulerLoopAsync(token), CancellationToken.None),
            Task.Run(() => PublishLoopAsync(token), CancellationToken.None),
            Task.Run(() => BackfillLoopAsync(token), CancellationToken.None),
            Task.Run(() => PeriodicLoopAsync(_options.FullReconcileInterval, FullReconcileTickAsync, token), CancellationToken.None),
            Task.Run(() => PeriodicLoopAsync(_options.UnavailableCheckInterval, UnavailableCheckTickAsync, token), CancellationToken.None),
        };
        for (var i = 0; i < Math.Max(1, _options.FullSpeedWorkers); i++)
        {
            var index = i;
            tasks.Add(Task.Run(() => WorkerLoopAsync(index, token), CancellationToken.None));
        }

        _tasks = tasks;
        RequestPublish();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? cts;
        List<Task> tasks;
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            cts = _cts;
            tasks = _tasks;
            _tasks = [];
            _pauseGate?.TrySetResult();
        }

        _settings.Changed -= OnSettingsChanged;
        _watchers?.Dispose();
        _watchers = null;
        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(_options.StopTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            if (ex is TimeoutException)
            {
                _logger.LogWarning("Background indexing did not stop within {Seconds} seconds", _options.StopTimeout.TotalSeconds);
            }
        }

        cts?.Dispose();
    }

    public void Pause()
    {
        lock (_gate)
        {
            _pauseGate ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        RequestPublish();
    }

    public void Resume()
    {
        TaskCompletionSource? gate;
        lock (_gate)
        {
            gate = _pauseGate;
            _pauseGate = null;
        }

        gate?.TrySetResult();
        RequestPublish();
    }

    public void RequestRescan(long? folderId)
    {
        lock (_gate)
        {
            if (folderId is null)
            {
                _rescanAll = true;
            }
            else
            {
                _fullRequests.Add(folderId.Value);
            }
        }

        _requestSignal.Release();
        RequestPublish();
    }

    public void RequestRetry(long? documentId)
    {
        lock (_gate)
        {
            _retryRequests.Add(documentId);
        }

        _requestSignal.Release();
        RequestPublish();
    }

    public async Task ResolveMassDeletionAsync(long folderId, bool deleteMissing, CancellationToken cancellationToken)
    {
        var rescan = await _reconciler.ResolveMassDeletionAsync(folderId, deleteMissing, cancellationToken).ConfigureAwait(false);
        MarkFolderDirty(folderId);
        if (rescan)
        {
            RequestRescan(folderId);
        }

        RequestPublish();
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _started = false;
            cts = _cts;
            _pauseGate?.TrySetResult();
        }

        _settings.Changed -= OnSettingsChanged;
        _watchers?.Dispose();
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped.
        }
    }

    // ------------------------------------------------------------------ requests and reconciliation

    private void RequestPartial(long folderId, IReadOnlyList<string>? paths)
    {
        if (paths is null)
        {
            RequestRescan(folderId);
            return;
        }

        lock (_gate)
        {
            if (_rescanAll || _fullRequests.Contains(folderId))
            {
                // A full reconciliation is already waiting and will see these changes.
                return;
            }

            if (!_partialRequests.TryGetValue(folderId, out var set))
            {
                _partialRequests[folderId] = set = new HashSet<string>(PathUtil.Comparer);
            }

            set.UnionWith(paths);
        }

        _requestSignal.Release();
        RequestPublish();
    }

    private void OnWatcherChange(long folderId, IReadOnlyList<string>? paths) => RequestPartial(folderId, paths);

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        bool changed;
        lock (_gate)
        {
            changed = !_lastSettings.EnabledCategories.ToHashSet().SetEquals(settings.EnabledCategories)
                || _lastSettings.MaxFileSizeMb != settings.MaxFileSizeMb;
            _lastSettings = settings;
        }

        if (changed)
        {
            RequestRescan(null);
        }
    }

    private bool TryTakeRequest(out Request request)
    {
        lock (_gate)
        {
            if (_retryRequests.Count > 0)
            {
                var id = _retryRequests[0];
                _retryRequests.RemoveAt(0);
                request = new Request(RequestKind.Retry, null, null, id);
                _running++;
                return true;
            }

            foreach (var (folderId, paths) in _partialRequests)
            {
                _partialRequests.Remove(folderId);
                request = new Request(RequestKind.Partial, folderId, paths.ToArray(), null);
                _running++;
                return true;
            }

            if (_rescanAll)
            {
                _rescanAll = false;
                _fullRequests.Clear();
                request = new Request(RequestKind.All, null, null, null);
                _running++;
                return true;
            }

            if (_fullRequests.Count > 0)
            {
                var id = _fullRequests.First();
                _fullRequests.Remove(id);
                request = new Request(RequestKind.Full, id, null, null);
                _running++;
                return true;
            }
        }

        request = null!;
        return false;
    }

    private async Task SchedulerLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _requestSignal.WaitAsync(token).ConfigureAwait(false);
                while (true)
                {
                    await WaitIfPausedAsync(token).ConfigureAwait(false);
                    if (!TryTakeRequest(out var request))
                    {
                        break;
                    }

                    try
                    {
                        await HandleRequestAsync(request, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("Reconciliation failed: {Error}", ex.GetType().Name);
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            _running--;
                        }

                        RequestPublish();
                        NotifyIfIdle();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private async Task HandleRequestAsync(Request request, CancellationToken token)
    {
        switch (request.Kind)
        {
            case RequestKind.All:
                foreach (var folder in await _store.GetFoldersAsync(token).ConfigureAwait(false))
                {
                    await WaitIfPausedAsync(token).ConfigureAwait(false);
                    await ReconcileFolderAsync(folder.Id, null, priority: false, token).ConfigureAwait(false);
                }

                break;
            case RequestKind.Full:
                await ReconcileFolderAsync(request.FolderId!.Value, null, priority: false, token).ConfigureAwait(false);
                break;
            case RequestKind.Partial:
                await ReconcileFolderAsync(request.FolderId!.Value, request.Paths, priority: true, token).ConfigureAwait(false);
                break;
            case RequestKind.Retry:
                await RetryFailedAsync(request.DocumentId, token).ConfigureAwait(false);
                break;
        }
    }

    private async Task ReconcileFolderAsync(long folderId, IReadOnlyList<string>? scope, bool priority, CancellationToken token)
    {
        var folders = await _store.GetFoldersAsync(token).ConfigureAwait(false);
        var folder = folders.FirstOrDefault(f => f.Id == folderId);
        if (folder is null)
        {
            _reconciler.Forget(folderId);
            MarkFolderDirty(folderId);
            return;
        }

        var result = await _reconciler.ReconcileAsync(folder, folders, scope, token).ConfigureAwait(false);
        EnqueueItems(result.Items, priority);
        MarkFolderDirty(folderId);

        if (_watchers is { } watchers)
        {
            watchers.Sync(await _store.GetFoldersAsync(token).ConfigureAwait(false));
        }
    }

    private async Task RetryFailedAsync(long? documentId, CancellationToken token)
    {
        var folders = await _store.GetFoldersAsync(token).ConfigureAwait(false);
        var settings = _settings.Current;
        var items = new List<WorkItem>();
        foreach (var document in await _store.GetFailedDocumentsAsync(int.MaxValue, token).ConfigureAwait(false))
        {
            if (documentId is { } id && document.Id != id)
            {
                continue;
            }

            if (folders.All(f => f.Id != document.FolderId))
            {
                continue;
            }

            try
            {
                var file = new FileInfo(document.Path);
                if (!file.Exists || (file.Attributes & FolderScanner.CloudOnly) != 0)
                {
                    continue;
                }

                var tooLarge = settings.MaxFileSizeMb is { } mb && file.Length > mb * 1024L * 1024L;
                items.Add(new WorkItem(document.FolderId, document.Path, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), tooLarge));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cannot be inspected right now; the next scan decides.
            }
        }

        EnqueueItems(items, priority: true);
        foreach (var folderId in items.Select(i => i.FolderId).Distinct())
        {
            MarkFolderDirty(folderId);
        }
    }

    private void EnqueueItems(IReadOnlyList<WorkItem> items, bool priority)
    {
        if (items.Count == 0)
        {
            return;
        }

        var added = 0;
        foreach (var item in items)
        {
            if (_queue.Enqueue(item, priority))
            {
                added++;
            }
        }

        lock (_gate)
        {
            _total += added;
        }

        _workSignal.Release(Math.Min(items.Count, Math.Max(1, _options.FullSpeedWorkers)));
        RequestPublish();
    }

    // ------------------------------------------------------------------ workers

    private bool IsThrottled() => _settings.Current.FullSpeedOnlyWhenIdle && _activityMonitor.IdleTime < _options.IdleThreshold;

    private int CurrentDegree() => IsThrottled() ? 1 : Math.Max(1, _options.FullSpeedWorkers);

    private async Task WorkerLoopAsync(int index, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await WaitIfPausedAsync(token).ConfigureAwait(false);
                if (index >= CurrentDegree())
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _time, token).ConfigureAwait(false);
                    continue;
                }

                WorkItem? item;
                lock (_gate)
                {
                    if (_queue.TryDequeue(out var dequeued))
                    {
                        item = dequeued;
                        _inFlight++;
                        _currentFiles[index] = Path.GetFileName(dequeued.Path);
                    }
                    else
                    {
                        item = null;
                    }
                }

                if (item is null)
                {
                    await _workSignal.WaitAsync(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
                    continue;
                }

                RequestPublish();
                await RunItemAsync(index, item, token).ConfigureAwait(false);

                if (IsThrottled())
                {
                    await Task.Delay(_options.ThrottledFileDelay, _time, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private async Task RunItemAsync(int worker, WorkItem item, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        ProcessOutcome? outcome = null;
        try
        {
            var folders = await _store.GetFoldersAsync(token).ConfigureAwait(false);
            var folder = folders.FirstOrDefault(f => f.Id == item.FolderId);
            if (folder is not null)
            {
                // The user may have excluded the file or switched its category off while it waited in the queue.
                var exclusions = await _store.GetExclusionsAsync(token).ConfigureAwait(false);
                var rules = ScanRules.Create(folder, folders, exclusions, _settings.Current, _registry);
                if (rules.Extensions.Contains(Path.GetExtension(item.Path)) && !rules.IsFileExcluded(item.Path))
                {
                    outcome = await _processor.ProcessAsync(item, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("Processing {File} failed: {Error}", Path.GetFileName(item.Path), ex.GetType().Name);
        }
        finally
        {
            FinishItem(worker, item, outcome, Stopwatch.GetElapsedTime(started));
        }
    }

    private void FinishItem(int worker, WorkItem item, ProcessOutcome? outcome, TimeSpan elapsed)
    {
        switch (outcome)
        {
            case ProcessOutcome.Added:
                ReportActivity(ActivityKind.Added, 1);
                break;
            case ProcessOutcome.Updated:
                ReportActivity(ActivityKind.Updated, 1);
                break;
        }

        lock (_gate)
        {
            _inFlight--;
            _processed++;
            _currentFiles.Remove(worker);
            _durations.Enqueue(elapsed);
            while (_durations.Count > 20)
            {
                _durations.Dequeue();
            }

            _dirtyFolders.Add(item.FolderId);
        }

        RequestPublish();
        NotifyIfIdle();
    }

    // ------------------------------------------------------------------ pause

    private async Task WaitIfPausedAsync(CancellationToken token)
    {
        while (true)
        {
            TaskCompletionSource? gate;
            lock (_gate)
            {
                gate = _pauseGate;
            }

            if (gate is null)
            {
                return;
            }

            await gate.Task.WaitAsync(token).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ idle bookkeeping

    private bool IsBusyLocked() =>
        _inFlight > 0
        || _running > 0
        || _queue.Count > 0
        || _rescanAll
        || _fullRequests.Count > 0
        || _partialRequests.Count > 0
        || _retryRequests.Count > 0;

    /// <summary>When nothing is left to do: forget the round's counters and give the vector backfill a chance.</summary>
    private void NotifyIfIdle()
    {
        var idle = false;
        lock (_gate)
        {
            if (!IsBusyLocked())
            {
                _total = 0;
                _processed = 0;
                _durations.Clear();
                idle = true;
            }
        }

        if (idle)
        {
            SignalBackfill();
        }

        RequestPublish();
    }

    // ------------------------------------------------------------------ vector backfill

    private void SignalBackfill()
    {
        if (_backfillSignal.CurrentCount == 0)
        {
            _backfillSignal.Release();
        }
    }

    private async Task BackfillLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _backfillSignal.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await BackfillAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError("Updating search data failed: {Error}", ex.GetType().Name);
                }
                finally
                {
                    SetBackfilling(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private bool IsBusy()
    {
        lock (_gate)
        {
            return IsBusyLocked();
        }
    }

    private async Task BackfillAsync(CancellationToken token)
    {
        if (!_embedding.IsAvailable)
        {
            return;
        }

        await UpdateModelMetaAsync(token).ConfigureAwait(false);
        var modelId = _embedding.ModelId;
        if (string.IsNullOrEmpty(modelId))
        {
            return;
        }

        long lastFirstId = -1;
        while (!token.IsCancellationRequested)
        {
            await WaitIfPausedAsync(token).ConfigureAwait(false);
            if (IsBusy())
            {
                // Real work arrived; the backfill is signalled again when the queue drains.
                return;
            }

            var batch = new List<ChunkForEmbedding>(_options.EmbeddingBatchSize);
            await foreach (var chunk in _store.ReadChunksMissingVectorAsync(modelId, token).ConfigureAwait(false))
            {
                batch.Add(chunk);
                if (batch.Count >= _options.EmbeddingBatchSize)
                {
                    break;
                }
            }

            if (batch.Count == 0 || batch[0].ChunkId == lastFirstId)
            {
                return;
            }

            lastFirstId = batch[0].ChunkId;
            SetBackfilling(true);

            var vectors = await _embedding.EmbedDocumentsAsync(batch.Select(c => c.EmbeddingText).ToList(), token).ConfigureAwait(false);
            if (vectors.Count != batch.Count)
            {
                _logger.LogWarning("The embedding service returned an unexpected number of vectors; stopping");
                return;
            }

            var stored = new List<StoredVector>(batch.Count);
            for (var i = 0; i < batch.Count; i++)
            {
                stored.Add(new StoredVector(batch[i].ChunkId, vectors[i]));
            }

            await _store.SaveVectorsAsync(modelId, stored, token).ConfigureAwait(false);

            if (IsThrottled())
            {
                await Task.Delay(_options.ThrottledFileDelay, _time, token).ConfigureAwait(false);
            }
        }
    }

    private void SetBackfilling(bool value)
    {
        lock (_gate)
        {
            if (_backfilling == value)
            {
                return;
            }

            _backfilling = value;
        }

        RequestPublish();
    }

    private async Task UpdateModelMetaAsync(CancellationToken token)
    {
        var modelId = _embedding.ModelId;
        if (string.IsNullOrEmpty(modelId))
        {
            return;
        }

        var stored = await _store.GetMetaAsync(EmbeddingModelMetaKey, token).ConfigureAwait(false);
        if (!string.Equals(stored, modelId, StringComparison.Ordinal))
        {
            await _store.SetMetaAsync(EmbeddingModelMetaKey, modelId, token).ConfigureAwait(false);
            _logger.LogInformation("Embedding model changed; vectors of the new model are filled in in the background");
            SignalBackfill();
        }
    }

    // ------------------------------------------------------------------ periodic work

    private async Task PeriodicLoopAsync(TimeSpan interval, Func<CancellationToken, Task> tick, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(interval, _time, token).ConfigureAwait(false);
                try
                {
                    await tick(token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError("Periodic check failed: {Error}", ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private Task FullReconcileTickAsync(CancellationToken token)
    {
        RequestRescan(null);
        return Task.CompletedTask;
    }

    private async Task UnavailableCheckTickAsync(CancellationToken token)
    {
        foreach (var folder in await _store.GetFoldersAsync(token).ConfigureAwait(false))
        {
            if (folder.State == FolderState.Unavailable)
            {
                RequestRescan(folder.Id);
            }
        }
    }

    // ------------------------------------------------------------------ activity and snapshots

    private void ReportActivity(ActivityKind kind, int count)
    {
        if (count <= 0)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var bucketTicks = Math.Max(1, _options.ActivityBucket.Ticks);
        var bucket = new DateTimeOffset(now.UtcTicks - (now.UtcTicks % bucketTicks), TimeSpan.Zero);
        lock (_gate)
        {
            var entry = _activity.FirstOrDefault(e => e.Kind == kind && e.Bucket == bucket);
            if (entry is null)
            {
                entry = new ActivityEntry(kind, bucket);
            }
            else
            {
                _activity.Remove(entry);
            }

            entry.Count += count;
            entry.At = now;
            _activity.Insert(0, entry);
            if (_activity.Count > _options.MaxRecentActivities)
            {
                _activity.RemoveRange(_options.MaxRecentActivities, _activity.Count - _options.MaxRecentActivities);
            }
        }

        RequestPublish();
    }

    private void MarkFolderDirty(long folderId)
    {
        lock (_gate)
        {
            _dirtyFolders.Add(folderId);
        }

        RequestPublish();
    }

    private void RequestPublish()
    {
        if (_publishSignal.CurrentCount == 0)
        {
            try
            {
                _publishSignal.Release();
            }
            catch (ObjectDisposedException)
            {
                // Shut down.
            }
        }
    }

    private async Task PublishLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _publishSignal.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await RefreshFolderStatsAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Could not refresh folder statistics: {Error}", ex.GetType().Name);
                }

                var snapshot = BuildSnapshot();
                try
                {
                    SnapshotChanged?.Invoke(this, snapshot);
                }
                catch (Exception ex)
                {
                    _logger.LogError("A snapshot handler failed: {Error}", ex.GetType().Name);
                }

                await Task.Delay(_options.SnapshotInterval, _time, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private async Task RefreshFolderStatsAsync(CancellationToken token)
    {
        long[] dirty;
        bool busy;
        lock (_gate)
        {
            if (_dirtyFolders.Count == 0)
            {
                return;
            }

            busy = IsBusyLocked();
            dirty = _dirtyFolders.ToArray();
        }

        if (busy && Stopwatch.GetElapsedTime(_lastStatsRefresh) < _options.FolderStatsInterval)
        {
            // Busy and refreshed recently: look again on the next publish.
            RequestPublishLater();
            return;
        }

        var folders = await _store.GetFoldersAsync(token).ConfigureAwait(false);
        var fresh = new Dictionary<long, FolderStats>();
        foreach (var folder in folders)
        {
            var stats = new FolderStats { State = folder.State };
            if (dirty.Contains(folder.Id) || !TryGetStats(folder.Id, out _))
            {
                foreach (var document in await _store.GetDocumentsAsync(folder.Id, token).ConfigureAwait(false))
                {
                    stats.Documents++;
                    switch (document.Status)
                    {
                        case DocumentStatus.Failed:
                            stats.Failed++;
                            break;
                        default:
                            stats.Indexed++;
                            break;
                    }
                }
            }
            else if (TryGetStats(folder.Id, out var old))
            {
                stats.Documents = old.Documents;
                stats.Indexed = old.Indexed;
                stats.Failed = old.Failed;
            }

            fresh[folder.Id] = stats;
        }

        lock (_gate)
        {
            _folderStats.Clear();
            foreach (var (id, stats) in fresh)
            {
                _folderStats[id] = stats;
            }

            _dirtyFolders.ExceptWith(dirty);
            _lastStatsRefresh = Stopwatch.GetTimestamp();
        }
    }

    private bool TryGetStats(long folderId, out FolderStats stats)
    {
        lock (_gate)
        {
            return _folderStats.TryGetValue(folderId, out stats!);
        }
    }

    private void RequestPublishLater()
    {
        // The publish loop waits SnapshotInterval after each snapshot, so signalling now results in a retry shortly after.
        RequestPublish();
    }

    private IndexingSnapshot BuildSnapshot()
    {
        lock (_gate)
        {
            var queued = _queue.Count;
            var processing = queued > 0 || _inFlight > 0;
            var state = _pauseGate is not null
                ? IndexingState.Paused
                : processing
                    ? IndexingState.Indexing
                    : IsBusyLocked()
                        ? IndexingState.Scanning
                        : _backfilling
                            ? IndexingState.Indexing
                            : IndexingState.Idle;

            string? currentFile = null;
            if (_inFlight > 0 && _currentFiles.Count > 0)
            {
                currentFile = _currentFiles.Values.Last();
            }
            else if (_backfilling && !processing)
            {
                currentFile = BackfillCurrentFile;
            }

            TimeSpan? remaining = null;
            if (state == IndexingState.Indexing && _processed >= 5 && _durations.Count > 0)
            {
                var average = TimeSpan.FromTicks((long)_durations.Average(d => d.Ticks));
                remaining = average * Math.Max(0, _total - _processed);
            }

            var folders = new List<FolderProgress>(_folderStats.Count);
            foreach (var (id, stats) in _folderStats.OrderBy(p => p.Key))
            {
                var (all, added) = _queue.CountForFolder(id);
                folders.Add(new FolderProgress(id, stats.State, stats.Documents + added, stats.Indexed, stats.Failed, all));
            }

            var activity = _activity.Select(e => new RecentActivity(e.Kind, e.Count, e.At)).ToList();
            return new IndexingSnapshot(state, _total, _processed, currentFile, remaining, folders, activity);
        }
    }
}
