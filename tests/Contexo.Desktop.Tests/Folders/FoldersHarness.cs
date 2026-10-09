using Avalonia.Threading;
using Contexo.App.Folders;
using Contexo.App.Services;
using Contexo.App.Shell;
using Contexo.Core;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Contexo.Desktop.Platform.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Contexo.Desktop.Tests.Folders;

/// <summary>Known locations for the wizard: real (temporary) folders so that adding them really works.</summary>
internal sealed class HarnessKnownFolders(string root) : IKnownFolders
{
    public IReadOnlyList<KnownFolder> GetKnownFolders() =>
    [
        new KnownFolder("documents", "文件", Path.Combine(root, "Documents"), true),
        new KnownFolder("desktop", "桌面", Path.Combine(root, "Desktop"), false),
        new KnownFolder("onedrive", "OneDrive - 公司共用", Path.Combine(root, "OneDrive"), true),
        new KnownFolder("downloads", "下載", Path.Combine(root, "Downloads"), false),
    ];
}

internal sealed class HarnessCounter : IFolderFileCounter
{
    public Task<FileCount> CountAsync(string path, IReadOnlySet<string> extensions, int limit, CancellationToken cancellationToken) =>
        Task.FromResult(path.EndsWith("Documents", StringComparison.Ordinal) ? new FileCount(2100, false)
            : path.EndsWith("OneDrive", StringComparison.Ordinal) ? new FileCount(1300, false)
            : path.EndsWith("Downloads", StringComparison.Ordinal) ? new FileCount(10000, true)
            : new FileCount(40, false));
}

internal sealed class HarnessClients : IAiClientStatusService
{
    public IReadOnlyList<IAiClientIntegration> Integrations => [];

    public McpServerLaunch CurrentLaunch => new("contexo-mcp", []);

    public Task<IReadOnlyList<AiClientStatus>> GetStatusesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AiClientStatus>>(
        [
            new AiClientStatus("claude-desktop", "Claude Desktop", ClientConnectionState.NotAdded, null, null, null),
            new AiClientStatus("cursor", "Cursor", ClientConnectionState.Connected, null, null, null),
        ]);
}

