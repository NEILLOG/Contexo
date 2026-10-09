using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Contexo.App.About;
using Contexo.App.Services;
using Contexo.App.Shell;
using Contexo.App.UserMessages;
using Contexo.App.ViewModels;
using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contexo.App.Folders;

public enum FolderFilter
{
    All,
    Problem,
    Running,
    Done,
}

/// <summary>One button of the summary / filter row, e.g. "有問題 1".</summary>
public sealed partial class FolderFilterOption : ObservableObject
{
    public FolderFilterOption(FolderFilter kind, string name)
    {
        Kind = kind;
        Name = name;
        Label = name;
    }

    public FolderFilter Kind { get; }

    public string Name { get; }

    [ObservableProperty]
    public partial string Label { get; internal set; }

    public override string ToString() => Label;
}

/// <summary>
/// The "資料夾" page: overall progress, the folder list (grouped, filtered, searchable), failed files, adding and removing folders.
/// Snapshot events arrive on background threads; they are throttled and applied through <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class FoldersViewModel : ViewModelBase, IPageLifecycle, IDisposable
{
    /// <summary>Minimum time between two visible updates caused by snapshot events (same as the status bar).</summary>
    public static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>"剛更新 N 個檔案" is shown for this long.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(5);

    /// <summary>The search box appears when more folders than this are watched.</summary>
    public const int SearchBoxThreshold = 8;

    /// <summary>The failed files card shows this many files; the rest are behind "查看全部".</summary>
    public const int FailedPreviewCount = 5;

    private const int FailedFetchLimit = 1000;

    private readonly IKnowledgeStore _store;
    private readonly IIndexingService _indexing;
    private readonly IDialogService _dialogs;
    private readonly IFolderPicker _picker;
    private readonly IShellLauncher _launcher;
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IFolderTreeReader _tree;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<long, FolderRowViewModel> _rows = [];
    private readonly Dictionary<long, int> _busyPeak = [];
    private readonly Dictionary<long, (int Count, DateTimeOffset At)> _updated = [];
    private readonly HashSet<long> _asking = [];
    private readonly FolderFilterOption _allOption = new(FolderFilter.All, "全部");
    private readonly FolderFilterOption _problemOption = new(FolderFilter.Problem, "有問題");
    private readonly FolderFilterOption _runningOption = new(FolderFilter.Running, "處理中");
    private readonly FolderFilterOption _doneOption = new(FolderFilter.Done, "已完成");

    private IReadOnlyList<WatchedFolder> _folders = [];
    private IReadOnlyList<DocumentRecord> _failedDocuments = [];
    private IndexingSnapshot _latest;
    private IndexingSnapshot _snapshot;
    private IndexingSnapshot _pending;
    private long _lastAppliedTimestamp;
    private bool _hasApplied;
    private ITimer? _throttleTimer;
    private ITimer? _expiryTimer;
    private bool _active = true;
    private bool _doneExpanded;
    private bool _disposed;
    private string _lastMismatch = "";

    public FoldersViewModel(
        IKnowledgeStore store,
        IIndexingService indexing,
        IDialogService dialogs,
        IFolderPicker picker,
        IShellLauncher launcher,
        INavigationService navigation,
        IUiDispatcher dispatcher,
        TimeProvider time,
        ILogger<FoldersViewModel>? logger = null,
        IFolderTreeReader? tree = null)
    {
        _store = store;
        _indexing = indexing;
        _dialogs = dialogs;
        _picker = picker;
        _launcher = launcher;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger ?? NullLogger<FoldersViewModel>.Instance;
        _tree = tree ?? new FileSystemFolderTreeReader();

        _latest = _snapshot = _pending = indexing?.Current ?? IndexingSnapshot.Initial;
        Filters = [_allOption, _problemOption, _runningOption, _doneOption];
        SelectedFilter = _allOption;
        if (indexing is not null)
        {
            UpdateProgressCard(_snapshot);
            indexing.SnapshotChanged += OnSnapshotChanged;
            indexing.MassDeletionPendingRaised += OnMassDeletionPending;
        }
    }

    /// <summary>
    /// Used only when the shell is composed without services (shell tests). The instance is inert: it shows an empty page
    /// and ignores navigation. The product always uses the constructor with services.
    /// </summary>
    internal FoldersViewModel()
        : this(null!, null!, null!, null!, null!, null!, null!, TimeProvider.System)
    {
    }

    private bool IsInert => _store is null;

    public string Title => "資料夾";

    // ---- Observable state -------------------------------------------------------------------------------------

    /// <summary>Group headers and folder rows, in display order.</summary>
    public ObservableCollection<object> ListItems { get; } = [];

    public IReadOnlyList<FolderFilterOption> Filters { get; }

    [ObservableProperty]
    public partial FolderFilterOption? SelectedFilter { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowSearchBox { get; private set; }

    [ObservableProperty]
    public partial bool HasFolders { get; private set; }

    [ObservableProperty]
    public partial bool IsEmptyHintVisible { get; private set; }

    [ObservableProperty]
    public partial bool IsNoMatchVisible { get; private set; }

    [ObservableProperty]
    public partial string ErrorBannerText { get; private set; } = "";

    public bool HasErrorBanner => ErrorBannerText.Length > 0;

    [ObservableProperty]
    public partial string NoticeText { get; private set; } = "";

    public bool HasNotice => NoticeText.Length > 0;

    // progress card
    [ObservableProperty]
    public partial bool IsProgressVisible { get; private set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; private set; }

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial string ProgressTitle { get; private set; } = "";

    [ObservableProperty]
    public partial string ProgressChipText { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsPaused { get; private set; }

    [ObservableProperty]
    public partial string ProgressCountText { get; private set; } = "";

    [ObservableProperty]
    public partial string CurrentFileText { get; private set; } = "";

    public bool HasCurrentFile => CurrentFileText.Length > 0;

    public string PauseButtonText => IsPaused ? "繼續" : "暫停";

    // failed files
    public ObservableCollection<FailedFileRowViewModel> AllFailed { get; } = [];

    public ObservableCollection<FailedFileRowViewModel> TopFailed { get; } = [];

    [ObservableProperty]
    public partial bool HasFailed { get; private set; }

    [ObservableProperty]
    public partial string FailedTitle { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasMoreFailed { get; private set; }

    [ObservableProperty]
    public partial string ShowAllFailedText { get; private set; } = "";

    /// <summary>Every folder row, including the ones hidden by the collapsed "done" group. Used by tests.</summary>
    internal IReadOnlyList<FolderRowViewModel> Rows => _rows.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Completes when the last refresh started by <see cref="OnNavigatedTo"/> or a snapshot has finished. Used by tests.</summary>
    internal Task PendingRefresh { get; private set; } = Task.CompletedTask;

    partial void OnErrorBannerTextChanged(string value) => OnPropertyChanged(nameof(HasErrorBanner));

    partial void OnNoticeTextChanged(string value) => OnPropertyChanged(nameof(HasNotice));

    partial void OnCurrentFileTextChanged(string value) => OnPropertyChanged(nameof(HasCurrentFile));

    partial void OnIsPausedChanged(bool value) => OnPropertyChanged(nameof(PauseButtonText));

    partial void OnSelectedFilterChanged(FolderFilterOption? value)
    {
        if (value is null)
        {
            SelectedFilter = _allOption;
            return;
        }

        RebuildList();
    }

    partial void OnSearchTextChanged(string value) => RebuildList();

    // ---- Page lifecycle ---------------------------------------------------------------------------------------

    public void OnNavigatedTo()
    {
        if (IsInert)
        {
            return;
        }

        _active = true;
        StartExpiryTimer();
        ApplyLatest(reloadOnChange: false);
        PendingRefresh = RefreshAsync();
    }

    public void OnNavigatedFrom()
    {
        _active = false;
        lock (_gate)
        {
            _expiryTimer?.Dispose();
            _expiryTimer = null;
        }
    }

    /// <summary>Reads folders and failed files from the database and updates the whole page.</summary>
    public async Task RefreshAsync()
    {
        if (IsInert)
        {
            return;
        }

        try
        {
            _folders = await _store.GetFoldersAsync(_cts.Token);
            await LoadFailedAsync();
            RebuildList();
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the folder list");
        }
    }

    private async Task LoadFailedAsync()
    {
        _failedDocuments = await _store.GetFailedDocumentsAsync(FailedFetchLimit, _cts.Token);
        RebuildFailed();
    }

    // ---- Indexing snapshots -----------------------------------------------------------------------------------

    private void OnSnapshotChanged(object? sender, IndexingSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = snapshot;
            if (_throttleTimer is not null)
            {
                return; // A flush is already scheduled; it will pick up the newest snapshot.
            }

            var wait = _hasApplied ? ThrottleInterval - _time.GetElapsedTime(_lastAppliedTimestamp) : TimeSpan.Zero;
            if (wait <= TimeSpan.Zero)
            {
                FlushLocked();
            }
            else
            {
                _throttleTimer = _time.CreateTimer(_ => FlushFromTimer(), null, wait, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void FlushFromTimer()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _throttleTimer?.Dispose();
            _throttleTimer = null;
            FlushLocked();
        }
    }

    private void FlushLocked()
    {
        var snapshot = _pending;
        _lastAppliedTimestamp = _time.GetTimestamp();
        _hasApplied = true;
        _dispatcher.Post(() =>
        {
            _latest = snapshot;
            if (_active)
            {
                ApplyLatest(reloadOnChange: true);
            }
        });
    }

    private void ApplyLatest(bool reloadOnChange)
    {
        var previous = _snapshot;
        var snapshot = _latest;
        TrackActivity(snapshot);
        _snapshot = snapshot;
        UpdateProgressCard(snapshot);
        RebuildList();

        if (!reloadOnChange || IsInert)
        {
            return;
        }

        var failedBefore = previous.Folders.Sum(f => f.FailedFiles);
        var failedNow = snapshot.Folders.Sum(f => f.FailedFiles);
        var becameIdle = previous.State != IndexingState.Idle && snapshot.State == IndexingState.Idle;
        var knownIds = new HashSet<long>(_folders.Select(f => f.Id));
        var snapshotIds = new HashSet<long>(snapshot.Folders.Select(f => f.FolderId));
        var mismatch = snapshotIds.Except(knownIds).Any();
        var signature = mismatch ? string.Join(',', snapshotIds.Order()) : "";

        if (mismatch && signature != _lastMismatch)
        {
            _lastMismatch = signature;
            PendingRefresh = RefreshAsync();
        }
        else if (failedBefore != failedNow || becameIdle)
        {
            PendingRefresh = RefreshQuietlyAsync(becameIdle);
        }
    }

    private async Task RefreshQuietlyAsync(bool reloadFolders)
    {
        try
        {
            if (reloadFolders)
            {
                _folders = await _store.GetFoldersAsync(_cts.Token);
            }

            await LoadFailedAsync();
            RebuildList();
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh the failed file list");
        }
    }

    /// <summary>Remembers which folders had waiting files and finished, to show "剛更新 N 個檔案" for a while.</summary>
    private void TrackActivity(IndexingSnapshot snapshot)
    {
        var now = _time.GetUtcNow();
        foreach (var folder in snapshot.Folders)
        {
            if (folder.PendingFiles > 0)
            {
                _busyPeak[folder.FolderId] = Math.Max(_busyPeak.GetValueOrDefault(folder.FolderId), folder.PendingFiles);
            }
            else if (_busyPeak.Remove(folder.FolderId, out var peak) && folder.State == FolderState.Active)
            {
                _updated[folder.FolderId] = (peak, now);
            }
        }
    }

    private void UpdateProgressCard(IndexingSnapshot snapshot)
    {
        IsPaused = snapshot.State == IndexingState.Paused;
        IsProgressVisible = snapshot.State is IndexingState.Scanning or IndexingState.Indexing or IndexingState.Paused;
        if (!IsProgressVisible)
        {
            return;
        }

        var hasTotal = snapshot.TotalFiles > 0;
        IsProgressIndeterminate = !hasTotal && !IsPaused;
        ProgressValue = hasTotal ? Math.Clamp(snapshot.ProcessedFiles * 100.0 / snapshot.TotalFiles, 0, 100) : 0;
        ProgressTitle = IsPaused ? "已暫停" : "正在讀取你的檔案";
        ProgressChipText = IsPaused ? "已暫停" : "處理中";

        if (!hasTotal)
        {
            ProgressCountText = snapshot.State == IndexingState.Scanning ? "正在檢查資料夾裡有哪些檔案" : "";
        }
        else
        {
            var text = $"{snapshot.ProcessedFiles:N0} / {snapshot.TotalFiles:N0} 個檔案";
            if (!IsPaused)
            {
                text += snapshot.EstimatedRemaining is { } remaining
                    ? $" · 預估還要約 {TimeText.Remaining(remaining)}"
                    : " · 正在估算剩餘時間";
            }

            ProgressCountText = text;
        }

        CurrentFileText = string.IsNullOrEmpty(snapshot.CurrentFile) ? "" : "目前：" + PathRelations.GetName(snapshot.CurrentFile);
    }

    // ---- Building the list ------------------------------------------------------------------------------------

    private void RebuildList()
    {
        var now = _time.GetUtcNow();
        var progress = _snapshot.Folders.ToDictionary(p => p.FolderId);
        var recentActivity = _snapshot.RecentActivity.Any(a => a.Kind is ActivityKind.Updated or ActivityKind.Added && now - a.At < RecentWindow);

        foreach (var stale in _rows.Keys.Except(_folders.Select(f => f.Id)).ToList())
        {
            _rows.Remove(stale);
            _updated.Remove(stale);
            _busyPeak.Remove(stale);
        }

        var rows = new List<FolderRowViewModel>(_folders.Count);
        foreach (var folder in _folders)
        {
            if (!_rows.TryGetValue(folder.Id, out var row))
            {
                row = new FolderRowViewModel(this, folder.Id);
                _rows[folder.Id] = row;
            }

            progress.TryGetValue(folder.Id, out var p);
            Describe(folder, p, now, recentActivity, out var count, out var status, out var tone, out var group);
            row.Update(folder.DisplayName, folder.Path, count, status, tone, group);
            rows.Add(row);
        }

        var problem = rows.Count(r => r.Group == FolderGroup.Problem);
        var running = rows.Count(r => r.Group == FolderGroup.Running);
        var done = rows.Count(r => r.Group == FolderGroup.Done);
        _allOption.Label = $"全部 {rows.Count}";
        _problemOption.Label = $"有問題 {problem}";
        _runningOption.Label = $"處理中 {running}";
        _doneOption.Label = $"已完成 {done}";

        HasFolders = rows.Count > 0;
        ShowSearchBox = rows.Count > SearchBoxThreshold;
        UpdateBanner();

        var query = ShowSearchBox ? SearchText.Trim() : "";
        var filter = (SelectedFilter ?? _allOption).Kind;
        var matching = rows
            .Where(r => query.Length == 0
                || r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || r.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var desired = new List<object>();
        foreach (var group in new[] { FolderGroup.Problem, FolderGroup.Running, FolderGroup.Done })
        {
            if (filter != FolderFilter.All && (int)filter != (int)group + 1)
            {
                continue;
            }

            var items = matching.Where(r => r.Group == group).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (items.Count == 0)
            {
                continue;
            }

            var label = group switch
            {
                FolderGroup.Problem => "有問題",
                FolderGroup.Running => "處理中",
                _ => "已完成",
            };
            var canToggle = group == FolderGroup.Done && filter == FolderFilter.All && query.Length == 0;
            var collapsed = canToggle && !_doneExpanded;
            desired.Add(new FolderGroupHeader(
                $"{label} · {items.Count} 個資料夾",
                canToggle,
                collapsed ? "展開" : "收合",
                canToggle ? ToggleDoneCommand : null));
            if (!collapsed)
            {
                desired.AddRange(items);
            }
        }

        IsEmptyHintVisible = rows.Count == 0;
        IsNoMatchVisible = rows.Count > 0 && desired.Count == 0;

        if (!ListItems.SequenceEqual(desired))
        {
            ListItems.Clear();
            foreach (var item in desired)
            {
                ListItems.Add(item);
            }
        }
    }

    private void Describe(
        WatchedFolder folder,
        FolderProgress? progress,
        DateTimeOffset now,
        bool recentActivity,
        out string count,
        out string status,
        out StatusTone tone,
        out FolderGroup group)
    {
        var state = progress?.State ?? folder.State;
        var total = progress?.TotalFiles;
        var totalText = total is null ? "—" : total.Value.ToString("N0");

        if (state != FolderState.Active)
        {
            count = totalText;
            status = ErrorText.ToText(state);
            tone = StatusTone.Warn;
            group = FolderGroup.Problem;
            return;
        }

        var queued = progress is null && folder.LastScanAt is null;
        if (queued || progress is { PendingFiles: > 0 })
        {
            group = FolderGroup.Running;
            if (_snapshot.State == IndexingState.Paused)
            {
                count = progress is null ? "—" : $"{progress.IndexedFiles:N0} / {progress.TotalFiles:N0}";
                status = "已暫停";
                tone = StatusTone.Muted;
            }
            else if (progress is null || progress.IndexedFiles == 0 || progress.TotalFiles == 0)
            {
                count = progress is null ? "—" : $"{progress.IndexedFiles:N0} / {progress.TotalFiles:N0}";
                status = "排隊中";
                tone = StatusTone.Running;
            }
            else
            {
                count = $"{progress.IndexedFiles:N0} / {progress.TotalFiles:N0}";
                status = $"處理中 {Math.Clamp(progress.IndexedFiles * 100 / progress.TotalFiles, 0, 99)}%";
                tone = StatusTone.Running;
            }

            return;
        }

        group = FolderGroup.Done;
        count = totalText;
        tone = StatusTone.Ok;
        status = recentActivity && _updated.TryGetValue(folder.Id, out var updated) && now - updated.At < RecentWindow
            ? $"剛更新 {updated.Count:N0} 個檔案"
            : "已完成";
    }

    private void UpdateBanner()
    {
        var unavailable = _folders
            .Where(f => (_snapshot.Folders.FirstOrDefault(p => p.FolderId == f.Id)?.State ?? f.State) == FolderState.Unavailable)
            .Select(f => f.DisplayName)
            .ToList();
        ErrorBannerText = unavailable.Count switch
        {
            0 => "",
            1 => $"發生錯誤：『{unavailable[0]}』目前無法存取，已暫停這個資料夾。",
            <= 3 => $"發生錯誤：{string.Join("、", unavailable.Select(n => $"『{n}』"))}目前無法存取，已暫停這些資料夾。",
            _ => $"發生錯誤：『{unavailable[0]}』等 {unavailable.Count} 個資料夾目前無法存取，已暫停這些資料夾。",
        };
    }

    private void RebuildFailed()
    {
        var folderById = _folders.ToDictionary(f => f.Id);
        var wanted = _failedDocuments.Select(d => (d.Id, d.ErrorCode)).ToList();
        var current = AllFailed.Select(r => (Id: r.DocumentId, Code: r.ErrorCode)).ToList();

        if (!wanted.SequenceEqual(current))
        {
            AllFailed.Clear();
            foreach (var doc in _failedDocuments)
            {
                AllFailed.Add(new FailedFileRowViewModel(this, doc, LocationOf(doc, folderById)));
            }

            TopFailed.Clear();
            foreach (var row in AllFailed.Take(FailedPreviewCount))
            {
                TopFailed.Add(row);
            }
        }

        var total = AllFailed.Count;
        HasFailed = total > 0;
        FailedTitle = $"有 {total:N0} 個檔案無法讀取";
        HasMoreFailed = total > FailedPreviewCount;
        ShowAllFailedText = $"查看全部 {total:N0} 個 ›";
    }

    private static string LocationOf(DocumentRecord doc, Dictionary<long, WatchedFolder> folders)
    {
        var parts = new List<string>();
        if (folders.TryGetValue(doc.FolderId, out var folder))
        {
            parts.Add(folder.DisplayName);
            parts.AddRange(PathRelations.GetDirectoriesBelow(folder.Path, doc.Path));
        }
        else
        {
            var normalized = PathRelations.Normalize(doc.Path);
            var index = normalized.LastIndexOf('/');
            parts.Add(index > 0 ? PathRelations.GetName(normalized[..index]) : "");
        }

        return string.Join(" › ", parts.Where(p => p.Length > 0));
    }

    private void StartExpiryTimer()
    {
        lock (_gate)
        {
            if (_disposed || _expiryTimer is not null)
            {
                return;
            }

            // Lets "剛更新 N 個檔案" disappear after five minutes even when nothing else changes.
            _expiryTimer = _time.CreateTimer(
                _ => _dispatcher.Post(() =>
                {
                    if (_active)
                    {
                        RebuildList();
                    }
                }),
                null,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30));
        }
    }

    // ---- Commands on the page ---------------------------------------------------------------------------------

    [RelayCommand]
    private void ToggleDone()
    {
        _doneExpanded = !_doneExpanded;
        RebuildList();
    }

    [RelayCommand]
    private void DismissNotice() => NoticeText = "";

    [RelayCommand]
    private void ExportReport() => _navigation.NavigateTo<AboutViewModel>();

    [RelayCommand]
    private void PauseOrResume()
    {
        if (_indexing.Current.State == IndexingState.Paused)
        {
            _indexing.Resume();
        }
        else
        {
            _indexing.Pause();
        }
    }

    [RelayCommand]
    private async Task AddFolder()
    {
        var path = await _picker.PickFolderAsync(null);
        if (!string.IsNullOrWhiteSpace(path))
        {
            await AddFolderAsync(path);
        }
    }

    [RelayCommand]
    private void RetryAll() => _indexing.RequestRetry(null);

    [RelayCommand]
    private Task ShowAllFailed() => _dialogs.ShowAsync(new FailedFilesDialogViewModel(AllFailed));

    // ---- Adding folders ---------------------------------------------------------------------------------------

    /// <summary>Folders dropped on the window. Files are ignored.</summary>
    public async Task AddDroppedFoldersAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) && _tree.DirectoryExists(path))
            {
                await AddFolderAsync(path);
            }
        }
    }

    /// <summary>
    /// Adds one folder. A folder already inside a watched folder is not added (a note is shown instead);
    /// a folder that contains watched folders asks whether to merge them into one.
    /// </summary>
    public async Task AddFolderAsync(string path)
    {
        var name = PathRelations.GetName(path);
        try
        {
            if (!_tree.DirectoryExists(path))
            {
                ShowNotice($"找不到『{name}』，可能已被移動或刪除。");
                return;
            }

            var current = await _store.GetFoldersAsync(_cts.Token);
            var same = current.FirstOrDefault(f => PathRelations.AreSame(f.Path, path));
            if (same is not null)
            {
                ShowNotice($"『{same.DisplayName}』已經加入了。");
                return;
            }

            var container = current.FirstOrDefault(f => PathRelations.IsInside(f.Path, path));
            if (container is not null)
            {
                ShowNotice($"『{name}』已經包含在『{container.DisplayName}』裡，不需要重複加入。");
                return;
            }

            var inner = current.Where(f => PathRelations.IsInside(path, f.Path)).ToList();
            if (inner.Count > 0)
            {
                var names = string.Join("、", inner.Select(f => $"『{f.DisplayName}』"));
                var merge = await _dialogs.ConfirmAsync(new ConfirmRequest(
                    "合併資料夾",
                    [$"『{name}』包含了已加入的{names}，要合併成一個嗎？", "合併後會重新讀取，你的原始檔案不會被刪除。"],
                    "合併"));
                if (!merge)
                {
                    return;
                }

                foreach (var child in inner)
                {
                    await _store.RemoveFolderAsync(child.Id, _cts.Token);
                }
            }

            var added = await _store.AddFolderAsync(path, _cts.Token);
            _indexing.RequestRescan(added.Id);
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not add a folder");
            ShowNotice($"沒有辦法加入『{name}』，請稍後再試。");
        }
    }

    // ---- Actions of a folder row ------------------------------------------------------------------------------

    internal async Task OpenSubfolderPickerAsync(FolderRowViewModel row)
    {
        var folder = _folders.FirstOrDefault(f => f.Id == row.Id);
        if (folder is null)
        {
            return;
        }

        using var dialog = new SubfolderPickerViewModel(folder, _tree, _store, _indexing, _logger);
        _ = dialog.LoadAsync();
        await _dialogs.ShowAsync(dialog);
        if (dialog.Saved)
        {
            await RefreshAsync();
        }
    }

    internal void OpenFolderInFileManager(FolderRowViewModel row) =>
        TryLaunch(() => _launcher.OpenFolder(row.Path), row.Name);

    internal void RevealFolderInFileManager(FolderRowViewModel row) =>
        TryLaunch(() => _launcher.RevealInFileManager(row.Path), row.Name);

    internal void RescanFolder(FolderRowViewModel row) => _indexing.RequestRescan(row.Id);

    internal async Task RemoveFolderAsync(FolderRowViewModel row)
    {
        var confirmed = await _dialogs.ConfirmAsync(new ConfirmRequest(
            "移除資料夾",
            [
                $"要移除「{row.Name}」嗎？",
                "移除後，AI 將查不到這個資料夾的內容。",
                "你的原始檔案不會被刪除，仍然留在原本的位置。",
                "之後重新加入，需要重新讀取這個資料夾。",
            ],
            "移除資料夾",
            IsDestructive: true));
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _store.RemoveFolderAsync(row.Id, _cts.Token);
            ShowNotice($"已移除「{row.Name}」的資料，原始檔案保留在原處。");
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not remove a folder");
            ShowNotice($"沒有辦法移除『{row.Name}』，請稍後再試。");
        }
    }

    internal Task ExcludeFolderAsync(FolderRowViewModel row) => ExcludeAsync(row.Path, true, row.Name);

    // ---- Actions of a failed file -----------------------------------------------------------------------------

    internal async Task RunFailedActionAsync(FailedFileRowViewModel row)
    {
        if (row.Action == FailedFileAction.Retry)
        {
            _indexing.RequestRetry(row.DocumentId);
            ShowNotice($"已重新嘗試讀取『{row.FileName}』，結果會在稍後更新。");
            return;
        }

        try
        {
            await _store.AddExclusionAsync(row.Path, false, _cts.Token);
            ShowNotice($"已略過『{row.FileName}』，AI 不會再讀取它。之後可以在「設定」中恢復。");
            await LoadFailedAsync();
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not skip a file");
            ShowNotice($"沒有辦法略過『{row.FileName}』，請稍後再試。");
        }
    }

    internal void OpenFile(FailedFileRowViewModel row) => TryLaunch(() => _launcher.OpenFile(row.Path), row.FileName);

    internal void RevealFile(FailedFileRowViewModel row) => TryLaunch(() => _launcher.RevealInFileManager(row.Path), row.FileName);

    internal Task ExcludeFileAsync(FailedFileRowViewModel row) => ExcludeAsync(row.Path, false, row.FileName);

    private async Task ExcludeAsync(string path, bool isFolder, string name)
    {
        var kind = isFolder ? "資料夾" : "檔案";
        var confirmed = await _dialogs.ConfirmAsync(new ConfirmRequest(
            $"不要讓 AI 讀這個{kind}",
            [
                $"之後 AI 查不到「{name}」的內容，已經讀取的資料也會一併移除。",
                "你的原始檔案不會被刪除或修改。",
                "之後可以在「設定」中恢復。",
            ],
            "不要讓 AI 讀取",
            IsDestructive: true));
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _store.AddExclusionAsync(path, isFolder, _cts.Token);
            ShowNotice($"已設定 AI 不讀取『{name}』。之後可以在「設定」中恢復。");
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not add an exclusion");
            ShowNotice($"沒有辦法設定『{name}』，請稍後再試。");
        }
    }

    private void TryLaunch(Action action, string name)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open an item in the file manager");
            ShowNotice($"沒有辦法開啟『{name}』。");
        }
    }

    private void ShowNotice(string text) => _dispatcher.Post(() => NoticeText = text);

    // ---- Large disappearance question -------------------------------------------------------------------------

    private void OnMassDeletionPending(object? sender, MassDeletionPending pending) => _ = HandleMassDeletionAsync(pending);

    /// <summary>
    /// Asks whether the missing files were really deleted. "保留" (and Esc) keeps the data; only the danger button deletes it.
    /// The answer is always passed to <see cref="IIndexingService.ResolveMassDeletionAsync"/>.
    /// </summary>
    public async Task HandleMassDeletionAsync(MassDeletionPending pending)
    {
        lock (_gate)
        {
            if (!_asking.Add(pending.FolderId))
            {
                return;
            }
        }

        try
        {
            var name = _folders.FirstOrDefault(f => f.Id == pending.FolderId)?.DisplayName
                ?? (await _store.GetFoldersAsync(CancellationToken.None)).FirstOrDefault(f => f.Id == pending.FolderId)?.DisplayName
                ?? "這個資料夾";

            var deleteMissing = await _dialogs.ConfirmAsync(new ConfirmRequest(
                $"『{name}』裡有 {pending.MissingFileCount:N0} 個檔案不見了",
                [
                    "如果你確實刪除或搬走了這些檔案，按「移除這些資料」；如果只是暫時無法存取（例如外接硬碟沒插），按「保留」。",
                    "你的原始檔案不受影響。",
                ],
                "移除這些資料",
                IsDestructive: true,
                CancelText: "保留"));

            await _indexing.ResolveMassDeletionAsync(pending.FolderId, deleteMissing, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not answer the missing files question");
        }
        finally
        {
            lock (_gate)
            {
                _asking.Remove(pending.FolderId);
            }

            _dispatcher.Post(() => PendingRefresh = RefreshAsync());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _throttleTimer?.Dispose();
            _expiryTimer?.Dispose();
        }

        if (!IsInert)
        {
            _indexing.SnapshotChanged -= OnSnapshotChanged;
            _indexing.MassDeletionPendingRaised -= OnMassDeletionPending;
        }

        _cts.Cancel();
        _cts.Dispose();
    }
}
