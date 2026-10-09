using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Contexo.App.Services;
using Contexo.App.ViewModels;
using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Contexo.App.AiClients;

/// <summary>
/// "AI 軟體" page: one card per installed AI client with its connection status and an add / repair / remove action.
/// While the page is visible (<see cref="IPageLifecycle"/>) the statuses are re-read every <see cref="RefreshInterval"/>.
/// The config file edits of <see cref="IAiClientIntegration"/> are synchronous and may sleep a little (retries),
/// so they always run on a background thread.
/// </summary>
public sealed partial class AiClientsViewModel : ViewModelBase, IPageLifecycle, IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

    private readonly IAiClientStatusService _statusService;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<AiClientsViewModel> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _loopGate = new();
    private CancellationTokenSource? _loop;
    private Task _loopTask = Task.CompletedTask;
    private bool _disposed;

    public AiClientsViewModel(
        IAiClientStatusService statusService,
        IDialogService dialogs,
        IClipboardService clipboard,
        IUiDispatcher dispatcher,
        TimeProvider time,
        ILogger<AiClientsViewModel> logger)
    {
        _statusService = statusService;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
    }

    /// <summary>Used by shell tests that never show this page; every service does nothing.</summary>
    internal AiClientsViewModel()
        : this(
            new IdleStatusService(),
            new IdleDialogService(),
            new IdleClipboard(),
            new IdleDispatcher(),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AiClientsViewModel>.Instance)
    {
    }

    /// <summary>Navigation title.</summary>
    public string Title => "AI 軟體";

    public string Heading => "讓 AI 軟體使用我的資料";

    public string Subtitle => "加入後，在這些 AI 軟體裡提問時，AI 就能查詢你的檔案。";

    public string HelpTitle => "怎麼確認可以用了？";

    public string HelpText => "在 AI 軟體裡問「用 Contexo 找報價單」，這裡的「最後查詢」時間就會更新。";

    public string EmptyText => "這台電腦上沒有找到支援的 AI 軟體。";

    /// <summary>Installed AI clients.</summary>
    public ObservableCollection<AiClientCardViewModel> Cards { get; } = [];

    /// <summary>"其他支援的 AI 軟體：Cursor、LM Studio" (clients that are not installed). Empty when there are none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOtherClients))]
    public partial string OtherClientsText { get; private set; } = "";

    /// <summary>True after the first status read finished.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoaded { get; private set; }

    public bool HasOtherClients => OtherClientsText.Length > 0;

    /// <summary>No supported AI client is installed on this computer.</summary>
    public bool ShowEmpty => IsLoaded && Cards.Count == 0;

    public void OnNavigatedTo()
    {
        lock (_loopGate)
        {
            if (_disposed)
            {
                return;
            }

            _loop?.Cancel();
            _loop?.Dispose();
            _loop = new CancellationTokenSource();
            var token = _loop.Token;
            // The timer is created before the task starts so no tick is missed.
            var timer = new PeriodicTimer(RefreshInterval, _time);
            _loopTask = Task.Run(() => RunLoopAsync(timer, token), CancellationToken.None);
        }
    }

    public void OnNavigatedFrom()
    {
        lock (_loopGate)
        {
            _loop?.Cancel();
            _loop?.Dispose();
            _loop = null;
        }
    }

    /// <summary>Completes when the refresh loop has stopped. For tests.</summary>
    internal Task LoopStopped => _loopTask;

    public void Dispose()
    {
        OnNavigatedFrom();
        lock (_loopGate)
        {
            _disposed = true;
        }
    }

    private async Task RunLoopAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        using (timer)
        {
            try
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    await RefreshAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The page was left.
            }
        }
    }

    /// <summary>Reads the statuses once and updates the cards. Never throws (a failed read keeps what is shown).</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AiClientStatus> statuses;
        try
        {
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            statuses = await _statusService.GetStatusesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read AI client status");
            return;
        }
        finally
        {
            _refreshGate.Release();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        _dispatcher.Post(() => Apply(statuses, now, zone));
    }

    private void Apply(IReadOnlyList<AiClientStatus> statuses, DateTimeOffset now, TimeZoneInfo zone)
    {
        var existing = Cards.ToDictionary(c => c.ClientId);
        var next = new List<AiClientCardViewModel>();
        foreach (var status in statuses.Where(s => s.State != ClientConnectionState.NotInstalled))
        {
            if (!existing.TryGetValue(status.ClientId, out var card))
            {
                card = new AiClientCardViewModel(this, status);
            }

            card.Apply(status, now, zone);
            next.Add(card);
        }

        if (!next.SequenceEqual(Cards))
        {
            Cards.Clear();
            foreach (var card in next)
            {
                Cards.Add(card);
            }
        }

        var others = statuses.Where(s => s.State == ClientConnectionState.NotInstalled).Select(s => s.DisplayName).ToList();
        OtherClientsText = others.Count == 0 ? "" : "其他支援的 AI 軟體：" + string.Join("、", others);
        IsLoaded = true;
        OnPropertyChanged(nameof(ShowEmpty));
    }

    internal async Task RunPrimaryAsync(AiClientCardViewModel card)
    {
        card.IsBusy = true;
        try
        {
            var changed = card.State is ClientConnectionState.NotAdded or ClientConnectionState.NeedsRepair
                ? await AddOrRepairAsync(card).ConfigureAwait(true)
                : await RemoveAsync(card).ConfigureAwait(true);
            if (changed)
            {
                await RefreshAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            card.IsBusy = false;
        }
    }

    private async Task<bool> AddOrRepairAsync(AiClientCardViewModel card)
    {
        card.ClearMessages();
        var repairing = card.State == ClientConnectionState.NeedsRepair;
        var integration = FindIntegration(card);
        if (integration is null)
        {
            card.SetError("找不到這個 AI 軟體的設定方式。");
            return false;
        }

        try
        {
            var launch = _statusService.CurrentLaunch;
            await Task.Run(() => integration.AddOrRepair(launch)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            card.SetError(DescribeFailure(ex, repairing ? "修復" : "加入", card.ClientId));
            return false;
        }

        card.SetNotice($"{(repairing ? "已修復" : "已加入")}。請把 {card.DisplayName} 完全關閉後重新開啟。");
        return true;
    }

    private async Task<bool> RemoveAsync(AiClientCardViewModel card)
    {
        card.ClearMessages();
        var confirmed = await _dialogs.ConfirmAsync(new ConfirmRequest(
            $"從 {card.DisplayName} 移除",
            [$"移除後，{card.DisplayName} 將無法查詢你的資料。你的資料仍保留在 Contexo。"],
            "移除")).ConfigureAwait(true);
        if (!confirmed)
        {
            return false;
        }

        var integration = FindIntegration(card);
        if (integration is null)
        {
            card.SetError("找不到這個 AI 軟體的設定方式。");
            return false;
        }

        try
        {
            await Task.Run(integration.Remove).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            card.SetError(DescribeFailure(ex, "移除", card.ClientId));
            return false;
        }

        card.SetNotice("已移除。");
        return true;
    }

    internal async Task CopySnippetAsync(AiClientCardViewModel card)
    {
        var integration = FindIntegration(card);
        if (integration is null)
        {
            card.SetError("找不到這個 AI 軟體的設定方式。");
            return;
        }

        string snippet;
        try
        {
            snippet = integration.BuildManualSnippet(_statusService.CurrentLaunch);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not build the manual snippet for {ClientId}", card.ClientId);
            card.SetError("無法產生設定內容。");
            return;
        }

        bool copied;
        try
        {
            copied = await _clipboard.TrySetTextAsync(snippet).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard failed");
            copied = false;
        }

        if (copied)
        {
            card.SetNotice("已複製");
        }
        else
        {
            card.SetError("無法複製，請稍後再試一次。");
        }
    }

    private IAiClientIntegration? FindIntegration(AiClientCardViewModel card) =>
        _statusService.Integrations.FirstOrDefault(i => string.Equals(i.ClientId, card.ClientId, StringComparison.Ordinal));

    /// <summary>
    /// <see cref="InvalidOperationException"/> from the integrations already carries a plain Traditional Chinese message
    /// (for example a config file that is not valid JSON); everything else gets a generic sentence.
    /// </summary>
    private string DescribeFailure(Exception ex, string verb, string clientId)
    {
        _logger.LogWarning(ex, "Could not {Verb} AI client {ClientId}", verb, clientId);
        return ex is InvalidOperationException && !string.IsNullOrWhiteSpace(ex.Message)
            ? ex.Message
            : $"無法{verb}，請稍後再試一次。";
    }

    private sealed class IdleStatusService : IAiClientStatusService
    {
        public IReadOnlyList<IAiClientIntegration> Integrations => [];

        public McpServerLaunch CurrentLaunch => new("", []);

        public Task<IReadOnlyList<AiClientStatus>> GetStatusesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiClientStatus>>([]);
    }

    private sealed class IdleDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(ConfirmRequest request) => Task.FromResult(false);

        public Task ShowAsync(object dialogViewModel) => Task.CompletedTask;
    }

    private sealed class IdleClipboard : IClipboardService
    {
        public Task<bool> TrySetTextAsync(string text) => Task.FromResult(false);
    }

    private sealed class IdleDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }
}
