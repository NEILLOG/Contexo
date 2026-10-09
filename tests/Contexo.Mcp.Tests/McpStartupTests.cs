using System.Diagnostics;
using System.Text.Json;
using Contexo.Core.Common;
using Contexo.Mcp.Tests.Support;

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

    private Process Start(string databasePath, string dataDirectory)
    {
        var startInfo = new ProcessStartInfo(McpServerProcess.DotnetPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(McpServerProcess.AssemblyPath);
        startInfo.ArgumentList.Add("--db");
        startInfo.ArgumentList.Add(databasePath);
        startInfo.Environment[AppPaths.DataDirectoryVariable] = dataDirectory;
        startInfo.Environment[AppPaths.ModelsDirectoryVariable] = Path.Combine(_dir, "no-models");
        return Process.Start(startInfo)!;
    }

    [Fact]
    public async Task Closing_stdin_without_any_request_ends_the_server_cleanly_and_writes_nothing_to_stdout()
    {
        var data = Path.Combine(_dir, "data");
        using var process = Start(Path.Combine(_dir, "test.db"), data);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, await stdout);
        Assert.Contains("started", await stderr);
        var logFile = Assert.Single(Directory.GetFiles(Path.Combine(data, "logs"), "mcp-*.log"));
        Assert.Contains("Contexo.Mcp started", File.ReadAllText(logFile));
    }

    [Fact]
    public async Task Every_line_on_stdout_is_a_json_rpc_message()
    {
        using var database = await TestDatabase.CreateAsync();
        using var process = Start(database.DatabasePath, database.DataDirectory);
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        async Task SendAsync(object message)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
            await process.StandardInput.FlushAsync(timeout.Token);
        }

        async Task<JsonElement> ReadResponseAsync(int id)
        {
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                Lines.Add(line);
                var json = JsonDocument.Parse(line).RootElement.Clone();
                if (json.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number && value.GetInt32() == id)
                {
                    return json;
                }
            }

            throw new InvalidOperationException("The server closed stdout before answering.");
        }

        await SendAsync(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "RawClient", version = "1.0" } },
        });
        var initialize = await ReadResponseAsync(1);
        await SendAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
        await SendAsync(new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        var tools = await ReadResponseAsync(2);
        await SendAsync(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "search", arguments = new { query = TestDatabase.QuoteQuery } } });
        var call = await ReadResponseAsync(3);

        // The classic handshake (initialize, then the initialized notification) records who connected.
        var connected = database.ReadActivityRows();
        for (var attempt = 0; attempt < 50 && connected.Count < 2; attempt++)
        {
            await Task.Delay(100);
            connected = database.ReadActivityRows();
        }

        process.StandardInput.Close();
        while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } rest)
        {
            Lines.Add(rest);
        }

        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal("contexo", initialize.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal(3, tools.GetProperty("result").GetProperty("tools").GetArrayLength());
        Assert.Contains("報價總計", call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.NotEmpty(Lines);
        foreach (var line in Lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
        }

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("started", await stderr);
        Assert.Single(connected, row => row.Kind == (int)Contexo.Core.Abstractions.McpEventKind.Connected && row.Client == "RawClient");
    }

    private List<string> Lines { get; } = [];
}