/// <summary>
/// The product's own container (<c>AddContexoDesktop</c> + <c>AddContexoCore</c>) on a temporary data directory,
/// with the indexer, AI program status and wizard locations replaced by fakes. Resolves the real shell and main window.
/// </summary>
internal sealed class FoldersHarness : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _dataDirectory;

    private FoldersHarness(ServiceProvider services, string dataDirectory, FakeIndexingService indexing)
    {
        _services = services;
        _dataDirectory = dataDirectory;
        Indexing = indexing;
    }

    public static async Task<FoldersHarness> CreateAsync(AppSettings? settings = null)
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "contexo-folders-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        foreach (var name in new[] { "Documents", "Desktop", "OneDrive", "Downloads" })
        {
            Directory.CreateDirectory(Path.Combine(dataDirectory, name));
        }

        var indexing = new FakeIndexingService();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAppPaths>(new AppPaths(new AppPathsOverrides { DataDirectory = Path.Combine(dataDirectory, "data") }));
        services.AddContexoDesktop();
        services.AddContexoCore();
        services.AddSingleton<IIndexingService>(indexing);
        services.AddSingleton<IAiClientStatusService>(new HarnessClients());
        services.AddSingleton<IKnownFolders>(new HarnessKnownFolders(dataDirectory));
        services.AddSingleton<IFolderFileCounter>(new HarnessCounter());

        var provider = services.BuildServiceProvider();
        var harness = new FoldersHarness(provider, dataDirectory, indexing);
        await harness.Store.InitializeAsync(CancellationToken.None);
        await harness.Settings.SaveAsync(settings ?? new AppSettings { FirstRunCompleted = true }, CancellationToken.None);
        return harness;
    }

    public FakeIndexingService Indexing { get; }

    public IKnowledgeStore Store => _services.GetRequiredService<IKnowledgeStore>();

    public ISettingsStore Settings => _services.GetRequiredService<ISettingsStore>();

    public NavigationService Navigation => _services.GetRequiredService<NavigationService>();

    public DialogHostViewModel Dialogs => _services.GetRequiredService<DialogHostViewModel>();

    public FoldersViewModel Folders => _services.GetRequiredService<FoldersViewModel>();

    public FirstRunViewModel FirstRun => _services.GetRequiredService<FirstRunViewModel>();

    public ShellViewModel Shell => _services.GetRequiredService<ShellViewModel>();

    public string Root => Path.Combine(_dataDirectory, "folders");

    public MainWindow CreateWindow() => new() { DataContext = Shell };

    /// <summary>Creates a real folder below <see cref="Root"/> (with sub folders) and adds it to the database.</summary>
    public async Task<WatchedFolder> AddFolderAsync(string name, params string[] subfolders)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        foreach (var sub in subfolders)
        {
            Directory.CreateDirectory(Path.Combine(path, sub.Replace('/', Path.DirectorySeparatorChar)));
        }

        return await Store.AddFolderAsync(path, CancellationToken.None);
    }

    /// <summary>15 folders like the sketch: 1 with a problem, 2 running, 12 finished; 7 unreadable files; work in progress.</summary>
    public async Task FillLikeTheSketchAsync()
    {
        var progress = new List<FolderProgress>();
        var problem = await AddFolderAsync("OneDrive - 公司共用");
        await Store.SetFolderStateAsync(problem.Id, FolderState.Unavailable, null, CancellationToken.None);
        progress.Add(new FolderProgress(problem.Id, FolderState.Unavailable, 1314, 1314, 0, 0));
        var working = await AddFolderAsync("工程");
        progress.Add(new FolderProgress(working.Id, FolderState.Active, 980, 612, 0, 368));
        var queued = await AddFolderAsync("桌面");
        progress.Add(new FolderProgress(queued.Id, FolderState.Active, 41, 0, 0, 41));

        var finished = new List<WatchedFolder>();
        foreach (var name in new[] { "文件", "業務", "採購", "會議紀錄", "人事公告", "品質管理", "教育訓練", "範本", "合約", "產品型錄", "客服 FAQ", "規格書" })
        {
            var folder = await AddFolderAsync(name);
            finished.Add(folder);
            progress.Add(new FolderProgress(folder.Id, FolderState.Active, 100 + name.Length, 100 + name.Length, 0, 0));
        }

        DocumentErrorCode[] codes =
        [
            DocumentErrorCode.PasswordProtected, DocumentErrorCode.Locked, DocumentErrorCode.Corrupted, DocumentErrorCode.PasswordProtected,
            DocumentErrorCode.TooLarge, DocumentErrorCode.Unknown, DocumentErrorCode.Unsupported,
        ];
        string[] files = ["薪資級距表.xlsx", "驗收報告_v3.docx", "舊系統匯出.pdf", "客戶名單_機密.xlsx", "工地照片集.pdf", "報價.docx", "附件.pptx"];
        for (var i = 0; i < files.Length; i++)
        {
            var folder = finished[i % 3];
            await Store.MarkDocumentAsync(
                folder.Id,
                Path.Combine(folder.Path, files[i]),
                new FileFingerprint(1, DateTimeOffset.UtcNow, "00"),
                DocumentStatus.Failed,
                codes[i],
                null,
                null,
                keepExistingChunks: false,
                CancellationToken.None);
        }

        Indexing.Raise(new IndexingSnapshot(
            IndexingState.Indexing, 3420, 1284, Path.Combine(Root, "2025 年度採購簡報.pptx"), TimeSpan.FromMinutes(42), progress, []));
    }

    public static async Task SettleAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    public void Dispose()
    {
        _services.Dispose();
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A still-open database file on Windows; the temp folder is cleaned up by the system later.
        }
    }
}
