using Contexo.Core.Abstractions;

namespace Contexo.Core.Integrations;

/// <summary>Cursor. Global config: ~/.cursor/mcp.json on both Windows and macOS; root key "mcpServers".</summary>
internal sealed class CursorIntegration : JsonMcpClientIntegration
{
    public CursorIntegration(IAppPaths paths, ClientPathOptions? pathOptions = null)
        : base(paths, pathOptions)
    {
    }

    public override string ClientId => "cursor";

    public override string DisplayName => "Cursor";

    // Pending confirmation in T22: Cursor is believed to send "cursor-vscode".
    public override IReadOnlyCollection<string> KnownClientNames { get; } = ["cursor-vscode", "cursor"];

    protected override string RootKey => "mcpServers";

    protected override ClientLocations? GetLocations() => Options.Platform switch
    {
        ClientPlatform.Windows => new(
            Path.Combine(Options.UserProfile, ".cursor", "mcp.json"),
            [Path.Combine(Options.LocalAppData, "Programs", "cursor")]),
        ClientPlatform.MacOS => new(
            Path.Combine(Options.UserProfile, ".cursor", "mcp.json"),
            [Path.Combine(Options.ApplicationsDirectory, "Cursor.app"), Path.Combine(Options.UserProfile, "Applications", "Cursor.app")]),
        _ => null,
    };
}
