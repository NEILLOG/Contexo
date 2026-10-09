#pragma warning disable CS8524 // Unnamed enum values are deliberately not covered, so a missing named member (CS8509) still fails the build.

using CommunityToolkit.Mvvm.ComponentModel;
using Contexo.App.Services;
using Contexo.App.UserMessages;
using Contexo.App.ViewModels;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Microsoft.Extensions.Logging;

namespace Contexo.App.Shell;

/// <summary>Colour of the small dot in front of a status bar item. The view maps these to Brush.* resources.</summary>
public enum StatusTone
{
    Muted,
    Ok,
    Running,
    Warn,
}

/// <summary>
/// Bottom status bar: indexing progress, recent activity, AI client connection summary and version.
/// Snapshot events arrive on background threads; updates are marshalled with <see cref="IUiDispatcher"/> and throttled.
/// </summary>
public sealed partial class StatusBarViewModel : ViewModelBase, IDisposable
{
    /// <summary>Minimum time between two visible updates caused by snapshot events.</summary>
    public static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(250);

    public static readonly TimeSpan AiClientPollInterval = TimeSpan.FromSeconds(30);

    private readonly IIndexingService _indexing;
    private readonly IAiClientStatusService _clients;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<StatusBarViewModel> _logger;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();

    private IndexingSnapshot _pending;
    private long _lastAppliedTimestamp;
    private bool _hasApplied;
    private ITimer? _throttleTimer;
    private ITimer? _pollTimer;
    private bool _disposed;

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = "";

    [ObservableProperty]
    public partial StatusTone ProgressTone { get; private set; } = StatusTone.Ok;

    [ObservableProperty]
    public partial string ActivityText { get; private set; } = "";

    [ObservableProperty]
    public partial string AiClientText { get; private set; } = "";

    [ObservableProperty]
    public partial StatusTone AiClientTone { get; private set; } = StatusTone.Muted;

    public StatusBarViewModel(
        IIndexingService indexing,
        IAiClientStatusService clients,
        IUiDispatcher dispatcher,
        TimeProvider time,
        ILogger<StatusBarViewModel> logger)
    {
        _indexing = indexing;
        _clients = clients;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;

        Version = "v" + AppVersion.Current.Version;
        _pending = indexing.Current;
        ApplySnapshot(_pending);
        _indexing.SnapshotChanged += OnSnapshotChanged;
    }

    public string Version { get; }

    public bool HasActivity => ActivityText.Length > 0;

    public bool HasAiClient => AiClientText.Length > 0;

    partial void OnActivityTextChanged(string value) => OnPropertyChanged(nameof(HasActivity));

    partial void OnAiClientTextChanged(string value) => OnPropertyChanged(nameof(HasAiClient));

    /// <summary>Starts the 30 second AI client poll (and one immediate refresh). Call once from the UI thread.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _pollTimer is not null)
            {
                return;
            }

            _pollTimer = _time.CreateTimer(
                _ => _ = RefreshAiClientsAsync(),
                null,
                TimeSpan.Zero,
                AiClientPollInterval);
        }
    }

    /// <summary>Reads the AI client statuses once and updates the summary. Never throws.</summary>
    public async Task RefreshAiClientsAsync()
    {
        try
        {
            var statuses = await _clients.GetStatusesAsync(_cts.Token).ConfigureAwait(false);
            var (text, tone) = SummarizeClients(statuses);
            _dispatcher.Post(() =>
            {
                AiClientText = text;
                AiClientTone = tone;
                RefreshRelativeTimes();
            });
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read AI client status");
        }
    }

    internal static (string Text, StatusTone Tone) SummarizeClients(IReadOnlyList<AiClientStatus> statuses)
    {
        var best = statuses
            .Where(s => Rank(s.State) > 0)
            .OrderByDescending(s => Rank(s.State))
            .FirstOrDefault();

        if (best is null)
        {
            return ("", StatusTone.Muted);
        }

        var tone = best.State switch
        {
            ClientConnectionState.Connected => StatusTone.Ok,
            ClientConnectionState.WaitingForConnection => StatusTone.Running,
            _ => StatusTone.Warn,
        };
        return ($"{best.DisplayName} {ErrorText.ToText(best.State)}", tone);

        static int Rank(ClientConnectionState state) => state switch
        {
            ClientConnectionState.Connected => 3,
            ClientConnectionState.WaitingForConnection => 2,
            ClientConnectionState.NeedsRepair => 1,
            ClientConnectionState.NotInstalled => 0,
            ClientConnectionState.NotAdded => 0,
        };
    }

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
        _dispatcher.Post(() => ApplySnapshot(snapshot));
    }

    private IndexingSnapshot _lastApplied = IndexingSnapshot.Initial;

    private void ApplySnapshot(IndexingSnapshot snapshot)
    {
        _lastApplied = snapshot;
        (ProgressText, ProgressTone) = DescribeProgress(snapshot);
        RefreshRelativeTimes();
    }

    private void RefreshRelativeTimes() => ActivityText = DescribeActivity(_lastApplied.RecentActivity, _time.GetUtcNow());

    internal static (string Text, StatusTone Tone) DescribeProgress(IndexingSnapshot snapshot) => snapshot.State switch
    {
        IndexingState.Idle => ("已是最新", StatusTone.Ok),
        IndexingState.Scanning => ("正在檢查資料夾", StatusTone.Running),
        IndexingState.Indexing => ($"處理中 {Percent(snapshot)}%", StatusTone.Running),
        IndexingState.Paused => ("已暫停", StatusTone.Warn),
    };

    private static int Percent(IndexingSnapshot snapshot) =>
        snapshot.TotalFiles <= 0 ? 0 : Math.Clamp((int)(snapshot.ProcessedFiles * 100L / snapshot.TotalFiles), 0, 100);

    /// <summary>"剛剛更新了 3 個檔案 · 已移除 2 個已刪除檔案的資料": newest activity with a time prefix, plus at most one more.</summary>
    internal static string DescribeActivity(IReadOnlyList<RecentActivity> activities, DateTimeOffset now)
    {
        if (activities.Count == 0)
        {
            return "";
        }

        var ordered = activities.OrderByDescending(a => a.At).Take(2).ToList();
        var first = RelativeTime(now - ordered[0].At) + Describe(ordered[0]);
        return ordered.Count == 1 ? first : first + " · " + Describe(ordered[1]);

        static string Describe(RecentActivity a) => a.Kind switch
        {
            ActivityKind.Updated => $"更新了 {a.FileCount} 個檔案",
            ActivityKind.Added => $"新增了 {a.FileCount} 個檔案",
            ActivityKind.Removed => $"已移除 {a.FileCount} 個已刪除檔案的資料",
            ActivityKind.Moved => $"整理了 {a.FileCount} 個移動過的檔案",
        };
    }

    private static string RelativeTime(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
        {
            return "剛剛";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} 分鐘前";
        }

        return age < TimeSpan.FromDays(1) ? $"{(int)age.TotalHours} 小時前" : $"{(int)age.TotalDays} 天前";
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
            _indexing.SnapshotChanged -= OnSnapshotChanged;
            _throttleTimer?.Dispose();
            _pollTimer?.Dispose();
        }

        _cts.Cancel();
        _cts.Dispose();
    }
}
