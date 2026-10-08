using System.Diagnostics;

namespace Contexo.Mcp.Tests;

/// <summary>stdout belongs to the MCP protocol: the server must never print anything else to it.</summary>
public sealed class McpStartupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("contexo-mcp-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public async Task Startup_writes_nothing_to_stdout_and_logs_to_the_logs_folder()
    {
        var assembly = typeof(McpArguments).Assembly.Location;
        var dotnet = Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet" ? Environment.ProcessPath! : "dotnet";
        var startInfo = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(assembly);
        startInfo.ArgumentList.Add("--db");
        startInfo.ArgumentList.Add(Path.Combine(_dir, "test.db"));
        startInfo.Environment["CONTEXO_DATA_DIR"] = Path.Combine(_dir, "data");

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, await stdout);
        Assert.Contains("started", await stderr);
        var logFile = Assert.Single(Directory.GetFiles(Path.Combine(_dir, "data", "logs"), "mcp-*.log"));
        Assert.Contains("Contexo.Mcp started", File.ReadAllText(logFile));
    }
}
