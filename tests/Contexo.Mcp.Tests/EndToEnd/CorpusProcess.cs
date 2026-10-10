using System.Diagnostics;
using Contexo.Core.Common;
using Contexo.Mcp.Tests.Support;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Contexo.Mcp.Tests.EndToEnd;

/// <summary>Runs the corpus generator (tools/Contexo.CorpusGen), finds the optional embedding model and starts Contexo.Mcp on a given database.</summary>
internal static class CorpusProcess
{
    public static string? FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Contexo.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    /// <summary>The folder that holds model folders, or null when none was downloaded (run tools/download-models.sh).</summary>
    public static string? FindModelsDirectory()
    {
        var candidates = new List<string>();
        var fromEnvironment = Environment.GetEnvironmentVariable(AppPaths.ModelsDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            candidates.Add(fromEnvironment);
        }

        if (FindRepositoryRoot() is { } root)
        {
            candidates.Add(Path.Combine(root, "models"));
        }

        return candidates.FirstOrDefault(d => Directory.Exists(d) && Directory.EnumerateDirectories(d).Any(m => File.Exists(Path.Combine(m, "contexo-model.json"))));
    }

    /// <summary>Writes corpus/, queries.json and expected.json into <paramref name="outputDirectory"/>.</summary>
    public static void Generate(string outputDirectory)
    {
        var root = FindRepositoryRoot() ?? throw new InvalidOperationException("Contexo.slnx was not found above " + AppContext.BaseDirectory);
        var built = new[] { "Debug", "Release" }
            .Select(configuration => Path.Combine(root, "tools", "Contexo.CorpusGen", "bin", configuration, "net10.0", "Contexo.CorpusGen.dll"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        var start = new ProcessStartInfo(McpServerProcess.DotnetPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        if (built is not null)
        {
            start.ArgumentList.Add(built);
        }
        else
        {
            start.ArgumentList.Add("run");
            start.ArgumentList.Add("--project");
            start.ArgumentList.Add(Path.Combine(root, "tools", "Contexo.CorpusGen", "Contexo.CorpusGen.csproj"));
            start.ArgumentList.Add("--");
        }

        start.ArgumentList.Add("generate");
        start.ArgumentList.Add(outputDirectory);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the corpus generator");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The corpus generator did not finish in 5 minutes");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"The corpus generator failed ({process.ExitCode}): {errors.Result}{output.Result}");
        }
    }

    public static async Task<McpClient> ConnectAsync(string dataDirectory, string databasePath, string modelsDirectory)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "contexo-under-test",
            Command = McpServerProcess.DotnetPath,
            Arguments = [McpServerProcess.AssemblyPath, "--db", databasePath, "--models", modelsDirectory],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                [AppPaths.DataDirectoryVariable] = dataDirectory,
                [AppPaths.ModelsDirectoryVariable] = modelsDirectory,
            },
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await McpClient.CreateAsync(
            transport,
            new McpClientOptions { ClientInfo = new Implementation { Name = McpServerProcess.ClientName, Version = McpServerProcess.ClientVersion } },
            cancellationToken: timeout.Token);
    }
}
