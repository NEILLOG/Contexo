using Contexo.App.Services;
using Contexo.Core.Abstractions;

namespace Contexo.App.Tests.Shell;

/// <summary>Manually advanced clock with timers, so throttling can be tested without sleeping.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private TimeSpan _elapsed;

    public DateTimeOffset Start { get; } = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Start + _elapsed;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _elapsed.Ticks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    public int ActiveTimerCount => _timers.Count(t => t.Active);

    public void Advance(TimeSpan by)
    {
        var target = _elapsed + by;
        while (true)
        {
            var next = _timers.Where(t => t.Active && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
            if (next is null)
            {
                break;
            }

            _elapsed = next.Due > _elapsed ? next.Due : _elapsed;
            next.Fire();
        }

        _elapsed = target;
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period;

        public bool Active { get; private set; }

        public TimeSpan Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Active = dueTime != Timeout.InfiniteTimeSpan;
            Due = owner._elapsed + (Active ? dueTime : TimeSpan.Zero);
            _period = period;
            return true;
        }

        public void Fire()
        {
            if (_period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero)
            {
                Active = false;
            }
            else
            {
                Due += _period;
            }

            callback(state);
        }

        public void Dispose() => Active = false;

        public ValueTask DisposeAsync()
        {
            Active = false;
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

internal sealed class FakeIndexingService : IIndexingService
{
    public IndexingSnapshot Current { get; private set; } = IndexingSnapshot.Initial;

    public event EventHandler<IndexingSnapshot>? SnapshotChanged;

    public event EventHandler<MassDeletionPending>? MassDeletionPendingRaised
    {
        add { }
        remove { }
    }

    public void Raise(IndexingSnapshot snapshot)
    {
        Current = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Pause()
    {
    }

    public void Resume()
    {
    }

    public void RequestRescan(long? folderId)
    {
    }

    public void RequestRetry(long? documentId)
    {
    }

    public Task ResolveMassDeletionAsync(long folderId, bool deleteMissing, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class FakeAiClientStatusService : IAiClientStatusService
{
    public List<AiClientStatus> Statuses { get; } = [];

    public int CallCount { get; private set; }

    public IReadOnlyList<IAiClientIntegration> Integrations => [];

    public McpServerLaunch CurrentLaunch => new("contexo-mcp", []);

    public Task<IReadOnlyList<AiClientStatus>> GetStatusesAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult<IReadOnlyList<AiClientStatus>>(Statuses.ToList());
    }
}

internal sealed class FakeSettingsStore : ISettingsStore
{
    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? Changed;

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Current = settings;
        Changed?.Invoke(this, settings);
        return Task.CompletedTask;
    }
}
