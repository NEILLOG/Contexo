using System.Collections.Concurrent;
using System.Text;
using Contexo.Core.Abstractions;
using Contexo.Core.Chunking;
using Contexo.Core.Indexing;
using Contexo.Core.Parsing;
using Contexo.Core.Storage;
using Contexo.Core.Tests.Common;
using Contexo.Core.Tests.Storage;

namespace Contexo.Core.Tests.Indexing;

/// <summary>
/// Fake parser for the extensions the scanner knows about. The behaviour is chosen by the start of the file text:
/// HANG, NOTIMPL, CORRUPT, BOOM, EMBED (one embedded workbook), NEST:n (an embedded file that nests deeper), BIG (a large attachment),
/// TABLE (a prose section plus a table summary). Anything else becomes one prose section.
/// </summary>
internal sealed class FakeParser : IDocumentParser
{
    private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);
    private int _total;
    private int _running;
    private int _maxConcurrent;

    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".txt", ".md", ".docx", ".xlsx", ".csv", ".pdf", ".pptx"];

    public TimeSpan Delay { get; set; }

    /// <summary>Called with the file name at the start of every parse.</summary>
    public Action<string>? OnParse { get; set; }

    /// <summary>Makes every parse fail with Corrupted, to test retry.</summary>
    public bool ForceCorrupt { get; set; }

    public int TotalCalls => Volatile.Read(ref _total);

    /// <summary>The highest number of parses that were running at the same moment.</summary>
    public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

    public int CallsFor(string fileName) => _calls.TryGetValue(fileName, out var count) ? count : 0;

    public async Task<ParsedDocument> ParseAsync(ParseContext context, CancellationToken cancellationToken)
    {
        var running = Interlocked.Increment(ref _running);
        int seen;
        while (running > (seen = Volatile.Read(ref _maxConcurrent)) && Interlocked.CompareExchange(ref _maxConcurrent, running, seen) != seen)
        {
        }

        try
        {
            return await ParseCoreAsync(context, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    private async Task<ParsedDocument> ParseCoreAsync(ParseContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _total);
        OnParse?.Invoke(context.FileName);
        _calls.AddOrUpdate(context.FileName, 1, (_, count) => count + 1);

        using var reader = new StreamReader(context.Content, Encoding.UTF8, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (ForceCorrupt)
        {
            throw new DocumentParseException(DocumentErrorCode.Corrupted, "forced");
        }

        if (text.StartsWith("HANG", StringComparison.Ordinal))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        if (text.StartsWith("NOTIMPL", StringComparison.Ordinal))
        {
            throw new NotImplementedException();
        }

        if (text.StartsWith("CORRUPT", StringComparison.Ordinal))
        {
            throw new DocumentParseException(DocumentErrorCode.Corrupted, "broken file");
        }

        if (text.StartsWith("BOOM", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("boom");
        }

        var location = new SourceLocation { HeadingPath = [context.FileName], EmbeddedPath = context.EmbeddedPath };
        var sections = new List<DocumentSection> { new(SectionKind.Prose, $"{text} (from {context.FileName})", location) };
        var embedded = new List<EmbeddedFile>();
        var tables = new List<SpreadsheetTable>();

        if (text.StartsWith("EMBED", StringComparison.Ordinal))
        {
            embedded.Add(new EmbeddedFile("內嵌.xlsx", Encoding.UTF8.GetBytes("TABLE inside"), new SourceLocation { Page = 1 }));
        }
        else if (text.StartsWith("BIG", StringComparison.Ordinal))
        {
            embedded.Add(new EmbeddedFile("big.xlsx", Encoding.UTF8.GetBytes("TABLE " + new string('x', 5000)), SourceLocation.None));
        }
        else if (text.StartsWith("NEST:", StringComparison.Ordinal))
        {
            var level = int.Parse(text[5..], System.Globalization.CultureInfo.InvariantCulture);
            embedded.Add(new EmbeddedFile("e.docx", Encoding.UTF8.GetBytes($"NEST:{level + 1}"), SourceLocation.None));
        }
        else if (text.StartsWith("TABLE", StringComparison.Ordinal))
        {
            sections.Add(new DocumentSection(SectionKind.TableSummary, "table of prices", location, KeepWhole: true, TableKey: "Sheet1!A1:B2"));
            tables.Add(new SpreadsheetTable("Sheet1!A1:B2", "Sheet1", "A1:B2", 1, ["品名", "價格"], 1, [["螺絲", "1"]], "table of prices"));
        }

        return new ParsedDocument(sections, embedded, [], tables, []);
    }
}

internal sealed class FakeEmbedding : IEmbeddingService
{
    private int _calls;

    public bool Available { get; set; } = true;

    public string ModelId => Available ? "fake-model" : string.Empty;

    public int Dimensions => 4;

    public bool IsAvailable => Available;

    public int Calls => Volatile.Read(ref _calls);

    public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        if (!Available)
        {
            throw new InvalidOperationException("not available");
        }

        IReadOnlyList<float[]> result = texts.Select(Vector).ToList();
        return Task.FromResult(result);
    }

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken) => Task.FromResult(Vector(text));

    private static float[] Vector(string text)
    {
        var hash = text.Aggregate(17, (h, c) => (h * 31) + c);
        var random = new Random(hash);
        var v = new float[4];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = (float)random.NextDouble() + 0.1f;
        }

        var norm = (float)Math.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / norm).ToArray();
    }
}

