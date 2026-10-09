using Contexo.Core.Abstractions;

namespace Contexo.Core.Integrations;

/// <summary>
/// Claude Desktop. Config: %APPDATA%\Claude\claude_desktop_config.json (Windows),
/// ~/Library/Application Support/Claude/claude_desktop_config.json (macOS); root key "mcpServers".
/// </summary>
internal sealed class ClaudeDesktopIntegration : JsonMcpClientIntegration
{
    private const string ConfigFileName = "claude_desktop_config.json";

    public ClaudeDesktopIntegration(IAppPaths paths, ClientPathOptions? pathOptions = null)
        : base(paths, pathOptions)
    {
    }

    public override string ClientId => "claude-desktop";

    public override string DisplayName => "Claude Desktop";

    // "claude-ai" is the value Claude Desktop is known to send; the others are fallbacks. Pending confirmation in T22.
    public override IReadOnlyCollection<string> KnownClientNames { get; } = ["claude-ai", "claude desktop", "claude-desktop"];

    protected override string RootKey => "mcpServers";

    protected override ClientLocations? GetLocations() => Options.Platform switch
    {
        ClientPlatform.Windows => new(
            Path.Combine(Options.AppData, "Claude", ConfigFileName),
            [Path.Combine(Options.LocalAppData, "AnthropicClaude"), Path.Combine(Options.LocalAppData, "Claude")]),
        ClientPlatform.MacOS => new(
            Path.Combine(Options.UserProfile, "Library", "Application Support", "Claude", ConfigFileName),
            [Path.Combine(Options.ApplicationsDirectory, "Claude.app"), Path.Combine(Options.UserProfile, "Applications", "Claude.app")]),
        _ => null,
    };
}
