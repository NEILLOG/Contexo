using Contexo.Core.Abstractions;

namespace Contexo.Core.Integrations;

/// <summary>Stub. Implemented by T14.</summary>
internal sealed class CursorIntegration : IAiClientIntegration
{
    public string ClientId => throw new NotImplementedException("T14");

    public string DisplayName => throw new NotImplementedException("T14");

    public IReadOnlyCollection<string> KnownClientNames => throw new NotImplementedException("T14");

    public ClientConfigState GetConfigState() => throw new NotImplementedException("T14");

    public void AddOrRepair(McpServerLaunch launch) => throw new NotImplementedException("T14");

    public void Remove() => throw new NotImplementedException("T14");

    public string BuildManualSnippet(McpServerLaunch launch) => throw new NotImplementedException("T14");
}
