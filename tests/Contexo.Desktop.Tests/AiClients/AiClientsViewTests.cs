using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Contexo.App.AiClients;
using Contexo.App.Services;
using Contexo.Core;
using Contexo.Core.Abstractions;
using Contexo.Core.Integrations;
using Contexo.Desktop.Platform.Common;
using Contexo.Desktop.Views.AiClients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contexo.Desktop.Tests.AiClients;

public sealed class AiClientsViewTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 14, 40, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "contexo-t18-" + Guid.NewGuid().ToString("N"));
    private readonly StubStatusService _status = new();
    private readonly StubDialogs _dialogs = new();
    private readonly StubClipboard _clipboard = new();

    public AiClientsViewTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the temporary folder.
        }
    }

    private AiClientsViewModel CreateVm(IAiClientStatusService? status = null) =>
        new(status ?? _status, _dialogs, _clipboard, new AvaloniaUiDispatcher(), new FixedClock(Now), NullLogger<AiClientsViewModel>.Instance);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static async Task PumpAsync(Task task)
    {
        for (var i = 0; i < 500 && !task.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(task.IsCompleted, "The operation did not finish");
        Dispatcher.UIThread.RunJobs();
        await task;
    }

    private static Window Show(AiClientsViewModel vm)
    {
        var view = new AiClientsView { DataContext = vm };
        var window = new Window { Width = 1000, Height = 720, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private void AddFourStates()
    {
        _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.Connected, Now.AddMinutes(-8), Now.AddMinutes(-10));
        _status.Add("vscode", "VS Code", ClientConnectionState.WaitingForConnection);
        _status.Add("cursor", "Cursor", ClientConnectionState.NeedsRepair, problem: "設定裡的程式位置已失效，可能是程式更新或搬移過。");
        _status.Add("lm-studio", "LM Studio", ClientConnectionState.NotAdded);
    }

    private static List<T> Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Where(c => c.Name == name).ToList();

    private static T Only<T>(Control root, string name) where T : Control => Assert.Single(Named<T>(root, name));

    [AvaloniaFact]
    public async Task The_four_states_render_with_the_right_chip_text_and_buttons()
    {
        AddFourStates();
        _status.Add("windsurf", "Windsurf", ClientConnectionState.NotInstalled);
        var vm = CreateVm();
        await PumpAsync(vm.RefreshAsync());
        var window = Show(vm);

        var chips = Named<Border>(window, "StatusChip");
        Assert.Equal(4, chips.Count);
        Assert.Equal(["已連線", "已設定，等待連線", "設定有問題", "尚未加入"], chips.Select(c => ((TextBlock)c.Child!).Text));
        Assert.Contains("Ok", chips[0].Classes);
        Assert.Contains("Running", chips[1].Classes);
        Assert.Contains("Warn", chips[2].Classes);
        Assert.Contains("Off", chips[3].Classes);

        Assert.Equal(
            ["最後連線：今天 14:32 · 最後查詢：10 分鐘前", "請把 VS Code 完全關閉後重新開啟。", "設定裡的程式位置已失效，可能是程式更新或搬移過。", "這台電腦已安裝。"],
            Named<TextBlock>(window, "DescriptionText").Select(t => t.Text));

        var buttons = Named<Button>(window, "ActionButton");
        Assert.Equal(["移除", "移除", "修復", "加入"], buttons.Select(b => (string?)b.Content));
        Assert.Equal([false, false, true, true], buttons.Select(b => b.Classes.Contains("Primary")));

        Assert.Equal("其他支援的 AI 軟體：Windsurf", Only<TextBlock>(window, "OtherClientsText").Text);
        Assert.True(Only<TextBlock>(window, "OtherClientsText").IsVisible);
        Assert.False(Only<TextBlock>(window, "EmptyText").IsVisible);
        Assert.All(Named<Border>(window, "ErrorBox"), box => Assert.False(box.IsVisible));

        ScreenshotHelper.Capture(window, "ai-clients-four-states-light", 1000, 720);
        TestShell.ApplyTheme(Contexo.Core.Abstractions.ThemePreference.Dark);
        Dispatcher.UIThread.RunJobs();
        ScreenshotHelper.Capture(window, "ai-clients-four-states-dark", 1000, 720);
        TestShell.ApplyTheme(Contexo.Core.Abstractions.ThemePreference.Light);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Pressing_add_shows_the_hint_inside_the_card()
    {
        var stub = _status.Add("lm-studio", "LM Studio", ClientConnectionState.NotAdded);
        var vm = CreateVm();
        await PumpAsync(vm.RefreshAsync());
        var window = Show(vm);

        var button = Only<Button>(window, "ActionButton");
        Assert.True(button.Command!.CanExecute(null));
        await PumpAsync(((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)button.Command).ExecuteAsync(null));

        Assert.Equal(1, stub.AddCalls);
        Assert.True(Only<Border>(window, "NoticeBox").IsVisible);
        Assert.Equal("已加入。請把 LM Studio 完全關閉後重新開啟。", Only<TextBlock>(window, "NoticeText").Text);
        ScreenshotHelper.Capture(window, "ai-clients-added-hint", 1000, 360);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_failed_add_shows_the_error_and_a_copy_button_in_the_card()
    {
        var stub = _status.Add("vscode", "VS Code", ClientConnectionState.NotAdded);
        stub.AddFails = new InvalidOperationException("設定檔格式有誤，為避免破壞你原本的設定，Contexo 沒有修改它。");
        var vm = CreateVm();
        await PumpAsync(vm.RefreshAsync());
        var window = Show(vm);

        await PumpAsync(vm.Cards[0].PrimaryCommand.ExecuteAsync(null));

        Assert.True(Only<Border>(window, "ErrorBox").IsVisible);
        Assert.Equal("設定檔格式有誤，為避免破壞你原本的設定，Contexo 沒有修改它。", Only<TextBlock>(window, "ErrorText").Text);
        Assert.False(Only<Border>(window, "NoticeBox").IsVisible);
        ScreenshotHelper.Capture(window, "ai-clients-add-failed", 1000, 360);

        var copy = Only<Button>(window, "ErrorCopyButton");
        await PumpAsync(((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)copy.Command!).ExecuteAsync(null));
        Assert.Single(_clipboard.Texts);
        Assert.Contains("Contexo.Mcp.exe", _clipboard.Texts[0]);
        Assert.Equal("已複製", Only<TextBlock>(window, "NoticeText").Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_more_menu_copies_the_setting_text()
    {
        _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.NotAdded);
        var vm = CreateVm();
        await PumpAsync(vm.RefreshAsync());
        var window = Show(vm);

        var more = Only<Button>(window, "MoreButton");
        var flyout = Assert.IsType<MenuFlyout>(more.Flyout);
        flyout.ShowAt(more);
        Dispatcher.UIThread.RunJobs();

        var item = Assert.IsType<MenuItem>(Assert.Single(flyout.Items));
        Assert.Equal("複製設定內容", item.Header);
        Assert.NotNull(item.Command);
        Assert.True(item.Command.CanExecute(null));
        await PumpAsync(((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)item.Command).ExecuteAsync(null));

        Assert.Single(_clipboard.Texts);
        Assert.Equal("已複製", vm.Cards[0].Notice);
        flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Nothing_installed_shows_a_plain_sentence()
    {
        _status.Add("cursor", "Cursor", ClientConnectionState.NotInstalled);
        _status.Add("lm-studio", "LM Studio", ClientConnectionState.NotInstalled);
        var vm = CreateVm();
        await PumpAsync(vm.RefreshAsync());
        var window = Show(vm);

        Assert.True(Only<TextBlock>(window, "EmptyText").IsVisible);
        Assert.Equal("其他支援的 AI 軟體：Cursor、LM Studio", Only<TextBlock>(window, "OtherClientsText").Text);
        Assert.Empty(Named<Button>(window, "ActionButton"));
        ScreenshotHelper.Capture(window, "ai-clients-none-installed", 1000, 360);
        window.Close();
    }

    [AvaloniaFact]
    public void The_view_is_found_by_the_naming_convention()
    {
        var locator = new ViewLocator();

        Assert.IsType<AiClientsView>(locator.Build(new AiClientsViewModel()));
    }

    [AvaloniaFact]
    public async Task Layout_survives_the_largest_font_scale_and_a_narrow_window()
    {
        AddFourStates();
        var vm = CreateVm();
        await PumpAsync(vm.RefreshAsync());
        var view = new AiClientsView { DataContext = vm };
        var scaled = new LayoutTransformControl { LayoutTransform = new Avalonia.Media.ScaleTransform(1.25, 1.25), Child = view };
        var window = new Window { Width = 900, Height = 700, Content = scaled };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.Bounds.Width > 0);
        Assert.All(Named<Button>(window, "ActionButton"), b => Assert.True(b.Bounds.Right <= view.Bounds.Width + 1));
        ScreenshotHelper.Capture(window, "ai-clients-font-125", 900, 700);
        window.Close();
    }

    /// <summary>
    /// The real integrations and status service on a throw-away profile (temporary folders only; the machine's own
    /// AI client settings are never read or written): press add, the config file gets the entry, then remove.
    /// </summary>
    [AvaloniaFact]
    public async Task Real_integration_adds_and_removes_the_entry_in_a_temporary_profile()
    {
        var home = Path.Combine(_root, "Home");
        var claudeDir = Path.Combine(home, "Library", "Application Support", "Claude");
        Directory.CreateDirectory(claudeDir);
        var configPath = Path.Combine(claudeDir, "claude_desktop_config.json");
        await File.WriteAllTextAsync(configPath, """{ "preferences": { "keep": true }, "mcpServers": { "other": { "command": "x" } } }""");

        var binDir = Path.Combine(_root, "bin");
        Directory.CreateDirectory(binDir);
        var mcp = Path.Combine(binDir, "Contexo.Mcp.exe");
        await File.WriteAllTextAsync(mcp, "stub");

        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(new FixedAppPaths(Path.Combine(_root, "data"), mcp));
        services.AddSingleton(new ClientPathOptions
        {
            Platform = ClientPlatform.MacOS,
            AppData = Path.Combine(_root, "AppData", "Roaming"),
            LocalAppData = Path.Combine(_root, "AppData", "Local"),
            UserProfile = home,
            ApplicationsDirectory = Path.Combine(_root, "Applications"),
        });
        services.AddContexoCore();
        await using var provider = services.BuildServiceProvider();
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        await provider.GetRequiredService<IKnowledgeStore>().InitializeAsync(CancellationToken.None);

        var vm = CreateVm(provider.GetRequiredService<IAiClientStatusService>());
        await PumpAsync(vm.RefreshAsync());
        var card = Assert.Single(vm.Cards);
        Assert.Equal("Claude Desktop", card.DisplayName);
        Assert.Equal("尚未加入", card.StatusText);
        Assert.True(vm.HasOtherClients);

        await PumpAsync(card.PrimaryCommand.ExecuteAsync(null));

        var json = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!;
        Assert.Equal(mcp, (string?)json["mcpServers"]!["contexo"]!["command"]);
        Assert.NotNull(json["mcpServers"]!["other"]);
        Assert.True((bool)json["preferences"]!["keep"]!);
        Assert.Equal("已設定，等待連線", card.StatusText);
        Assert.Equal("已加入。請把 Claude Desktop 完全關閉後重新開啟。", card.Notice);

        await PumpAsync(card.PrimaryCommand.ExecuteAsync(null));

        var after = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!;
        Assert.Null(after["mcpServers"]!["contexo"]);
        Assert.NotNull(after["mcpServers"]!["other"]);
        Assert.Equal("尚未加入", card.StatusText);

        // A damaged file is reported in plain words and left alone.
        await File.WriteAllTextAsync(configPath, "{ not json");
        await PumpAsync(card.PrimaryCommand.ExecuteAsync(null));
        Assert.Equal("設定檔格式有誤，為避免破壞你原本的設定，Contexo 沒有修改它。", card.ErrorMessage);
        Assert.Equal("{ not json", await File.ReadAllTextAsync(configPath));
    }
}
