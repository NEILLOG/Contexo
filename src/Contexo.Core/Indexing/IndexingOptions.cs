namespace Contexo.Core.Indexing;

/// <summary>Tunable timings and limits of the indexing pipeline. The defaults are the product values; tests shorten them.</summary>
internal sealed record IndexingOptions
{
    /// <summary>Per file, covering the parser and every embedded file inside it.</summary>
    public TimeSpan ParseTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan WatcherDebounce { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan FullReconcileInterval { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan UnavailableCheckInterval { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>When the user was active more recently than this and FullSpeedOnlyWhenIdle is on, one file at a time is processed.</summary>
    public TimeSpan IdleThreshold { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ThrottledFileDelay { get; init; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan SnapshotInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan FolderStatsInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan LockedRetryDelay { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ActivityBucket { get; init; } = TimeSpan.FromMinutes(1);

    public int MaxEmbeddedDepth { get; init; } = 3;
    public long MaxEmbeddedBytes { get; init; } = 50L * 1024 * 1024;

    public int MassDeletionMinCount { get; init; } = 20;
    public double MassDeletionRatio { get; init; } = 0.3;
    public TimeSpan MassDeletionSuppression { get; init; } = TimeSpan.FromHours(24);

    public int EmbeddingBatchSize { get; init; } = 64;
    public int FullSpeedWorkers { get; init; } = 2;
    public int MaxRecentActivities { get; init; } = 5;
    public int MaxPartialPaths { get; init; } = 200;

    /// <summary>Tests that do not care about file system events can switch the watchers off.</summary>
    public bool EnableWatchers { get; init; } = true;
}
