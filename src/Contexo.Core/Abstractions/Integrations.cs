namespace Contexo.Core.Abstractions;

/// <summary>How an AI client should launch Contexo.Mcp.</summary>
public sealed record McpServerLaunch(string ExecutablePath, IReadOnlyList<string> Arguments);

public enum ClientConfigState
{
    /// <summary>The AI client does not appear to be installed.</summary>
    NotInstalled,
    /// <summary>Installed, and its config has no "contexo" entry.</summary>
    NotConfigured,
    /// <summary>The "contexo" entry exists and points at an existing executable.</summary>
    Configured,
    /// <summary>The entry exists but the executable is missing, or the config file cannot be parsed.</summary>
    Broken,
}

/// <summary>Reads and edits one AI client's MCP configuration file. Only ever touches the "contexo" entry.</summary>
public interface IAiClientIntegration
{
    /// <summary>Stable id, e.g. "claude-desktop".</summary>
    string ClientId { get; }

    string DisplayName { get; }

    /// <summary>Lower-case clientInfo.name values this client sends during MCP initialize.</summary>
    IReadOnlyCollection<string> KnownClientNames { get; }

    ClientConfigState GetConfigState();

    /// <summary>Adds or repairs the entry, preserving everything else in the file. Writes a .bak copy first.</summary>
    void AddOrRepair(McpServerLaunch launch);

    /// <summary>Removes only the "contexo" entry.</summary>
    void Remove();

    /// <summary>JSON snippet for manual setup by IT.</summary>
    string BuildManualSnippet(McpServerLaunch launch);
}

public enum ClientConnectionState
{
    NotInstalled,
    NotAdded,
    WaitingForConnection,
    Connected,
    NeedsRepair,
}

public sealed record AiClientStatus(
    string ClientId,
    string DisplayName,
    ClientConnectionState State,
    DateTimeOffset? LastConnectedAt,
    DateTimeOffset? LastQueryAt,
    string? Problem);

public interface IAiClientStatusService
{
    IReadOnlyList<IAiClientIntegration> Integrations { get; }

    McpServerLaunch CurrentLaunch { get; }

    Task<IReadOnlyList<AiClientStatus>> GetStatusesAsync(CancellationToken cancellationToken);
}
