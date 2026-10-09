using Contexo.App.AiClients;
using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contexo.App.Tests.AiClients;

public sealed class AiClientsViewModelTests
{
    private readonly FakeStatusService _status = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly TestClock _clock = new();

    private AiClientsViewModel Create() =>
        new(_status, _dialogs, _clipboard, new InlineUiDispatcher(), _clock, NullLogger<AiClientsViewModel>.Instance);

    private async Task<AiClientsViewModel> CreateLoadedAsync()
    {
        var vm = Create();
        await vm.RefreshAsync();
        return vm;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), because);
    }

    // ---- Four states ----

    [Fact]
    public async Task Connected_card_shows_times_and_a_remove_button()
    {
        var connected = _clock.Start.AddHours(-0.2);
        _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.Connected, connected, _clock.Start.AddMinutes(-10));

        var vm = await CreateLoadedAsync();

        var card = Assert.Single(vm.Cards);
        Assert.Equal("已連線", card.StatusText);
        Assert.Equal(ChipTone.Ok, card.Tone);
        Assert.True(card.IsOk);
        Assert.Equal("最後連線：今天 14:28 · 最後查詢：10 分鐘前", card.Description);
        Assert.Equal("移除", card.ActionText);
        Assert.False(card.IsPrimaryAction);
        Assert.Equal("C", card.Initial);
    }

    [Fact]
    public async Task Connected_card_without_a_query_says_there_is_none_yet()
    {
        _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.Connected, _clock.Start.AddMinutes(-3), null);

        var vm = await CreateLoadedAsync();

        Assert.Equal("最後連線：今天 14:37 · 最後查詢：還沒有", vm.Cards[0].Description);
    }

    [Fact]
    public async Task Waiting_card_asks_to_restart_the_client()
    {
        _status.Add("vscode", "VS Code", ClientConnectionState.WaitingForConnection);

        var vm = await CreateLoadedAsync();

        var card = vm.Cards[0];
        Assert.Equal("已設定，等待連線", card.StatusText);
        Assert.Equal(ChipTone.Running, card.Tone);
        Assert.Equal("請把 VS Code 完全關閉後重新開啟。", card.Description);
        Assert.Equal("移除", card.ActionText);
        Assert.False(card.IsPrimaryAction);
    }

    [Fact]
    public async Task NeedsRepair_card_shows_the_problem_and_a_primary_repair_button()
    {
        _status.Add("cursor", "Cursor", ClientConnectionState.NeedsRepair, problem: "設定裡的程式位置已失效，可能是程式更新或搬移過。");

        var vm = await CreateLoadedAsync();

        var card = vm.Cards[0];
        Assert.Equal("設定有問題", card.StatusText);
        Assert.Equal(ChipTone.Warn, card.Tone);
        Assert.True(card.IsWarn);
        Assert.Equal("設定裡的程式位置已失效，可能是程式更新或搬移過。", card.Description);
        Assert.Equal("修復", card.ActionText);
        Assert.True(card.IsPrimaryAction);
    }

    [Fact]
    public async Task NeedsRepair_without_a_problem_text_still_explains_what_to_do()
    {
        _status.Add("cursor", "Cursor", ClientConnectionState.NeedsRepair, problem: null);

        var vm = await CreateLoadedAsync();

        Assert.Equal("設定有問題，請按「修復」。", vm.Cards[0].Description);
    }

    [Fact]
    public async Task NotAdded_card_offers_a_primary_add_button()
    {
        _status.Add("lm-studio", "LM Studio", ClientConnectionState.NotAdded);

        var vm = await CreateLoadedAsync();

        var card = vm.Cards[0];
        Assert.Equal("尚未加入", card.StatusText);
        Assert.Equal(ChipTone.Off, card.Tone);
        Assert.True(card.IsOff);
        Assert.Equal("這台電腦已安裝。", card.Description);
        Assert.Equal("加入", card.ActionText);
        Assert.True(card.IsPrimaryAction);
    }

    // ---- Not installed ----

    [Fact]
    public async Task Not_installed_clients_only_appear_in_the_bottom_line()
    {
        _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.NotAdded);
        _status.Add("vscode", "VS Code", ClientConnectionState.NotInstalled);
        _status.Add("cursor", "Cursor", ClientConnectionState.NotInstalled);
        _status.Add("lm-studio", "LM Studio", ClientConnectionState.NotInstalled);

        var vm = await CreateLoadedAsync();

        Assert.Equal(["claude-desktop"], vm.Cards.Select(c => c.ClientId));
        Assert.True(vm.HasOtherClients);
        Assert.Equal("其他支援的 AI 軟體：VS Code、Cursor、LM Studio", vm.OtherClientsText);
        Assert.False(vm.ShowEmpty);
    }

    [Fact]
    public async Task Bottom_line_is_hidden_when_everything_is_installed()
    {
        _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.NotAdded);

        var vm = await CreateLoadedAsync();

        Assert.False(vm.HasOtherClients);
        Assert.Equal("", vm.OtherClientsText);
    }

    [Fact]
    public async Task Nothing_installed_shows_the_empty_text_only_after_loading()
    {
        _status.Add("cursor", "Cursor", ClientConnectionState.NotInstalled);
        var vm = Create();
        Assert.False(vm.ShowEmpty);

        await vm.RefreshAsync();

        Assert.Empty(vm.Cards);
        Assert.True(vm.ShowEmpty);
        Assert.Equal("其他支援的 AI 軟體：Cursor", vm.OtherClientsText);
    }

    [Fact]
    public async Task Fixed_texts_match_the_spec()
    {
        var vm = await CreateLoadedAsync();

        Assert.Equal("讓 AI 軟體使用我的資料", vm.Heading);
        Assert.Equal("加入後，在這些 AI 軟體裡提問時，AI 就能查詢你的檔案。", vm.Subtitle);
        Assert.Equal("怎麼確認可以用了？", vm.HelpTitle);
        Assert.Equal("在 AI 軟體裡問「用 Contexo 找報價單」，這裡的「最後查詢」時間就會更新。", vm.HelpText);
    }

    [Fact]
    public async Task Refresh_updates_cards_in_place_and_keeps_their_messages()
    {
        var fake = _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.NotAdded);
        var vm = await CreateLoadedAsync();
        var card = vm.Cards[0];
        await card.PrimaryCommand.ExecuteAsync(null);
        _status.SetState("claude-desktop", ClientConnectionState.WaitingForConnection);

        await vm.RefreshAsync();

        Assert.Same(card, Assert.Single(vm.Cards));
        Assert.Equal("已設定，等待連線", card.StatusText);
        Assert.Equal("已加入。請把 Claude Desktop 完全關閉後重新開啟。", card.Notice);
        Assert.Equal(1, fake.AddCalls);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_what_is_shown()
    {
        _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.NotAdded);
        var vm = await CreateLoadedAsync();
        _status.Fails = new IOException("disk");

        await vm.RefreshAsync();

        Assert.Single(vm.Cards);
    }

    // ---- Add / repair ----

    [Fact]
    public async Task Add_writes_the_current_launch_in_the_background_shows_the_hint_and_refreshes()
    {
        var fake = _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.NotAdded);
        fake.OnAdd = () => _status.SetState("claude-desktop", ClientConnectionState.WaitingForConnection);
        var vm = await CreateLoadedAsync();
        var callsBefore = _status.CallCount;
        var card = vm.Cards[0];

        await card.PrimaryCommand.ExecuteAsync(null);

        Assert.Equal(1, fake.AddCalls);
        Assert.Same(_status.CurrentLaunch, fake.LastLaunch);
        Assert.NotEqual(Environment.CurrentManagedThreadId, fake.AddThreadId);
        Assert.Equal("已加入。請把 Claude Desktop 完全關閉後重新開啟。", card.Notice);
        Assert.True(card.HasNotice);
        Assert.False(card.HasError);
        Assert.Equal(callsBefore + 1, _status.CallCount);
        Assert.Equal("已設定，等待連線", card.StatusText);
        Assert.False(card.IsBusy);
        Assert.Empty(_dialogs.Requests);
    }

    [Fact]
    public async Task Repair_calls_AddOrRepair_with_the_current_launch()
    {
        var fake = _status.Add("cursor", "Cursor", ClientConnectionState.NeedsRepair, problem: "壞了");
        var vm = await CreateLoadedAsync();

        await vm.Cards[0].PrimaryCommand.ExecuteAsync(null);

        Assert.Equal(1, fake.AddCalls);
        Assert.Same(_status.CurrentLaunch, fake.LastLaunch);
        Assert.Equal("已修復。請把 Cursor 完全關閉後重新開啟。", vm.Cards[0].Notice);
    }

    [Fact]
    public async Task Add_failure_with_a_plain_message_shows_it_inside_the_card()
    {
        var fake = _status.Add("vscode", "VS Code", ClientConnectionState.NotAdded);
        fake.AddFails = new InvalidOperationException("設定檔格式有誤，為避免破壞你原本的設定，Contexo 沒有修改它。");
        var vm = await CreateLoadedAsync();
        var callsBefore = _status.CallCount;
        var card = vm.Cards[0];

        await card.PrimaryCommand.ExecuteAsync(null);

        Assert.Equal("設定檔格式有誤，為避免破壞你原本的設定，Contexo 沒有修改它。", card.ErrorMessage);
        Assert.True(card.HasError);
        Assert.False(card.HasNotice);
        Assert.Equal("尚未加入", card.StatusText);
        Assert.Equal(callsBefore, _status.CallCount);
        Assert.False(card.IsBusy);
        Assert.True(card.PrimaryCommand.CanExecute(null));
    }

    [Fact]
    public async Task Add_failure_with_any_other_exception_gets_a_generic_sentence_without_technical_text()
    {
        var fake = _status.Add("vscode", "VS Code", ClientConnectionState.NotAdded);
        fake.AddFails = new UnauthorizedAccessException("Access to the path 'C:\\x' is denied.");
        var vm = await CreateLoadedAsync();

        await vm.Cards[0].PrimaryCommand.ExecuteAsync(null);

        Assert.Equal("無法加入，請稍後再試一次。", vm.Cards[0].ErrorMessage);
    }

    [Fact]
    public async Task A_new_attempt_clears_the_previous_error()
    {
        var fake = _status.Add("vscode", "VS Code", ClientConnectionState.NotAdded);
        fake.AddFails = new InvalidOperationException("設定檔格式有誤");
        var vm = await CreateLoadedAsync();
        var card = vm.Cards[0];
        await card.PrimaryCommand.ExecuteAsync(null);
        Assert.True(card.HasError);

        fake.AddFails = null;
        await card.PrimaryCommand.ExecuteAsync(null);

        Assert.False(card.HasError);
        Assert.True(card.HasNotice);
    }

    [Fact]
    public async Task Buttons_are_disabled_while_an_action_runs()
    {
        var fake = _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.NotAdded);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fake.OnAdd = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        var vm = await CreateLoadedAsync();
        var card = vm.Cards[0];

        var running = card.PrimaryCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => entered.IsSet, "the action should have started");

        Assert.True(card.IsBusy);
        Assert.False(card.PrimaryCommand.CanExecute(null));
        Assert.False(card.CopySnippetCommand.CanExecute(null));

        release.Set();
        await running;
        Assert.False(card.IsBusy);
        Assert.True(card.PrimaryCommand.CanExecute(null));
    }

    // ---- Remove ----

    [Fact]
    public async Task Remove_asks_first_and_does_nothing_when_cancelled()
    {
        var fake = _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.Connected, _clock.Start, _clock.Start);
        _dialogs.Answer = false;
        var vm = await CreateLoadedAsync();
        var callsBefore = _status.CallCount;

        await vm.Cards[0].PrimaryCommand.ExecuteAsync(null);

        var request = Assert.Single(_dialogs.Requests);
        Assert.Contains("Claude Desktop", request.Title);
        Assert.Equal(["移除後，Claude Desktop 將無法查詢你的資料。你的資料仍保留在 Contexo。"], request.Lines);
        Assert.Equal("移除", request.ConfirmText);
        Assert.Equal(0, fake.RemoveCalls);
        Assert.False(vm.Cards[0].HasNotice);
        Assert.False(vm.Cards[0].HasError);
        Assert.Equal(callsBefore, _status.CallCount);
        Assert.False(vm.Cards[0].IsBusy);
    }

    [Fact]
    public async Task Remove_removes_the_entry_and_refreshes_when_confirmed()
    {
        var fake = _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.WaitingForConnection);
        fake.OnRemove = () => _status.SetState("claude-desktop", ClientConnectionState.NotAdded);
        var vm = await CreateLoadedAsync();
        var callsBefore = _status.CallCount;

        await vm.Cards[0].PrimaryCommand.ExecuteAsync(null);

        Assert.Equal(1, fake.RemoveCalls);
        Assert.Equal("已移除。", vm.Cards[0].Notice);
        Assert.Equal(callsBefore + 1, _status.CallCount);
        Assert.Equal("尚未加入", vm.Cards[0].StatusText);
        Assert.Equal("加入", vm.Cards[0].ActionText);
    }

    [Fact]
    public async Task Remove_with_a_damaged_config_file_shows_the_message_instead_of_crashing()
    {
        var fake = _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.Connected);
        fake.RemoveFails = new InvalidOperationException("設定檔格式有誤，為避免破壞你原本的設定，Contexo 沒有修改它。");
        var vm = await CreateLoadedAsync();

        await vm.Cards[0].PrimaryCommand.ExecuteAsync(null);

        Assert.Equal("設定檔格式有誤，為避免破壞你原本的設定，Contexo 沒有修改它。", vm.Cards[0].ErrorMessage);
        Assert.False(vm.Cards[0].IsBusy);
    }

    // ---- Copy the manual snippet ----

    [Fact]
    public async Task Copy_puts_the_snippet_on_the_clipboard_and_says_so()
    {
        var fake = _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.NotAdded);
        fake.Snippet = "{ \"mcpServers\": { \"contexo\": {} } }";
        var vm = await CreateLoadedAsync();

        await vm.Cards[0].CopySnippetCommand.ExecuteAsync(null);

        Assert.Equal([fake.Snippet], _clipboard.Texts);
        Assert.Same(_status.CurrentLaunch, fake.SnippetLaunch);
        Assert.Equal("已複製", vm.Cards[0].Notice);
        Assert.False(vm.Cards[0].HasError);
    }

    [Fact]
    public async Task Copy_failure_is_reported_in_the_card()
    {
        _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.NotAdded);
        _clipboard.Succeeds = false;
        var vm = await CreateLoadedAsync();

        await vm.Cards[0].CopySnippetCommand.ExecuteAsync(null);

        Assert.Equal("無法複製，請稍後再試一次。", vm.Cards[0].ErrorMessage);
        Assert.False(vm.Cards[0].HasNotice);
    }

    [Fact]
    public async Task Copy_survives_a_clipboard_that_throws()
    {
        _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.NotAdded);
        _clipboard.Throws = true;
        var vm = await CreateLoadedAsync();

        await vm.Cards[0].CopySnippetCommand.ExecuteAsync(null);

        Assert.True(vm.Cards[0].HasError);
    }

    [Fact]
    public async Task Copy_is_still_possible_after_an_add_failure()
    {
        var fake = _status.Add("vscode", "VS Code", ClientConnectionState.NotAdded);
        fake.AddFails = new InvalidOperationException("設定檔格式有誤");
        var vm = await CreateLoadedAsync();
        await vm.Cards[0].PrimaryCommand.ExecuteAsync(null);

        await vm.Cards[0].CopySnippetCommand.ExecuteAsync(null);

        Assert.Equal("已複製", vm.Cards[0].Notice);
        Assert.False(vm.Cards[0].HasError);
    }

    // ---- Periodic refresh ----

    [Fact]
    public async Task Nothing_is_read_until_the_page_is_shown()
    {
        _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.NotAdded);
        using var vm = Create();

        _clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50);

        Assert.Equal(0, _status.CallCount);
        Assert.Equal(0, _clock.ActiveTimerCount);
    }

    [Fact]
    public async Task Showing_the_page_reads_at_once_and_then_every_ten_seconds()
    {
        _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.NotAdded);
        using var vm = Create();

        vm.OnNavigatedTo();
        await WaitUntilAsync(() => _status.CallCount == 1, "first read");
        Assert.Single(vm.Cards);

        _clock.Advance(TimeSpan.FromSeconds(9));
        await Task.Delay(50);
        Assert.Equal(1, _status.CallCount);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => _status.CallCount == 2, "read after 10 seconds");

        _clock.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => _status.CallCount == 3, "read after 20 seconds");
    }

    [Fact]
    public async Task Leaving_the_page_stops_the_refresh_and_coming_back_resumes_it()
    {
        _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.NotAdded);
        using var vm = Create();
        vm.OnNavigatedTo();
        await WaitUntilAsync(() => _status.CallCount == 1, "first read");

        vm.OnNavigatedFrom();
        await vm.LoopStopped;
        _clock.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(50);
        Assert.Equal(1, _status.CallCount);
        Assert.Equal(0, _clock.ActiveTimerCount);

        vm.OnNavigatedTo();
        await WaitUntilAsync(() => _status.CallCount == 2, "read after coming back");
        _clock.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => _status.CallCount == 3, "periodic read after coming back");
    }

    [Fact]
    public async Task Showing_the_page_twice_does_not_run_two_loops()
    {
        _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.NotAdded);
        using var vm = Create();
        vm.OnNavigatedTo();
        await WaitUntilAsync(() => _status.CallCount == 1, "first read");

        vm.OnNavigatedTo();
        await WaitUntilAsync(() => _status.CallCount == 2, "second read");
        await WaitUntilAsync(() => _clock.ActiveTimerCount == 1, "only one timer is left");

        _clock.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => _status.CallCount == 3, "one read for the tick");
        await Task.Delay(50);
        Assert.Equal(3, _status.CallCount);
    }

    [Fact]
    public async Task Relative_times_move_forward_with_each_refresh()
    {
        _status.Add("claude-desktop", "Claude Desktop", ClientConnectionState.Connected, _clock.Start.AddMinutes(-30), _clock.Start.AddSeconds(-10));
        var vm = await CreateLoadedAsync();
        Assert.EndsWith("最後查詢：剛剛", vm.Cards[0].Description);

        _clock.Advance(TimeSpan.FromMinutes(5));
        await vm.RefreshAsync();

        Assert.EndsWith("最後查詢：5 分鐘前", vm.Cards[0].Description);
    }

    // ---- Times ----

    [Fact]
    public void Absolute_time_says_today_yesterday_or_the_date()
    {
        var now = new DateTimeOffset(2026, 10, 9, 15, 0, 0, TimeSpan.Zero);

        Assert.Equal("今天 14:32", TimeText.Absolute(new DateTimeOffset(2026, 10, 9, 14, 32, 0, TimeSpan.Zero), now, TimeZoneInfo.Utc));
        Assert.Equal("今天 00:05", TimeText.Absolute(new DateTimeOffset(2026, 10, 9, 0, 5, 0, TimeSpan.Zero), now, TimeZoneInfo.Utc));
        Assert.Equal("昨天 23:59", TimeText.Absolute(new DateTimeOffset(2026, 10, 8, 23, 59, 0, TimeSpan.Zero), now, TimeZoneInfo.Utc));
        Assert.Equal("10/7 08:00", TimeText.Absolute(new DateTimeOffset(2026, 10, 7, 8, 0, 0, TimeSpan.Zero), now, TimeZoneInfo.Utc));
        Assert.Equal("12/31 10:00", TimeText.Absolute(new DateTimeOffset(2025, 12, 31, 10, 0, 0, TimeSpan.Zero), now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Absolute_time_uses_the_local_time_zone_for_the_day_boundary()
    {
        var taipei = TimeZoneInfo.CreateCustomTimeZone("test-taipei", TimeSpan.FromHours(8), "Taipei", "Taipei");
        var now = new DateTimeOffset(2026, 10, 9, 17, 0, 0, TimeSpan.Zero); // 10/10 01:00 in Taipei

        Assert.Equal("昨天 22:30", TimeText.Absolute(new DateTimeOffset(2026, 10, 9, 14, 30, 0, TimeSpan.Zero), now, taipei));
        Assert.Equal("今天 00:30", TimeText.Absolute(new DateTimeOffset(2026, 10, 9, 16, 30, 0, TimeSpan.Zero), now, taipei));
    }

    [Theory]
    [InlineData(0, "剛剛")]
    [InlineData(59, "剛剛")]
    [InlineData(60, "1 分鐘前")]
    [InlineData(600, "10 分鐘前")]
    [InlineData(3599, "59 分鐘前")]
    [InlineData(3600, "1 小時前")]
    [InlineData(7 * 3600, "7 小時前")]
    [InlineData(-30, "剛剛")]
    public void Relative_time_counts_seconds_minutes_and_hours(int secondsAgo, string expected)
    {
        var now = new DateTimeOffset(2026, 10, 9, 23, 0, 0, TimeSpan.Zero);

        Assert.Equal(expected, TimeText.Relative(now.AddSeconds(-secondsAgo), now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Relative_time_falls_back_to_the_date_after_a_day()
    {
        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("昨天 10:00", TimeText.Relative(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero), now, TimeZoneInfo.Utc));
        Assert.Equal("10/5 10:00", TimeText.Relative(new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero), now, TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task Connected_description_uses_yesterday_and_dates_for_older_times()
    {
        _status.Add("a", "Claude Desktop", ClientConnectionState.Connected, _clock.Start.AddDays(-1), _clock.Start.AddDays(-5));

        var vm = await CreateLoadedAsync();

        Assert.Equal("最後連線：昨天 14:40 · 最後查詢：10/4 14:40", vm.Cards[0].Description);
    }

    [Fact]
    public void The_parameterless_constructor_used_by_shell_tests_is_inert()
    {
        var vm = new AiClientsViewModel();

        vm.OnNavigatedTo();
        vm.OnNavigatedFrom();

        Assert.Empty(vm.Cards);
        Assert.Equal("AI 軟體", vm.Title);
    }
}
