using Contexo.Core.Abstractions;
using Contexo.Core.Integrations;
using Contexo.Core.Tests.Common;

namespace Contexo.Core.Tests.Integrations;

internal sealed class FakeAppPaths(string mcpExecutablePath, string databasePath) : IAppPaths
{
    public string DataDirectory => Path.GetDirectoryName(databasePath)!;
    public string DatabasePath => databasePath;
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string ModelsDirectory => Path.Combine(DataDirectory, "models");
    public string McpExecutablePath => mcpExecutablePath;
}

/// <summary>
/// A throw-away user profile. Every root directory the integrations look at lives under a temporary folder,
/// so these tests can never touch the real AI client settings of the machine running them.
/// </summary>
internal sealed class IntegrationHarness : IDisposable
{
    public static readonly string[] ClientIds = ["claude-desktop", "vscode", "cursor", "lm-studio"];

    private readonly TempDirectory _dir = new();

    public IntegrationHarness(ClientPlatform platform)
    {
        Platform = platform;
        Options = new ClientPathOptions
        {
            Platform = platform,
            AppData = _dir.Combine("AppData", "Roaming"),
            LocalAppData = _dir.Combine("AppData", "Local"),
            UserProfile = _dir.Combine("Home"),
            ApplicationsDirectory = _dir.Combine("Applications"),
        };
        Directory.CreateDirectory(_dir.Combine("bin"));
        McpExecutable = _dir.Combine("bin", "Contexo.Mcp.exe");
        File.WriteAllText(McpExecutable, "stub");
        Paths = new FakeAppPaths(McpExecutable, _dir.Combine("data", "contexo.db"));
    }

    public ClientPlatform Platform { get; }

    public ClientPathOptions Options { get; }

    public FakeAppPaths Paths { get; }

    public string McpExecutable { get; }

    public string Root => _dir.Path;

    public McpServerLaunch Launch => new(McpExecutable, ["--db", Paths.DatabasePath]);

    public void Dispose() => _dir.Dispose();

    public JsonMcpClientIntegration Create(string clientId) => clientId switch
    {
        "claude-desktop" => new ClaudeDesktopIntegration(Paths, Options),
        "vscode" => new VsCodeIntegration(Paths, Options),
        "cursor" => new CursorIntegration(Paths, Options),
        "lm-studio" => new LmStudioIntegration(Paths, Options),
        _ => throw new ArgumentOutOfRangeException(nameof(clientId)),
    };

    public IReadOnlyList<IAiClientIntegration> CreateAll() => ClientIds.Select(Create).ToList();

    /// <summary>Pretends the client is installed by creating the folder that holds its config file.</summary>
    public string Install(JsonMcpClientIntegration integration)
    {
        var path = integration.ConfigPath!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    public static IEnumerable<object[]> AllClients()
    {
        foreach (var platform in new[] { ClientPlatform.Windows, ClientPlatform.MacOS })
        {
            foreach (var id in ClientIds)
            {
                yield return [id, platform];
            }
        }
    }
}
