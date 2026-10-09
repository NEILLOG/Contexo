using Contexo.Core.Abstractions;
using Contexo.Core.Integrations;
using Contexo.Core.Tests.Storage;

namespace Contexo.Core.Tests.Integrations;

public sealed class AiClientStatusServiceTests : IDisposable, IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow.AddHours(-3);

    private readonly IntegrationHarness _h = new(ClientPlatform.Windows);
    private readonly StoreFixture _store = new();

    public Task InitializeAsync() => _store.Store.InitializeAsync(Ct);

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _store.Dispose();
        _h.Dispose();
    }

    private async Task<AiClientStatusService> CreateServiceAsync()
    {
        // Registered out of display order on purpose.
        var all = _h.CreateAll().Reverse().ToList();
        return new AiClientStatusService(_h.Paths, all, _store.Store);
    }

    private JsonMcpClientIntegration Client(string id) => _h.Create(id);

    private void Configure(string id)
    {
        var client = Client(id);
        _h.Install(client);
        client.AddOrRepair(_h.Launch);
    }

    private Task Record(McpEventKind kind, string client, int minutes, string? detail = null) =>
        _store.Store.RecordMcpActivityAsync(new McpActivity(kind, client, "1.0", kind == McpEventKind.ToolCall ? "search" : null, detail, T0.AddMinutes(minutes)), Ct);

    private static AiClientStatus Of(IReadOnlyList<AiClientStatus> statuses, string id) => statuses.Single(s => s.ClientId == id);

    [Fact]
    public async Task Lists_the_clients_in_display_order()
    {
        var service = await CreateServiceAsync();
        Assert.Equal(["claude-desktop", "vscode", "cursor", "lm-studio"], service.Integrations.Select(i => i.ClientId));
        var statuses = await service.GetStatusesAsync(Ct);
        Assert.Equal(["claude-desktop", "vscode", "cursor", "lm-studio"], statuses.Select(s => s.ClientId));
        Assert.Equal(["Claude Desktop", "Visual Studio Code", "Cursor", "LM Studio"], statuses.Select(s => s.DisplayName));
    }

    [Fact]
    public async Task CurrentLaunch_uses_the_executable_and_database_paths()
    {
        var service = await CreateServiceAsync();
        Assert.Equal(_h.McpExecutable, service.CurrentLaunch.ExecutablePath);
        Assert.Equal(["--db", _h.Paths.DatabasePath], service.CurrentLaunch.Arguments);
    }

    [Fact]
    public async Task Six_combinations_map_to_the_right_state()
    {
        // claude-desktop: not installed (nothing created)
        // vscode: installed, not added
        _h.Install(Client("vscode"));
        // cursor: configured, never connected -> waiting
        Configure("cursor");
        // lm-studio: configured and connected
        Configure("lm-studio");
        await Record(McpEventKind.Connected, "lm studio", 0);
        await Record(McpEventKind.ToolCall, "lm studio", 5);

        var service = await CreateServiceAsync();
        var statuses = await service.GetStatusesAsync(Ct);

        Assert.Equal(ClientConnectionState.NotInstalled, Of(statuses, "claude-desktop").State);
        Assert.Equal(ClientConnectionState.NotAdded, Of(statuses, "vscode").State);
        Assert.Equal(ClientConnectionState.WaitingForConnection, Of(statuses, "cursor").State);
        Assert.Null(Of(statuses, "cursor").LastConnectedAt);
        var lm = Of(statuses, "lm-studio");
        Assert.Equal(ClientConnectionState.Connected, lm.State);
        Assert.Equal(T0, lm.LastConnectedAt);
        Assert.Equal(T0.AddMinutes(5), lm.LastQueryAt);
        Assert.Null(lm.Problem);
    }

    [Fact]
    public async Task Broken_config_needs_repair_with_a_plain_reason()
    {
        Configure("claude-desktop");
        File.Delete(_h.McpExecutable);

        var statuses = await (await CreateServiceAsync()).GetStatusesAsync(Ct);

        var status = Of(statuses, "claude-desktop");
        Assert.Equal(ClientConnectionState.NeedsRepair, status.State);
        Assert.False(string.IsNullOrWhiteSpace(status.Problem));
    }

    [Fact]
    public async Task Corrupt_config_needs_repair()
    {
        File.WriteAllText(_h.Install(Client("cursor")), "{ nope");
        var statuses = await (await CreateServiceAsync()).GetStatusesAsync(Ct);
        Assert.Equal(ClientConnectionState.NeedsRepair, Of(statuses, "cursor").State);
        Assert.Contains("格式", Of(statuses, "cursor").Problem);
    }

    [Fact]
    public async Task A_later_error_than_the_last_connection_needs_repair()
    {
        Configure("claude-desktop");
        await Record(McpEventKind.Connected, "claude-ai", 0);
        await Record(McpEventKind.ToolCall, "claude-ai", 5);
        await Record(McpEventKind.Error, "claude-ai", 10, "boom");

        var status = Of(await (await CreateServiceAsync()).GetStatusesAsync(Ct), "claude-desktop");

        Assert.Equal(ClientConnectionState.NeedsRepair, status.State);
        Assert.Equal("上次連線時發生錯誤", status.Problem);
        Assert.Equal(T0, status.LastConnectedAt);
    }

    [Fact]
    public async Task An_error_followed_by_a_new_connection_is_connected()
    {
        Configure("claude-desktop");
        await Record(McpEventKind.Connected, "claude-ai", 0);
        await Record(McpEventKind.Error, "claude-ai", 10, "boom");
        await Record(McpEventKind.Connected, "claude-ai", 20);

        var status = Of(await (await CreateServiceAsync()).GetStatusesAsync(Ct), "claude-desktop");

        Assert.Equal(ClientConnectionState.Connected, status.State);
        Assert.Equal(T0.AddMinutes(20), status.LastConnectedAt);
        Assert.Null(status.LastQueryAt);
    }

    [Fact]
    public async Task Activity_does_not_matter_when_the_client_is_not_added_or_not_installed()
    {
        _h.Install(Client("vscode"));
        await Record(McpEventKind.Connected, "visual studio code", 0);
        await Record(McpEventKind.Connected, "claude-ai", 0);

        var statuses = await (await CreateServiceAsync()).GetStatusesAsync(Ct);

        Assert.Equal(ClientConnectionState.NotAdded, Of(statuses, "vscode").State);
        Assert.Equal(ClientConnectionState.NotInstalled, Of(statuses, "claude-desktop").State);
    }

    [Theory]
    [InlineData("claude-ai", "claude-desktop")]
    [InlineData("Claude-AI", "claude-desktop")]
    [InlineData("Visual Studio Code", "vscode")]
    [InlineData("cursor-vscode", "cursor")]
    [InlineData("LM Studio", "lm-studio")]
    // Not in KnownClientNames: matched by keyword.
    [InlineData("Claude Something", "claude-desktop")]
    [InlineData("Some Code Editor", "vscode")]
    [InlineData("Cursor Nightly", "cursor")]
    [InlineData("lmstudio-beta", "lm-studio")]
    public async Task Activity_is_attributed_by_known_name_then_by_fuzzy_match(string reportedName, string expectedClient)
    {
        foreach (var id in IntegrationHarness.ClientIds)
        {
            Configure(id);
        }

        await Record(McpEventKind.Connected, reportedName, 0);

        var statuses = await (await CreateServiceAsync()).GetStatusesAsync(Ct);

        foreach (var status in statuses)
        {
            Assert.Equal(
                status.ClientId == expectedClient ? ClientConnectionState.Connected : ClientConnectionState.WaitingForConnection,
                status.State);
        }
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("Claude Code")]
    [InlineData("mcp-inspector")]
    [InlineData("")]
    public async Task Unknown_clients_are_ignored(string reportedName)
    {
        foreach (var id in IntegrationHarness.ClientIds)
        {
            Configure(id);
        }

        await Record(McpEventKind.Connected, reportedName, 0);

        var statuses = await (await CreateServiceAsync()).GetStatusesAsync(Ct);

        Assert.All(statuses, s => Assert.Equal(ClientConnectionState.WaitingForConnection, s.State));
    }

    [Fact]
    public async Task Several_names_for_one_client_are_merged()
    {
        Configure("vscode");
        await Record(McpEventKind.Connected, "visual studio code", 0);
        await Record(McpEventKind.Connected, "vscode", 30);
        await Record(McpEventKind.ToolCall, "visual studio code", 40);

        var status = Of(await (await CreateServiceAsync()).GetStatusesAsync(Ct), "vscode");

        Assert.Equal(ClientConnectionState.Connected, status.State);
        Assert.Equal(T0.AddMinutes(30), status.LastConnectedAt);
        Assert.Equal(T0.AddMinutes(40), status.LastQueryAt);
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        var service = await CreateServiceAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetStatusesAsync(cts.Token));
    }
}
