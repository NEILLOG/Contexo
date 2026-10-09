using Contexo.Core.Abstractions;

namespace Contexo.Core.Integrations;

/// <summary>
/// Visual Studio Code. User-level config: %APPDATA%\Code\User\mcp.json (Windows),
/// ~/Library/Application Support/Code/User/mcp.json (macOS); root key "servers", entries carry "type": "stdio".
/// The file may contain comments; they are dropped when Contexo rewrites it (a .contexo.bak copy is kept).
/// </summary>
internal sealed class VsCodeIntegration : JsonMcpClientIntegration
{
    public VsCodeIntegration(IAppPaths paths, ClientPathOptions? pathOptions = null)
        : base(paths, pathOptions)
    {
    }

    public override string ClientId => "vscode";

    public override string DisplayName => "Visual Studio Code";

    // Pending confirmation in T22: VS Code is believed to send "Visual Studio Code" (older builds "vscode").
    public override IReadOnlyCollection<string> KnownClientNames { get; } = ["visual studio code", "vscode", "vscode-mcp-client"];

    protected override string RootKey => "servers";

    protected override bool IncludeType => true;

    protected override ClientLocations? GetLocations() => Options.Platform switch
    {
        ClientPlatform.Windows => new(
            Path.Combine(Options.AppData, "Code", "User", "mcp.json"),
            [Path.Combine(Options.LocalAppData, "Programs", "Microsoft VS Code")]),
        ClientPlatform.MacOS => new(
            Path.Combine(Options.UserProfile, "Library", "Application Support", "Code", "User", "mcp.json"),
            [Path.Combine(Options.ApplicationsDirectory, "Visual Studio Code.app"), Path.Combine(Options.UserProfile, "Applications", "Visual Studio Code.app")]),
        _ => null,
    };
}
