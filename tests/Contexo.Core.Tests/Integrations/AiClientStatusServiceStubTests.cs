using Contexo.Core.Common;
using Contexo.Core.Integrations;
using Contexo.Core.Tests.Common;

namespace Contexo.Core.Tests.Integrations;

/// <summary>The placeholder must be safe to run so the desktop shell can start before T14 is done.</summary>
public sealed class AiClientStatusServiceStubTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Placeholder_lists_no_clients_and_builds_the_launch_from_the_paths()
    {
        var paths = new AppPaths(new AppPathsOverrides { DataDirectory = _dir.Path, DatabasePath = _dir.Combine("custom.db") });
        var service = new AiClientStatusService(paths);

        var statuses = await service.GetStatusesAsync(CancellationToken.None);

        Assert.Empty(service.Integrations);
        Assert.Empty(statuses);
        Assert.Equal(paths.McpExecutablePath, service.CurrentLaunch.ExecutablePath);
        Assert.Equal(new[] { "--db", paths.DatabasePath }, service.CurrentLaunch.Arguments);
    }
}
