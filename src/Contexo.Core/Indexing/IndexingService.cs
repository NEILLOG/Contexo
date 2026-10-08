using Contexo.Core.Abstractions;

namespace Contexo.Core.Indexing;

/// <summary>
/// Placeholder implemented by T10. Safe to run: it reports an idle, empty state and never raises events,
/// so the desktop shell can start before the real pipeline exists.
/// </summary>
internal sealed class IndexingService : IIndexingService
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
