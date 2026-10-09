using Contexo.Core.Abstractions;

namespace Contexo.Core.Integrations;

/// <summary>Combines each AI client's config state with the MCP activity recorded by Contexo.Mcp.</summary>
internal sealed class AiClientStatusService : IAiClientStatusService
{
    private static readonly string[] DisplayOrder = ["claude-desktop", "vscode", "cursor", "lm-studio"];

    private readonly IAppPaths _paths;
    private readonly IKnowledgeStore _store;

    public AiClientStatusService(IAppPaths paths, IEnumerable<IAiClientIntegration> integrations, IKnowledgeStore store)
    {
        _paths = paths;
        _store = store;
        Integrations = integrations
            .Select((integration, index) => (integration, index))
            .OrderBy(x => OrderOf(x.integration.ClientId))
            .ThenBy(x => x.index)
            .Select(x => x.integration)
            .ToList();
    }

    public IReadOnlyList<IAiClientIntegration> Integrations { get; }

    public McpServerLaunch CurrentLaunch => new(_paths.McpExecutablePath, ["--db", _paths.DatabasePath]);

    public async Task<IReadOnlyList<AiClientStatus>> GetStatusesAsync(CancellationToken cancellationToken)
    {
        var summaries = await _store.GetMcpActivitySummariesAsync(cancellationToken).ConfigureAwait(false);
        var activityByClient = GroupActivity(summaries);

        var statuses = new List<AiClientStatus>(Integrations.Count);
        foreach (var integration in Integrations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            activityByClient.TryGetValue(integration.ClientId, out var activity);
            statuses.Add(BuildStatus(integration, activity));
        }

        return statuses;
    }

    private Dictionary<string, Activity> GroupActivity(IReadOnlyList<McpClientActivitySummary> summaries)
    {
        var result = new Dictionary<string, Activity>(StringComparer.Ordinal);
        foreach (var summary in summaries)
        {
            var owner = FindOwner(summary.ClientName);
            if (owner is null)
            {
                continue;
            }

            result[owner] = result.TryGetValue(owner, out var existing)
                ? existing.Merge(summary)
                : Activity.From(summary);
        }

        return result;
    }

    private string? FindOwner(string clientName)
    {
        var name = clientName.Trim().ToLowerInvariant();
        if (name.Length == 0)
        {
            return null;
        }

        foreach (var integration in Integrations)
        {
            if (integration.KnownClientNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return integration.ClientId;
            }
        }

        // Fuzzy fallback. Cursor and LM Studio first because "cursor-vscode" also contains "code".
        // Claude Code (the CLI) is a different product from Claude Desktop, so it is not attributed to it.
        var preferred = name switch
        {
            _ when name.Contains("cursor") => "cursor",
            _ when name.Contains("lm studio") || name.Contains("lmstudio") || name.Contains("lm-studio") => "lm-studio",
            _ when name.Contains("claude code") || name.Contains("claude-code") => null,
            _ when name.Contains("claude") => "claude-desktop",
            _ when name.Contains("code") => "vscode",
            _ => null,
        };

        return preferred is not null && Integrations.Any(i => i.ClientId == preferred) ? preferred : null;
    }

    private static AiClientStatus BuildStatus(IAiClientIntegration integration, Activity? activity)
    {
        AiClientStatus Make(ClientConnectionState state, string? problem = null) =>
            new(integration.ClientId, integration.DisplayName, state, activity?.LastConnectedAt, activity?.LastToolCallAt, problem);

        ClientConfigState config;
        try
        {
            config = integration.GetConfigState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Make(ClientConnectionState.NeedsRepair, "無法讀取設定檔。");
        }

        switch (config)
        {
            case ClientConfigState.NotInstalled:
                return new(integration.ClientId, integration.DisplayName, ClientConnectionState.NotInstalled, null, null, null);
            case ClientConfigState.NotConfigured:
                return new(integration.ClientId, integration.DisplayName, ClientConnectionState.NotAdded, null, null, null);
            case ClientConfigState.Broken:
                var reason = (integration as IConfigProblemSource)?.DescribeProblem() ?? "設定有問題，需要修復。";
                return Make(ClientConnectionState.NeedsRepair, reason);
        }

        if (activity is null || (activity.LastConnectedAt is null && activity.LastErrorAt is null))
        {
            return Make(ClientConnectionState.WaitingForConnection);
        }

        if (activity.LastErrorAt is { } errorAt && (activity.LastConnectedAt is not { } connectedAt || errorAt > connectedAt))
        {
            return Make(ClientConnectionState.NeedsRepair, "上次連線時發生錯誤");
        }

        return Make(ClientConnectionState.Connected);
    }

    private static int OrderOf(string clientId)
    {
        var index = Array.IndexOf(DisplayOrder, clientId);
        return index < 0 ? DisplayOrder.Length : index;
    }

    private sealed record Activity(DateTimeOffset? LastConnectedAt, DateTimeOffset? LastToolCallAt, DateTimeOffset? LastErrorAt)
    {
        public static Activity From(McpClientActivitySummary s) => new(s.LastConnectedAt, s.LastToolCallAt, s.LastErrorAt);

        public Activity Merge(McpClientActivitySummary s) =>
            new(Latest(LastConnectedAt, s.LastConnectedAt), Latest(LastToolCallAt, s.LastToolCallAt), Latest(LastErrorAt, s.LastErrorAt));

        private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) =>
            a is null ? b : b is null ? a : a > b ? a : b;
    }
}
