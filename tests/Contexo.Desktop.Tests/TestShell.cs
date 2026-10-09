using Avalonia;
using Contexo.App.About;
using Contexo.App.AiClients;
using Contexo.App.Folders;
using Contexo.App.Search;
using Contexo.App.Services;
using Contexo.App.Settings;
using Contexo.App.Shell;
using Contexo.Core.Abstractions;
using Contexo.Desktop.Platform.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contexo.Desktop.Tests;

internal sealed class FakeSettingsStore : ISettingsStore
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

internal sealed class FakeIndexingService : IIndexingService
{
    public IndexingSnapshot Current { get; set; } = IndexingSnapshot.Initial;

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
    public IReadOnlyList<IAiClientIntegration> Integrations => [];

    public McpServerLaunch CurrentLaunch => new("contexo-mcp", []);

    public Task<IReadOnlyList<AiClientStatus>> GetStatusesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AiClientStatus>>(
            [new AiClientStatus("claude-desktop", "Claude Desktop", ClientConnectionState.Connected, null, null, null)]);
}

/// <summary>Builds a real shell view model on top of fakes, wired like the product (Avalonia dispatcher, real dialog host).</summary>
internal sealed class TestShell
{
    public TestShell(AppSettings? settings = null)
    {
        Settings = new FakeSettingsStore();
        if (settings is not null)
        {
            Settings.SaveAsync(settings, CancellationToken.None).GetAwaiter().GetResult();
        }

        Indexing = new FakeIndexingService();
        var dispatcher = new AvaloniaUiDispatcher();
        Navigation = new NavigationService();
        Dialogs = new DialogHostViewModel(dispatcher);
        var statusBar = new StatusBarViewModel(Indexing, new FakeAiClientStatusService(), dispatcher, TimeProvider.System, NullLogger<StatusBarViewModel>.Instance);
        Shell = new ShellViewModel(
            new FoldersViewModel(),
            new FirstRunViewModel(),
            new SearchViewModel(),
            new AiClientsViewModel(),
            new SettingsViewModel(),
            new AboutViewModel(),
            statusBar,
            Navigation,
            Dialogs,
            Settings,
            dispatcher);
    }

    public FakeSettingsStore Settings { get; }

    public FakeIndexingService Indexing { get; }

    public NavigationService Navigation { get; }

    public DialogHostViewModel Dialogs { get; }

    public ShellViewModel Shell { get; }

    public MainWindow CreateWindow() => new() { DataContext = Shell };

    /// <summary>Applies the theme the way the product does.</summary>
    public static void ApplyTheme(ThemePreference preference) =>
        Application.Current!.RequestedThemeVariant = ThemeManager.ToVariant(preference);
}
