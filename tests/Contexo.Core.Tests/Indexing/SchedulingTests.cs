using Contexo.Core.Abstractions;
using Contexo.Core.Indexing;

namespace Contexo.Core.Tests.Indexing;

/// <summary>Pause, stop, throttling, progress numbers and the file system watchers.</summary>
public sealed class SchedulingTests
{
    private static readonly DateTime Old = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<(IndexingFixture Fixture, WatchedFolder Folder)> CreateAsync(IndexingOptions? options = null)
    {
        var fixture = await IndexingFixture.CreateAsync(options);
        var folder = await fixture.AddFolderAsync("docs");
        return (fixture, folder);
    }

    [Fact]
    public async Task Pausing_stops_new_files_and_resuming_finishes_the_job()
    {
        var (fixture, folder) = await CreateAsync();
        await using var _ = fixture;
        fixture.Parser.Delay = TimeSpan.FromMilliseconds(60);
        for (var i = 0; i < 20; i++)
        {
            fixture.WriteFile("docs", $"f{i:00}.txt", $"text {i}", Old.AddMinutes(i));
        }

        await fixture.StartAsync();
        await IndexingFixture.WaitUntilAsync(() => fixture.Parser.TotalCalls >= 3, what: "some files processed");
        fixture.Service.Pause();
        Assert.Equal(IndexingState.Paused, fixture.Service.Current.State);

        // The files already running finish; then nothing more starts.
        await Task.Delay(400);
        var callsWhenPaused = fixture.Parser.TotalCalls;
        var documentsWhenPaused = (await fixture.DocumentsAsync(folder.Id)).Count;
        await Task.Delay(400);
        Assert.Equal(callsWhenPaused, fixture.Parser.TotalCalls);
        Assert.Equal(documentsWhenPaused, (await fixture.DocumentsAsync(folder.Id)).Count);
        Assert.InRange(documentsWhenPaused, 1, 19);
        Assert.Equal(IndexingState.Paused, fixture.Service.Current.State);

        fixture.Service.Resume();
        await fixture.WaitIdleAsync();
        Assert.Equal(20, (await fixture.DocumentsAsync(folder.Id)).Count);
    }

    [Fact]
    public async Task Requests_made_while_paused_wait_for_resume()
    {
        var (fixture, folder) = await CreateAsync();
        await using var _ = fixture;
        fixture.Service.Pause();
        fixture.WriteFile("docs", "a.txt", "alpha");
        await fixture.StartAsync();
        await Task.Delay(300);

        Assert.Equal(IndexingState.Paused, fixture.Service.Current.State);
        Assert.Equal(0, fixture.Parser.TotalCalls);
        Assert.Empty(await fixture.DocumentsAsync(folder.Id));

        fixture.Service.Resume();
        await fixture.WaitIdleAsync();
        Assert.Single(await fixture.DocumentsAsync(folder.Id));
    }

    [Fact]
    public async Task Stop_returns_quickly_even_while_a_file_is_hanging()
    {
        var (fixture, _) = await CreateAsync();
        await using var disposable = fixture;
        fixture.WriteFile("docs", "hang1.txt", "HANG one");
        fixture.WriteFile("docs", "hang2.txt", "HANG two");
        fixture.WriteFile("docs", "hang3.txt", "HANG three");
        await fixture.StartAsync();
        await IndexingFixture.WaitUntilAsync(() => fixture.Parser.MaxConcurrent >= 2, what: "parsers hanging");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await fixture.Service.StopAsync(CancellationToken.None);
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"stop took {watch.Elapsed}");

        // Nothing keeps running in the background.
        int events;
        lock (fixture.Snapshots)
        {
            events = fixture.Snapshots.Count;
        }

