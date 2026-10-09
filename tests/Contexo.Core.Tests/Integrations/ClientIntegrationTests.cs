using System.Text.Json;
using System.Text.Json.Nodes;
using Contexo.Core.Abstractions;
using Contexo.Core.Integrations;

namespace Contexo.Core.Tests.Integrations;

public sealed class ClientIntegrationTests
{
    public static IEnumerable<object[]> Clients() => IntegrationHarness.AllClients();

    private static string RootKeyOf(string clientId) => clientId == "vscode" ? "servers" : "mcpServers";

    private static JsonObject ReadJson(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    // ---- GetConfigState ----

    [Theory]
    [MemberData(nameof(Clients))]
    public void Not_installed_when_no_folder_or_app_exists(string clientId, ClientPlatform platform)
    {
        using var h = new IntegrationHarness(platform);
        Assert.Equal(ClientConfigState.NotInstalled, h.Create(clientId).GetConfigState());
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public void Not_configured_when_only_the_config_folder_exists(string clientId, ClientPlatform platform)
    {
        using var h = new IntegrationHarness(platform);
        var client = h.Create(clientId);
        h.Install(client);
        Assert.Equal(ClientConfigState.NotConfigured, client.GetConfigState());
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public void Not_configured_when_the_file_has_other_servers_but_no_contexo(string clientId, ClientPlatform platform)
    {
        using var h = new IntegrationHarness(platform);
        var client = h.Create(clientId);
        File.WriteAllText(h.Install(client), $$"""{ "{{RootKeyOf(clientId)}}": { "other": { "command": "x" } } }""");
        Assert.Equal(ClientConfigState.NotConfigured, client.GetConfigState());
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public void Configured_after_AddOrRepair_with_the_current_executable(string clientId, ClientPlatform platform)
    {
        using var h = new IntegrationHarness(platform);
        var client = h.Create(clientId);
        h.Install(client);
        client.AddOrRepair(h.Launch);
        Assert.Equal(ClientConfigState.Configured, client.GetConfigState());
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public void Broken_when_the_executable_no_longer_exists(string clientId, ClientPlatform platform)
    {
        using var h = new IntegrationHarness(platform);
        var client = h.Create(clientId);
        h.Install(client);
        client.AddOrRepair(h.Launch);
        File.Delete(h.McpExecutable);
        Assert.Equal(ClientConfigState.Broken, client.GetConfigState());
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public void Broken_when_the_executable_differs_from_the_current_one(string clientId, ClientPlatform platform)
    {
        using var h = new IntegrationHarness(platform);
        var client = h.Create(clientId);
        h.Install(client);
        var other = Path.Combine(h.Root, "old-install", "Contexo.Mcp.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "stub");
        client.AddOrRepair(new McpServerLaunch(other, ["--db", h.Paths.DatabasePath]));

        Assert.Equal(ClientConfigState.Broken, client.GetConfigState());
        Assert.False(string.IsNullOrWhiteSpace(client.DescribeProblem()));

        client.AddOrRepair(h.Launch);
        Assert.Equal(ClientConfigState.Configured, client.GetConfigState());
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public void Broken_when_the_json_is_corrupt(string clientId, ClientPlatform platform)
    {
        using var h = new IntegrationHarness(platform);
        var client = h.Create(clientId);
        File.WriteAllText(h.Install(client), "{ this is not json");
        Assert.Equal(ClientConfigState.Broken, client.GetConfigState());
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public void Broken_when_the_contexo_entry_has_no_command(string clientId, ClientPlatform platform)
    {
        using var h = new IntegrationHarness(platform);
        var client = h.Create(clientId);
        File.WriteAllText(h.Install(client), $$"""{ "{{RootKeyOf(clientId)}}": { "contexo": { "args": [] } } }""");
        Assert.Equal(ClientConfigState.Broken, client.GetConfigState());
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public void Installed_is_detected_from_the_application_location_alone(string clientId, ClientPlatform platform)
    {
        using var h = new IntegrationHarness(platform);
        var client = h.Create(clientId);
        string marker = (clientId, platform) switch
        {
            ("claude-desktop", ClientPlatform.Windows) => Path.Combine(h.Options.LocalAppData, "AnthropicClaude"),
            ("vscode", ClientPlatform.Windows) => Path.Combine(h.Options.LocalAppData, "Programs", "Microsoft VS Code"),
            ("cursor", ClientPlatform.Windows) => Path.Combine(h.Options.LocalAppData, "Programs", "cursor"),
            ("lm-studio", ClientPlatform.Windows) => Path.Combine(h.Options.LocalAppData, "Programs", "LM Studio"),
            ("claude-desktop", _) => Path.Combine(h.Options.ApplicationsDirectory, "Claude.app"),
            ("vscode", _) => Path.Combine(h.Options.ApplicationsDirectory, "Visual Studio Code.app"),
            ("cursor", _) => Path.Combine(h.Options.ApplicationsDirectory, "Cursor.app"),
            _ => Path.Combine(h.Options.ApplicationsDirectory, "LM Studio.app"),
        };
        Directory.CreateDirectory(marker);

        Assert.Equal(ClientConfigState.NotConfigured, client.GetConfigState());
    }

    [Fact]
    public void Config_locations_follow_each_platforms_table()
    {
        using var win = new IntegrationHarness(ClientPlatform.Windows);
        using var mac = new IntegrationHarness(ClientPlatform.MacOS);
        string Rel(IntegrationHarness h, string id) => Path.GetRelativePath(h.Root, h.Create(id).ConfigPath!).Replace('\\', '/');

        Assert.Equal("AppData/Roaming/Claude/claude_desktop_config.json", Rel(win, "claude-desktop"));
        Assert.Equal("AppData/Roaming/Code/User/mcp.json", Rel(win, "vscode"));
        Assert.Equal("Home/.cursor/mcp.json", Rel(win, "cursor"));
        Assert.Equal("Home/.lmstudio/mcp.json", Rel(win, "lm-studio"));

        Assert.Equal("Home/Library/Application Support/Claude/claude_desktop_config.json", Rel(mac, "claude-desktop"));
        Assert.Equal("Home/Library/Application Support/Code/User/mcp.json", Rel(mac, "vscode"));
        Assert.Equal("Home/.cursor/mcp.json", Rel(mac, "cursor"));
        Assert.Equal("Home/.lmstudio/mcp.json", Rel(mac, "lm-studio"));
    }

    [Fact]
    public void Unsupported_platform_reports_not_installed_and_refuses_to_write()
    {
        using var h = new IntegrationHarness(ClientPlatform.Other);
        var client = h.Create("cursor");
        Assert.Equal(ClientConfigState.NotInstalled, client.GetConfigState());
        Assert.Throws<InvalidOperationException>(() => client.AddOrRepair(h.Launch));
        client.Remove();
    }

    [Fact]
    public void Identity_is_stable()
    {
        using var h = new IntegrationHarness(ClientPlatform.Windows);
        Assert.Equal(IntegrationHarness.ClientIds, h.CreateAll().Select(c => c.ClientId));
        Assert.Equal(["Claude Desktop", "Visual Studio Code", "Cursor", "LM Studio"], h.CreateAll().Select(c => c.DisplayName));
        Assert.All(h.CreateAll(), c =>
        {
            Assert.NotEmpty(c.KnownClientNames);
            Assert.All(c.KnownClientNames, n => Assert.Equal(n.ToLowerInvariant(), n));
        });
        Assert.Contains("claude-ai", h.Create("claude-desktop").KnownClientNames);
    }

    // ---- AddOrRepair ----

    [Theory]
    [MemberData(nameof(Clients))]
    public void AddOrRepair_keeps_everything_else_and_writes_a_backup(string clientId, ClientPlatform platform)
    {
        using var h = new IntegrationHarness(platform);
        var client = h.Create(clientId);
        var path = h.Install(client);
        var rootKey = RootKeyOf(clientId);
        var original = $$"""
            {
              "theme": "dark",
              "{{rootKey}}": {
                "filesystem": { "command": "npx", "args": ["-y", "server-filesystem", "C:\\Users\\王小明\\文件"] }
              },
              "inputs": []
            }
            """;
        File.WriteAllText(path, original);

        client.AddOrRepair(h.Launch);

        var root = ReadJson(path);
        Assert.Equal("dark", root["theme"]!.GetValue<string>());
        Assert.NotNull(root["inputs"]);
        var servers = root[rootKey]!.AsObject();
        Assert.Equal("npx", servers["filesystem"]!["command"]!.GetValue<string>());
        Assert.Equal("C:\\Users\\王小明\\文件", servers["filesystem"]!["args"]![2]!.GetValue<string>());
        var entry = servers["contexo"]!.AsObject();
        Assert.Equal(h.McpExecutable, entry["command"]!.GetValue<string>());
        Assert.Equal(["--db", h.Paths.DatabasePath], entry["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(clientId == "vscode" ? "stdio" : null, entry["type"]?.GetValue<string>());
        Assert.Equal(clientId == "vscode" ? 3 : 2, entry.Count);

        Assert.Equal(original, File.ReadAllText(path + ".contexo.bak"));
        Assert.False(File.Exists(path + ".contexo.tmp"));
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public void AddOrRepair_is_idempotent(string clientId, ClientPlatform platform)
    {
        using var h = new IntegrationHarness(platform);
        var client = h.Create(clientId);
        var path = h.Install(client);
        File.WriteAllText(path, $$"""{ "{{RootKeyOf(clientId)}}": { "a": { "command": "a" } } }""");

        client.AddOrRepair(h.Launch);
        var first = File.ReadAllText(path);
        client.AddOrRepair(h.Launch);
        var second = File.ReadAllText(path);

        Assert.Equal(first, second);
        Assert.Equal(first, File.ReadAllText(path + ".contexo.bak"));
        Assert.Single(ReadJson(path)[RootKeyOf(clientId)]!.AsObject(), kv => kv.Key == "contexo");
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public void AddOrRepair_creates_missing_folder_and_file_without_a_backup(string clientId, ClientPlatform platform)
    {
        using var h = new IntegrationHarness(platform);
        var client = h.Create(clientId);
        var path = client.ConfigPath!;
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));

        client.AddOrRepair(h.Launch);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".contexo.bak"));
        Assert.Equal(ClientConfigState.Configured, client.GetConfigState());
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public void AddOrRepair_accepts_an_empty_file(string clientId, ClientPlatform platform)
    {
        using var h = new IntegrationHarness(platform);
        var client = h.Create(clientId);
        File.WriteAllText(h.Install(client), "  \n");
        client.AddOrRepair(h.Launch);
        Assert.Equal(ClientConfigState.Configured, client.GetConfigState());
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public void AddOrRepair_replaces_a_stale_entry(string clientId, ClientPlatform platform)
    {
        using var h = new IntegrationHarness(platform);
        var client = h.Create(clientId);
        File.WriteAllText(h.Install(client), $$"""{ "{{RootKeyOf(clientId)}}": { "contexo": { "command": "C:\\gone\\Contexo.Mcp.exe", "args": ["old"], "env": { "A": "1" } } } }""");
        Assert.Equal(ClientConfigState.Broken, client.GetConfigState());

        client.AddOrRepair(h.Launch);

        Assert.Equal(ClientConfigState.Configured, client.GetConfigState());
        var entry = ReadJson(client.ConfigPath!)[RootKeyOf(clientId)]!["contexo"]!.AsObject();
        Assert.False(entry.ContainsKey("env"));
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public void AddOrRepair_refuses_to_touch_a_corrupt_file(string clientId, ClientPlatform platform)
    {
        using var h = new IntegrationHarness(platform);
        var client = h.Create(clientId);
        var path = h.Install(client);
        const string corrupt = """{ "mcpServers": { "a": """;
        File.WriteAllText(path, corrupt);

        var ex = Assert.Throws<InvalidOperationException>(() => client.AddOrRepair(h.Launch));

        Assert.Equal("設定檔格式有誤，為避免破壞你原本的設定，Contexo 沒有修改它。", ex.Message);
        Assert.Equal(corrupt, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".contexo.bak"));
        Assert.False(File.Exists(path + ".contexo.tmp"));
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    public void AddOrRepair_refuses_a_root_that_is_not_an_object(string content)
    {
        using var h = new IntegrationHarness(ClientPlatform.Windows);
        var client = h.Create("cursor");
        var path = h.Install(client);
        File.WriteAllText(path, content);

        Assert.Throws<InvalidOperationException>(() => client.AddOrRepair(h.Launch));
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void AddOrRepair_refuses_when_the_servers_node_is_not_an_object()
    {
        using var h = new IntegrationHarness(ClientPlatform.Windows);
        var client = h.Create("cursor");
        var path = h.Install(client);
        File.WriteAllText(path, """{ "mcpServers": [] }""");

        Assert.Throws<InvalidOperationException>(() => client.AddOrRepair(h.Launch));
        Assert.Equal("""{ "mcpServers": [] }""", File.ReadAllText(path));
    }

    [Fact]
    public void AddOrRepair_reads_comments_and_trailing_commas_from_vscode_settings()
    {
        using var h = new IntegrationHarness(ClientPlatform.Windows);
        var client = h.Create("vscode");
        var path = h.Install(client);
        var original = """
            {
              // my servers
              "servers": {
                /* keep me */
                "github": { "type": "http", "url": "https://example.test/mcp", },
              },
              "inputs": [],
            }
            """;
        File.WriteAllText(path, original);

        Assert.Equal(ClientConfigState.NotConfigured, client.GetConfigState());
        client.AddOrRepair(h.Launch);

        var servers = ReadJson(path)["servers"]!.AsObject();
        Assert.Equal("https://example.test/mcp", servers["github"]!["url"]!.GetValue<string>());
        Assert.Equal("stdio", servers["contexo"]!["type"]!.GetValue<string>());
        Assert.Equal(original, File.ReadAllText(path + ".contexo.bak"));
        Assert.Equal(ClientConfigState.Configured, client.GetConfigState());
    }

    [Fact]
    public void AddOrRepair_writes_two_space_indentation_and_readable_chinese()
    {
        using var h = new IntegrationHarness(ClientPlatform.Windows);
        var client = h.Create("claude-desktop");
        var path = client.ConfigPath!;
        client.AddOrRepair(new McpServerLaunch(h.McpExecutable, ["--db", "C:\\資料\\contexo.db"]));

        var text = File.ReadAllText(path);
        Assert.Contains("\n  \"mcpServers\": {\n    \"contexo\": {", text.Replace("\r\n", "\n"));
        Assert.Contains("C:\\\\資料\\\\contexo.db", text);
    }

    [Fact]
    public void AddOrRepair_retries_then_reports_a_plain_error_when_the_file_stays_locked()
    {
        using var h = new IntegrationHarness(ClientPlatform.Windows);
        var client = h.Create("cursor");
        var path = h.Install(client);
        File.WriteAllText(path, "{}");

        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var started = DateTime.UtcNow;
        var ex = Assert.Throws<InvalidOperationException>(() => client.AddOrRepair(h.Launch));

        Assert.Equal(JsonMcpClientIntegration.WriteFailedMessage, ex.Message);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(350), "should retry with 200 ms pauses");
    }

    // ---- Remove ----

    [Theory]
    [MemberData(nameof(Clients))]
    public void Remove_only_removes_the_contexo_entry(string clientId, ClientPlatform platform)
    {
        using var h = new IntegrationHarness(platform);
        var client = h.Create(clientId);
        var path = h.Install(client);
        var rootKey = RootKeyOf(clientId);
        File.WriteAllText(path, $$"""{ "keep": 1, "{{rootKey}}": { "other": { "command": "x" } } }""");
        client.AddOrRepair(h.Launch);

        client.Remove();

        var root = ReadJson(path);
        Assert.Equal(1, root["keep"]!.GetValue<int>());
        var servers = root[rootKey]!.AsObject();
        Assert.True(servers.ContainsKey("other"));
        Assert.False(servers.ContainsKey("contexo"));
        Assert.True(File.Exists(path + ".contexo.bak"));
        Assert.Equal(ClientConfigState.NotConfigured, client.GetConfigState());
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public void Remove_does_nothing_when_there_is_no_file_or_no_entry(string clientId, ClientPlatform platform)
    {
        using var h = new IntegrationHarness(platform);
        var client = h.Create(clientId);
        client.Remove();
        Assert.False(File.Exists(client.ConfigPath!));

        var path = h.Install(client);
        var content = $$"""{ "{{RootKeyOf(clientId)}}": { "other": {} } }""";
        File.WriteAllText(path, content);
        client.Remove();

        Assert.Equal(content, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".contexo.bak"));
    }

    [Fact]
    public void Remove_refuses_a_corrupt_file()
    {
        using var h = new IntegrationHarness(ClientPlatform.Windows);
        var client = h.Create("lm-studio");
        var path = h.Install(client);
        File.WriteAllText(path, "{ oops");
        Assert.Throws<InvalidOperationException>(client.Remove);
        Assert.Equal("{ oops", File.ReadAllText(path));
    }

    // ---- BuildManualSnippet ----

    [Theory]
    [MemberData(nameof(Clients))]
    public void Manual_snippet_is_valid_json_with_the_right_root_key(string clientId, ClientPlatform platform)
    {
        using var h = new IntegrationHarness(platform);
        var launch = new McpServerLaunch("C:\\Program Files\\Contexo\\Contexo.Mcp.exe", ["--db", "C:\\Users\\xxx\\contexo.db"]);

        var snippet = h.Create(clientId).BuildManualSnippet(launch);

        var root = JsonNode.Parse(snippet)!.AsObject();
        Assert.Equal([RootKeyOf(clientId)], root.Select(kv => kv.Key));
        var entry = root[RootKeyOf(clientId)]!["contexo"]!.AsObject();
        Assert.Equal(launch.ExecutablePath, entry["command"]!.GetValue<string>());
        Assert.Equal(launch.Arguments, entry["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(clientId == "vscode", entry.ContainsKey("type"));
        JsonDocument.Parse(snippet).Dispose();
    }
}
