using Contexo.Core.Abstractions;

namespace Contexo.Core.Integrations;

/// <summary>LM Studio. Config: ~/.lmstudio/mcp.json on both Windows and macOS (Cursor-style notation); root key "mcpServers".</summary>
internal sealed class LmStudioIntegration : JsonMcpClientIntegration
{
    public LmStudioIntegration(IAppPaths paths, ClientPathOptions? pathOptions = null)
        : base(paths, pathOptions)
    {
    }

    public override string ClientId => "lm-studio";

    public override string DisplayName => "LM Studio";

    // Pending confirmation in T22: the exact clientInfo.name LM Studio sends is undocumented.
    public override IReadOnlyCollection<string> KnownClientNames { get; } = ["lm-studio", "lm studio", "lmstudio"];

    protected override string RootKey => "mcpServers";

    protected override ClientLocations? GetLocations() => Options.Platform switch
    {
        ClientPlatform.Windows => new(
            Path.Combine(Options.UserProfile, ".lmstudio", "mcp.json"),
            [Path.Combine(Options.LocalAppData, "Programs", "LM Studio"), Path.Combine(Options.LocalAppData, "Programs", "LM-Studio")]),
        ClientPlatform.MacOS => new(
            Path.Combine(Options.UserProfile, ".lmstudio", "mcp.json"),
            [Path.Combine(Options.ApplicationsDirectory, "LM Studio.app"), Path.Combine(Options.UserProfile, "Applications", "LM Studio.app")]),
        _ => null,
    };
}
