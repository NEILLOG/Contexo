using Contexo.App.Shell;
using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contexo.App.Tests.Shell;

public sealed class StatusBarViewModelTests
{
    private readonly FakeIndexingService _indexing = new();
    private readonly FakeAiClientStatusService _clients = new();
    private readonly ManualTimeProvider _time = new();

    private StatusBarViewModel Create() =>
        new(_indexing, _clients, new InlineDispatcher(), _time, NullLogger<StatusBarViewModel>.Instance);

    private static IndexingSnapshot Snapshot(IndexingState state, int total = 0, int done = 0, params RecentActivity[] activity) =>
        new(state, total, done, null, null, [], activity);

    [Fact]
    public void Starts_with_the_current_snapshot()
    {
        using var vm = Create();

        Assert.Equal("已是最新", vm.ProgressText);
        Assert.Equal(StatusTone.Ok, vm.ProgressTone);
        Assert.False(vm.HasActivity);
        Assert.StartsWith("v", vm.Version);
    }

    [Fact]
    public void Shows_percentage_while_indexing()
    {
        using var vm = Create();

        _indexing.Raise(Snapshot(IndexingState.Indexing, total: 200, done: 76));

        Assert.Equal("處理中 38%", vm.ProgressText);
        Assert.Equal(StatusTone.Running, vm.ProgressTone);
    }

    [Fact]
    public void Shows_zero_percent_when_total_is_unknown()
    {
        using var vm = Create();

        _indexing.Raise(Snapshot(IndexingState.Indexing, total: 0, done: 0));

        Assert.Equal("處理中 0%", vm.ProgressText);
    }

    [Fact]
    public void Shows_up_to_date_after_work_finishes()
    {
        using var vm = Create();
        _indexing.Raise(Snapshot(IndexingState.Indexing, 10, 5));
        _time.Advance(TimeSpan.FromSeconds(1));

        _indexing.Raise(Snapshot(IndexingState.Idle, 10, 10));

        Assert.Equal("已是最新", vm.ProgressText);
        Assert.Equal(StatusTone.Ok, vm.ProgressTone);
    }

    [Fact]
    public void Shows_paused_and_scanning()
    {
        using var vm = Create();

        _indexing.Raise(Snapshot(IndexingState.Paused, 10, 5));
        Assert.Equal("已暫停", vm.ProgressText);
        Assert.Equal(StatusTone.Warn, vm.ProgressTone);

        _time.Advance(TimeSpan.FromSeconds(1));
        _indexing.Raise(Snapshot(IndexingState.Scanning));
        Assert.Equal("正在檢查資料夾", vm.ProgressText);
    }

    [Fact]
    public void Describes_recent_activity_in_plain_words()
    {
        using var vm = Create();
        var now = _time.GetUtcNow();

        _indexing.Raise(Snapshot(
            IndexingState.Idle,
            activity:
            [
                new RecentActivity(ActivityKind.Removed, 2, now - TimeSpan.FromSeconds(50)),
                new RecentActivity(ActivityKind.Updated, 3, now - TimeSpan.FromSeconds(5)),
            ]));

        Assert.Equal("剛剛更新了 3 個檔案 · 已移除 2 個已刪除檔案的資料", vm.ActivityText);
        Assert.True(vm.HasActivity);
    }

    [Fact]
    public void Activity_time_text_ages()
    {
        var now = _time.Start;

        Assert.Equal("5 分鐘前新增了 1 個檔案", StatusBarViewModel.DescribeActivity([new(ActivityKind.Added, 1, now - TimeSpan.FromMinutes(5))], now));
        Assert.Equal("3 小時前更新了 4 個檔案", StatusBarViewModel.DescribeActivity([new(ActivityKind.Updated, 4, now - TimeSpan.FromHours(3))], now));
        Assert.Equal("2 天前整理了 7 個移動過的檔案", StatusBarViewModel.DescribeActivity([new(ActivityKind.Moved, 7, now - TimeSpan.FromDays(2))], now));
        Assert.Equal("", StatusBarViewModel.DescribeActivity([], now));
    }

    [Fact]
    public void Throttles_updates_to_one_per_250_ms_and_keeps_the_newest()
    {
        using var vm = Create();
        var changes = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StatusBarViewModel.ProgressText))
            {
                changes.Add(vm.ProgressText);
            }
        };

        _indexing.Raise(Snapshot(IndexingState.Indexing, 100, 10)); // applied immediately
        _time.Advance(TimeSpan.FromMilliseconds(50));
        _indexing.Raise(Snapshot(IndexingState.Indexing, 100, 20)); // held back
        _time.Advance(TimeSpan.FromMilliseconds(50));
        _indexing.Raise(Snapshot(IndexingState.Indexing, 100, 30)); // replaces the held one

        Assert.Equal(["處理中 10%"], changes);

        _time.Advance(TimeSpan.FromMilliseconds(149));
        Assert.Equal(["處理中 10%"], changes);

        _time.Advance(TimeSpan.FromMilliseconds(2));
        Assert.Equal(["處理中 10%", "處理中 30%"], changes);

        // After the quiet period the next event is applied immediately again.
        _time.Advance(TimeSpan.FromSeconds(1));
        _indexing.Raise(Snapshot(IndexingState.Indexing, 100, 50));
        Assert.Equal("處理中 50%", changes[^1]);
    }

    [Fact]
    public async Task Shows_the_best_ai_client_status_or_nothing()
    {
        using var vm = Create();

        await vm.RefreshAiClientsAsync();
        Assert.False(vm.HasAiClient);

        _clients.Statuses.Add(new AiClientStatus("a", "Cursor", ClientConnectionState.NotInstalled, null, null, null));
        _clients.Statuses.Add(new AiClientStatus("b", "VS Code", ClientConnectionState.NeedsRepair, null, null, null));
        await vm.RefreshAiClientsAsync();
        Assert.Equal("VS Code 需要修復", vm.AiClientText);
        Assert.Equal(StatusTone.Warn, vm.AiClientTone);

        _clients.Statuses.Add(new AiClientStatus("c", "Claude Desktop", ClientConnectionState.Connected, null, null, null));
        await vm.RefreshAiClientsAsync();
        Assert.Equal("Claude Desktop 已連線", vm.AiClientText);
        Assert.Equal(StatusTone.Ok, vm.AiClientTone);
        Assert.True(vm.HasAiClient);
    }

    [Fact]
    public void Polls_ai_clients_every_30_seconds_after_start()
    {
        using var vm = Create();

        vm.Start();
        Assert.Equal(1, _time.ActiveTimerCount); // the poll timer only
        _time.Advance(TimeSpan.Zero);
        Assert.Equal(1, _clients.CallCount);

        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(1, _clients.CallCount);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, _clients.CallCount);
    }

    [Fact]
    public void Stops_listening_after_dispose()
    {
        var vm = Create();
        vm.Dispose();

        _indexing.Raise(Snapshot(IndexingState.Paused, 10, 5));

        Assert.Equal("已是最新", vm.ProgressText);
    }
}
