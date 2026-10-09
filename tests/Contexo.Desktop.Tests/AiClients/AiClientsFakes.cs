using Contexo.App.Services;
using Contexo.Core.Abstractions;

namespace Contexo.Desktop.Tests.AiClients;

internal sealed class StubIntegration(string clientId, string displayName) : IAiClientIntegration
{
    public string ClientId { get; } = clientId;

    public string DisplayName { get; } = displayName;

    public IReadOnlyCollection<string> KnownClientNames => [];

    public int AddCalls { get; private set; }

    public Exception? AddFails { get; set; }

    public ClientConfigState GetConfigState() => ClientConfigState.NotConfigured;

    public void AddOrRepair(McpServerLaunch launch)
    {
        AddCalls++;
        if (AddFails is not null)
        {
            throw AddFails;
        }
    }

    public void Remove()
    {
    }

    public string BuildManualSnippet(McpServerLaunch launch) => "{ \"mcpServers\": { \"contexo\": { \"command\": \"" + launch.ExecutablePath + "\" } } }";
}

internal sealed class StubStatusService : IAiClientStatusService
{
    public List<StubIntegration> Stubs { get; } = [];

    public List<AiClientStatus> Statuses { get; } = [];

    public IReadOnlyList<IAiClientIntegration> Integrations => Stubs;

    public McpServerLaunch CurrentLaunch { get; } = new("C:\\Contexo\\Contexo.Mcp.exe", ["--db", "C:\\data\\contexo.db"]);

    public Task<IReadOnlyList<AiClientStatus>> GetStatusesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AiClientStatus>>(Statuses.ToList());

    public StubIntegration Add(string id, string name, ClientConnectionState state, DateTimeOffset? connected = null, DateTimeOffset? query = null, string? problem = null)
    {
        var stub = new StubIntegration(id, name);
        Stubs.Add(stub);
        Statuses.Add(new AiClientStatus(id, name, state, connected, query, problem));
        return stub;
    }
}

internal sealed class StubDialogs : IDialogService
{
    public bool Answer { get; set; } = true;

    public Task<bool> ConfirmAsync(ConfirmRequest request) => Task.FromResult(Answer);

    public Task ShowAsync(object dialogViewModel) => Task.CompletedTask;
}

internal sealed class StubClipboard : IClipboardService
{
    public List<string> Texts { get; } = [];

    public Task<bool> TrySetTextAsync(string text)
    {
        Texts.Add(text);
        return Task.FromResult(true);
    }
}

internal sealed class FixedAppPaths(string dataDirectory, string mcpExecutablePath) : IAppPaths
{
    public string DataDirectory => dataDirectory;

    public string DatabasePath => Path.Combine(dataDirectory, "contexo.db");

    public string LogsDirectory => Path.Combine(dataDirectory, "logs");

    public string SettingsPath => Path.Combine(dataDirectory, "settings.json");

    public string ModelsDirectory => Path.Combine(dataDirectory, "models");

    public string McpExecutablePath => mcpExecutablePath;
}
