using Contexo.Core.Abstractions;
using Contexo.Core.Indexing;

namespace Contexo.Core.Tests.Indexing;

/// <summary>The placeholder must be safe to run so the desktop shell can start before T10 is done.</summary>
public sealed class IndexingServiceStubTests
{
    [Fact]
    public async Task Placeholder_reports_idle_and_accepts_every_call()
    {
        IIndexingService service = new IndexingService();
        var raised = false;
        service.SnapshotChanged += (_, _) => raised = true;
        service.MassDeletionPendingRaised += (_, _) => raised = true;

        await service.StartAsync(CancellationToken.None);
        service.Pause();
        service.Resume();
        service.RequestRescan(null);
        service.RequestRescan(1);
        service.RequestRetry(null);
        service.RequestRetry(1);
        await service.ResolveMassDeletionAsync(1, deleteMissing: false, CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Same(IndexingSnapshot.Initial, service.Current);
        Assert.Equal(IndexingState.Idle, service.Current.State);
        Assert.False(raised);
    }

    [Fact]
    public void Idle_monitor_reports_maximum_idle_time()
    {
        Assert.Equal(TimeSpan.MaxValue, new AlwaysIdleActivityMonitor().IdleTime);
    }
}
