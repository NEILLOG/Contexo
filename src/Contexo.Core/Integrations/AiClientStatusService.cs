using Contexo.Core.Abstractions;

namespace Contexo.Core.Integrations;

/// <summary>
/// Placeholder implemented by T14. Safe to run: it lists no AI clients, so the desktop shell can start before the real service exists.
/// </summary>
internal sealed class AiClientStatusService : IAiClientStatusService
{
    private readonly IAppPaths _paths;

    public AiClientStatusService(IAppPaths paths) => _paths = paths;

    public IReadOnlyList<IAiClientIntegration> Integrations { get; } = [];

    public McpServerLaunch CurrentLaunch => new(_paths.McpExecutablePath, ["--db", _paths.DatabasePath]);

    public Task<IReadOnlyList<AiClientStatus>> GetStatusesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AiClientStatus>>([]);
}
