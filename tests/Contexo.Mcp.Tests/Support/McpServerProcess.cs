using Contexo.Core.Common;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Contexo.Mcp.Tests.Support;

/// <summary>Starts the built Contexo.Mcp as a child process and talks to it with the official SDK client over stdio.</summary>
internal static class McpServerProcess
{
    public const string ClientName = "ContexoTestClient";
    public const string ClientVersion = "9.9.9";

    public static string AssemblyPath => typeof(McpArguments).Assembly.Location;

    public static string DotnetPath =>
        Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet" ? Environment.ProcessPath! : "dotnet";

    public static Dictionary<string, string?> Environment_(TestDatabase database) => new()
    {
        [AppPaths.DataDirectoryVariable] = database.DataDirectory,
        [AppPaths.ModelsDirectoryVariable] = database.ModelsDirectory,
    };

    public static async Task<McpClient> ConnectAsync(TestDatabase database, string? databasePath = null)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "contexo-under-test",
            Command = DotnetPath,
            Arguments = [AssemblyPath, "--db", databasePath ?? database.DatabasePath, "--models", database.ModelsDirectory],
            EnvironmentVariables = Environment_(database),
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await McpClient.CreateAsync(
            transport,
            new McpClientOptions { ClientInfo = new Implementation { Name = ClientName, Version = ClientVersion } },
            cancellationToken: timeout.Token);
    }

    public static async Task<CallToolResult> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await client.CallToolAsync(tool, arguments, cancellationToken: timeout.Token);
    }

    public static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
