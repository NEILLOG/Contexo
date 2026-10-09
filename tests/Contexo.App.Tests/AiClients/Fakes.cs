using Contexo.App.Services;
using Contexo.Core.Abstractions;

namespace Contexo.App.Tests.AiClients;

/// <summary>Manually advanced clock (UTC local zone unless changed) whose timers fire only when <see cref="Advance"/> is called.</summary>
internal sealed class TestClock : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private TimeSpan _elapsed;

    public TestClock(DateTimeOffset? start = null, TimeZoneInfo? zone = null)
    {
        Start = start ?? new DateTimeOffset(2026, 10, 9, 14, 40, 0, TimeSpan.Zero);
        Zone = zone ?? TimeZoneInfo.Utc;
    }

    public DateTimeOffset Start { get; }

    public TimeZoneInfo Zone { get; }

    public override TimeZoneInfo LocalTimeZone => Zone;

    public override DateTimeOffset GetUtcNow() => Start + _elapsed;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _elapsed.Ticks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    public int ActiveTimerCount
    {
        get
        {
            lock (_timers)
            {
                return _timers.Count(t => t.Active);
            }
        }
    }

    public void Advance(TimeSpan by)
    {
        var target = _elapsed + by;
        while (true)
        {
            ManualTimer? next;
            lock (_timers)
            {
                next = _timers.Where(t => t.Active && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
            }

            if (next is null)
            {
                break;
            }

            _elapsed = next.Due > _elapsed ? next.Due : _elapsed;
            next.Fire();
        }

        _elapsed = target;
    }

    private sealed class ManualTimer(TestClock owner, TimerCallback callback, object? state) : ITimer
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

internal sealed class InlineUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

internal sealed class FakeIntegration(string clientId, string displayName) : IAiClientIntegration
{
    public string ClientId { get; } = clientId;

    public string DisplayName { get; } = displayName;

    public IReadOnlyCollection<string> KnownClientNames => [];

    public int AddCalls { get; private set; }

    public int RemoveCalls { get; private set; }

    public McpServerLaunch? LastLaunch { get; private set; }

    public int? AddThreadId { get; private set; }

    public Exception? AddFails { get; set; }

    public Exception? RemoveFails { get; set; }

    public Action? OnAdd { get; set; }

    public Action? OnRemove { get; set; }

    public string Snippet { get; set; } = "{ \"mcpServers\": {} }";

    public McpServerLaunch? SnippetLaunch { get; private set; }

    public ClientConfigState GetConfigState() => ClientConfigState.NotConfigured;

    public void AddOrRepair(McpServerLaunch launch)
    {
        AddCalls++;
        LastLaunch = launch;
        AddThreadId = Environment.CurrentManagedThreadId;
        if (AddFails is not null)
        {
            throw AddFails;
        }

        OnAdd?.Invoke();
    }

    public void Remove()
    {
        RemoveCalls++;
        if (RemoveFails is not null)
        {
            throw RemoveFails;
        }

        OnRemove?.Invoke();
    }

    public string BuildManualSnippet(McpServerLaunch launch)
    {
        SnippetLaunch = launch;
        return Snippet;
    }
}

internal sealed class FakeStatusService : IAiClientStatusService
{
    private int _calls;

    public List<FakeIntegration> Fakes { get; } = [];

    public List<AiClientStatus> Statuses { get; } = [];

    public int CallCount => Volatile.Read(ref _calls);

    public Exception? Fails { get; set; }

    public IReadOnlyList<IAiClientIntegration> Integrations => Fakes;

    public McpServerLaunch CurrentLaunch { get; } = new("C:\\Contexo\\Contexo.Mcp.exe", ["--db", "C:\\data\\contexo.db"]);

    public Task<IReadOnlyList<AiClientStatus>> GetStatusesAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        if (Fails is not null)
        {
            throw Fails;
        }

        AiClientStatus[] snapshot;
        lock (Statuses)
        {
            snapshot = [.. Statuses];
        }

        return Task.FromResult<IReadOnlyList<AiClientStatus>>(snapshot);
    }

    public FakeIntegration Add(string id, string name, ClientConnectionState state, DateTimeOffset? connected = null, DateTimeOffset? query = null, string? problem = null)
    {
        var fake = new FakeIntegration(id, name);
        Fakes.Add(fake);
        lock (Statuses)
        {
            Statuses.Add(new AiClientStatus(id, name, state, connected, query, problem));
        }

        return fake;
    }

    public void SetState(string id, ClientConnectionState state, string? problem = null)
    {
        lock (Statuses)
        {
            var index = Statuses.FindIndex(s => s.ClientId == id);
            Statuses[index] = Statuses[index] with { State = state, Problem = problem };
        }
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

internal sealed class FakeClipboard : IClipboardService
{
    public bool Succeeds { get; set; } = true;

    public bool Throws { get; set; }

    public List<string> Texts { get; } = [];

    public Task<bool> TrySetTextAsync(string text)
    {
        if (Throws)
        {
            throw new InvalidOperationException("clipboard busy");
        }

        Texts.Add(text);
        return Task.FromResult(Succeeds);
    }
}