internal sealed class FakeSettings : ISettingsStore
{
    public AppSettings Current { get; private set; } = new() { FirstRunCompleted = true };

    public event EventHandler<AppSettings>? Changed;

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Current = settings;
        Changed?.Invoke(this, settings);
        return Task.CompletedTask;
    }
}

internal sealed class FakeActivityMonitor : IUserActivityMonitor
{
    public TimeSpan IdleTime { get; set; } = TimeSpan.MaxValue;
}

/// <summary>The real clock plus an offset the test can advance; timers still run in real time.</summary>
internal sealed class OffsetTimeProvider : TimeProvider
{
    private long _offsetTicks;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
}

/// <summary>A real store in a temporary directory, fake parser / embedding, and the real chunker and reconciliation logic.</summary>
internal sealed class IndexingFixture : IAsyncDisposable
{
    private readonly StoreFixture _storeFixture;

    private IndexingFixture(StoreFixture storeFixture, IndexingOptions options)
    {
        _storeFixture = storeFixture;
        Options = options;
        Service = CreateService();
        Service.SnapshotChanged += (_, snapshot) =>
        {
            lock (Snapshots)
            {
                Snapshots.Add(snapshot);
            }
        };
        Service.MassDeletionPendingRaised += (_, pending) =>
        {
            lock (MassDeletions)
            {
                MassDeletions.Add(pending);
            }
        };
    }

    /// <summary>A further service instance on the same store (for restart scenarios).</summary>
    public IndexingService CreateService() => new(
        _storeFixture.Store,
        Embedding,
        new ParserRegistry([Parser]),
        new StructuredChunker(),
        Settings,
        Monitor,
        new ParserOptions(),
        new ChunkingOptions(),
        Logger,
        Time,
        Options);

    public static IndexingOptions FastOptions { get; } = new()
    {
        WatcherDebounce = TimeSpan.FromMilliseconds(200),
        FullReconcileInterval = TimeSpan.FromHours(1),
        UnavailableCheckInterval = TimeSpan.FromHours(1),
        ThrottledFileDelay = TimeSpan.FromMilliseconds(10),
        SnapshotInterval = TimeSpan.FromMilliseconds(20),
        FolderStatsInterval = TimeSpan.FromMilliseconds(50),
        ParseTimeout = TimeSpan.FromSeconds(30),
        EnableWatchers = false,
    };

    public IndexingOptions Options { get; }

    public IndexingService Service { get; }

    public SqliteKnowledgeStore Store => _storeFixture.Store;

    public StoreFixture StoreFx => _storeFixture;

    public FakeParser Parser { get; } = new();

    public FakeEmbedding Embedding { get; } = new();

    public FakeSettings Settings { get; } = new();

    public FakeActivityMonitor Monitor { get; } = new();

    public OffsetTimeProvider Time { get; } = new();

    public TestLogger<IndexingService> Logger { get; } = new();

    public List<IndexingSnapshot> Snapshots { get; } = [];

    public List<MassDeletionPending> MassDeletions { get; } = [];

    public static async Task<IndexingFixture> CreateAsync(IndexingOptions? options = null) =>
        new(await StoreFixture.CreateAsync(), options ?? FastOptions);

    public string FolderPath(string name) => _storeFixture.PathOf("watched", name);

    public async Task<WatchedFolder> AddFolderAsync(string name)
    {
        var path = FolderPath(name);
        Directory.CreateDirectory(path);
        return await Store.AddFolderAsync(path, CancellationToken.None);
    }

    /// <summary>Writes (or overwrites) a file below the given watched folder directory and returns its full path.</summary>
    public string WriteFile(string folderName, string relativePath, string content, DateTime? lastWriteUtc = null)
    {
        var path = Path.Combine(FolderPath(folderName), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        if (lastWriteUtc is { } stamp)
        {
            File.SetLastWriteTimeUtc(path, stamp);
        }

        return path;
    }

    public Task StartAsync() => Service.StartAsync(CancellationToken.None);

    public async Task<IReadOnlyList<DocumentRecord>> DocumentsAsync(long folderId) =>
        await Store.GetDocumentsAsync(folderId, CancellationToken.None);

    public async Task<WatchedFolder> FolderAsync(long id) =>
        (await Store.GetFoldersAsync(CancellationToken.None)).Single(f => f.Id == id);

    /// <summary>Waits until the service reports Idle steadily (a few consecutive polls), so a brief flicker does not count.</summary>
    public async Task WaitIdleAsync(int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            stable = Service.Current.State == IndexingState.Idle ? stable + 1 : 0;
            if (stable >= 5)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Indexing did not become idle; state {Service.Current.State}");
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutSeconds = 30, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Condition not met: {what}");
    }

    public static Task WaitUntilAsync(Func<bool> condition, int timeoutSeconds = 30, string? what = null) =>
        WaitUntilAsync(() => Task.FromResult(condition()), timeoutSeconds, what);

    public async ValueTask DisposeAsync()
    {
        await Service.StopAsync(CancellationToken.None);
        Service.Dispose();
        _storeFixture.Dispose();
    }
}