        await Task.Delay(300);
        lock (fixture.Snapshots)
        {
            Assert.Equal(events, fixture.Snapshots.Count);
        }
    }

    [Fact]
    public async Task Idle_users_get_two_workers_and_active_users_get_one()
    {
        var (fixture, _) = await CreateAsync();
        await using var _1 = fixture;
        fixture.Parser.Delay = TimeSpan.FromMilliseconds(50);
        for (var i = 0; i < 8; i++)
        {
            fixture.WriteFile("docs", $"f{i}.txt", $"text {i}");
        }

        await fixture.StartAsync();
        await fixture.WaitIdleAsync();
        Assert.Equal(2, fixture.Parser.MaxConcurrent);

        var (busy, _2) = await CreateAsync();
        await using var _3 = busy;
        busy.Monitor.IdleTime = TimeSpan.FromSeconds(10);
        busy.Parser.Delay = TimeSpan.FromMilliseconds(50);
        for (var i = 0; i < 6; i++)
        {
            busy.WriteFile("docs", $"f{i}.txt", $"text {i}");
        }

        await busy.StartAsync();
        await busy.WaitIdleAsync();
        Assert.Equal(1, busy.Parser.MaxConcurrent);

        // Turning the option off gives the busy user full speed too.
        var (full, _4) = await CreateAsync();
        await using var _5 = full;
        full.Monitor.IdleTime = TimeSpan.FromSeconds(10);
        await full.Settings.SaveAsync(full.Settings.Current with { FullSpeedOnlyWhenIdle = false }, CancellationToken.None);
        full.Parser.Delay = TimeSpan.FromMilliseconds(50);
        for (var i = 0; i < 8; i++)
        {
            full.WriteFile("docs", $"f{i}.txt", $"text {i}");
        }

        await full.StartAsync();
        await full.WaitIdleAsync();
        Assert.Equal(2, full.Parser.MaxConcurrent);
    }

    [Fact]
    public async Task The_remaining_time_estimate_appears_after_five_files()
    {
        var options = IndexingFixture.FastOptions with { FullSpeedWorkers = 1 };
        var (fixture, _) = await CreateAsync(options);
        await using var _ = fixture;
        fixture.Parser.Delay = TimeSpan.FromMilliseconds(40);
        for (var i = 0; i < 25; i++)
        {
            fixture.WriteFile("docs", $"f{i:00}.txt", $"text {i}");
        }

        await fixture.StartAsync();
        TimeSpan? estimate = null;
        var estimatedWith = -1;
        var early = false;
        await IndexingFixture.WaitUntilAsync(
            () =>
            {
                var snapshot = fixture.Service.Current;
                if (snapshot.ProcessedFiles is > 0 and < 5 && snapshot.EstimatedRemaining is not null)
                {
                    early = true;
                }

                if (snapshot.EstimatedRemaining is { } value)
                {
                    estimate = value;
                    estimatedWith = snapshot.ProcessedFiles;
                }

                return snapshot.State == IndexingState.Idle && snapshot.TotalFiles == 0 && estimate is not null;
            },
            what: "estimate seen");

        Assert.False(early);
        Assert.True(estimatedWith >= 5);
        Assert.True(estimate > TimeSpan.Zero);
        Assert.Null(fixture.Service.Current.EstimatedRemaining);
    }

    [Fact]
    public async Task Snapshot_events_are_rate_limited()
    {
        var options = IndexingFixture.FastOptions with { SnapshotInterval = TimeSpan.FromMilliseconds(250) };
        var (fixture, _) = await CreateAsync(options);
        await using var _ = fixture;
        fixture.Parser.Delay = TimeSpan.FromMilliseconds(10);
        for (var i = 0; i < 60; i++)
        {
            fixture.WriteFile("docs", $"f{i:00}.txt", $"text {i}");
        }

        var stamps = new List<DateTime>();
        fixture.Service.SnapshotChanged += (_, _) =>
        {
            lock (stamps)
            {
                stamps.Add(DateTime.UtcNow);
            }
        };

        await fixture.StartAsync();
        await fixture.WaitIdleAsync();
        await Task.Delay(400);

        lock (stamps)
        {
            Assert.True(stamps.Count >= 2);
            for (var i = 1; i < stamps.Count; i++)
            {
                Assert.True(stamps[i] - stamps[i - 1] >= TimeSpan.FromMilliseconds(200), $"events {i - 1} and {i} were only {stamps[i] - stamps[i - 1]} apart");
            }
        }
    }

    [Fact]
    public async Task Edits_and_deletions_are_summarised_in_the_recent_activity()
    {
        var (fixture, folder) = await CreateAsync();
        await using var _ = fixture;
        for (var i = 0; i < 6; i++)
        {
            fixture.WriteFile("docs", $"f{i}.txt", $"text {i}", Old);
        }

        await fixture.StartAsync();
        await fixture.WaitIdleAsync();
        for (var i = 0; i < 3; i++)
        {
            fixture.WriteFile("docs", $"f{i}.txt", $"changed text {i}", Old.AddDays(1));
        }

        File.Delete(Path.Combine(fixture.FolderPath("docs"), "f5.txt"));
        fixture.Service.RequestRescan(folder.Id);
        await fixture.WaitIdleAsync();

        var activity = fixture.Service.Current.RecentActivity;
        Assert.Contains(activity, a => a.Kind == ActivityKind.Updated && a.FileCount == 3);
        Assert.Contains(activity, a => a.Kind == ActivityKind.Removed && a.FileCount == 1);
        Assert.Contains(activity, a => a.Kind == ActivityKind.Added && a.FileCount == 6);
        Assert.True(activity.Count <= 5);
    }

    [Fact]
    public async Task The_periodic_full_scan_notices_changes_without_any_request()
    {
        var options = IndexingFixture.FastOptions with { FullReconcileInterval = TimeSpan.FromMilliseconds(300) };
        var (fixture, folder) = await CreateAsync(options);
        await using var _ = fixture;
        fixture.WriteFile("docs", "a.txt", "first");
        await fixture.StartAsync();
        await fixture.WaitIdleAsync();

        fixture.WriteFile("docs", "b.txt", "second");
        await IndexingFixture.WaitUntilAsync(async () => (await fixture.DocumentsAsync(folder.Id)).Count == 2, what: "periodic scan");
    }

    [Fact]
    public async Task Watchers_pick_up_new_changed_and_deleted_files_without_a_rescan()
    {
        var options = IndexingFixture.FastOptions with { EnableWatchers = true };
        var (fixture, folder) = await CreateAsync(options);
        await using var _ = fixture;
        fixture.WriteFile("docs", "existing.txt", "already here");
        await fixture.StartAsync();
        await fixture.WaitIdleAsync();
        Assert.Single(await fixture.DocumentsAsync(folder.Id));

        fixture.WriteFile("docs", Path.Combine("sub", "created.txt"), "brand new file");
        await IndexingFixture.WaitUntilAsync(
            async () => (await fixture.DocumentsAsync(folder.Id)).Any(d => d.Path.EndsWith("created.txt", StringComparison.Ordinal)),
            timeoutSeconds: 20,
            what: "new file noticed");

        fixture.WriteFile("docs", "existing.txt", "now with different content");
        await IndexingFixture.WaitUntilAsync(
            async () => (await fixture.Store.KeywordSearchAsync("\"different content\"", [], 10, CancellationToken.None)).Count == 1,
            timeoutSeconds: 20,
            what: "edit noticed");

        File.Delete(Path.Combine(fixture.FolderPath("docs"), "sub", "created.txt"));
        await IndexingFixture.WaitUntilAsync(
            async () => !(await fixture.DocumentsAsync(folder.Id)).Any(d => d.Path.EndsWith("created.txt", StringComparison.Ordinal)),
            timeoutSeconds: 20,
            what: "deletion noticed");

        // Noise from excluded places is ignored.
        var calls = fixture.Parser.TotalCalls;
        fixture.WriteFile("docs", Path.Combine(".git", "x.txt"), "noise");
        fixture.WriteFile("docs", "~$temp.txt", "noise");
        await Task.Delay(800);
        Assert.Equal(calls, fixture.Parser.TotalCalls);
    }

    [Fact]
    public void The_default_activity_monitor_always_reports_the_user_as_idle()
    {
        Assert.Equal(TimeSpan.MaxValue, new AlwaysIdleActivityMonitor().IdleTime);
    }
}
