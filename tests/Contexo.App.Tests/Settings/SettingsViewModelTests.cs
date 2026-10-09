using Contexo.App.Settings;
using Contexo.Core.Abstractions;

namespace Contexo.App.Tests.Settings;

public sealed class SettingsViewModelTests
{
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The condition was not reached in time");
    }

    // --- Saving every change -------------------------------------------------------------------------------------

    [Fact]
    public async Task Theme_and_text_size_are_saved_with_the_chosen_values()
    {
        var rig = new SettingsRig();

        rig.ViewModel.ThemeIndex = 2;
        await rig.ViewModel.WhenIdleAsync();
        rig.ViewModel.FontScaleIndex = 1;
        await rig.ViewModel.WhenIdleAsync();

        Assert.Equal(2, rig.Store.Saved.Count);
        Assert.Equal(ThemePreference.Dark, rig.Store.Saved[0].Theme);
        Assert.Equal(FontScale.Standard, rig.Store.Saved[0].FontScale);
        Assert.Equal(ThemePreference.Dark, rig.Store.Saved[1].Theme);
        Assert.Equal(FontScale.Large, rig.Store.Saved[1].FontScale);
    }

    [Fact]
    public async Task Each_behaviour_switch_is_saved()
    {
        var rig = new SettingsRig();

        rig.ViewModel.FullSpeedOnlyWhenIdle = false;
        await rig.ViewModel.WhenIdleAsync();
        Assert.False(rig.Store.Current.FullSpeedOnlyWhenIdle);

        rig.ViewModel.MinimizeToTray = false;
        await rig.ViewModel.WhenIdleAsync();
        Assert.False(rig.Store.Current.MinimizeToTray);

        rig.ViewModel.LaunchAtStartup = false;
        await rig.ViewModel.WhenIdleAsync();
        Assert.False(rig.Store.Current.LaunchAtStartup);

        Assert.Equal(3, rig.Store.Saved.Count);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 50)]
    [InlineData(2, 100)]
    [InlineData(3, null)]
    public async Task File_size_limit_is_saved_in_megabytes_or_as_no_limit(int index, int? expected)
    {
        var rig = new SettingsRig(new AppSettings { FirstRunCompleted = true, MaxFileSizeMb = index == 1 ? 100 : 50 });

        rig.ViewModel.MaxFileSizeIndex = index;
        await rig.ViewModel.WhenIdleAsync();

        Assert.Equal(expected, rig.Store.Current.MaxFileSizeMb);
        Assert.Equal(["20 MB", "50 MB", "100 MB", "不限制"], rig.ViewModel.FileSizeLabels);
    }

    [Fact]
    public void The_page_starts_with_the_stored_values()
    {
        var rig = new SettingsRig(new AppSettings
        {
            FirstRunCompleted = true,
            Theme = ThemePreference.Light,
            FontScale = FontScale.ExtraLarge,
            MaxFileSizeMb = null,
            MinimizeToTray = false,
        });

        Assert.Equal(1, rig.ViewModel.ThemeIndex);
        Assert.Equal(2, rig.ViewModel.FontScaleIndex);
        Assert.Equal(3, rig.ViewModel.MaxFileSizeIndex);
        Assert.False(rig.ViewModel.MinimizeToTray);
        Assert.Empty(rig.Store.Saved);
    }

    [Fact]
    public async Task Quick_successive_changes_end_with_the_last_one_and_are_saved_in_order()
    {
        var rig = new SettingsRig();
        rig.Store.HoldSaves();

        rig.ViewModel.ThemeIndex = 1;
        await WaitUntil(() => rig.Store.SaveStarted == 1);
        rig.ViewModel.ThemeIndex = 2;
        rig.ViewModel.FontScaleIndex = 2;
        rig.ViewModel.ThemeIndex = 0;

        // Saves are serialized: the others wait for the first one.
        await Task.Delay(50);
        Assert.Equal(1, rig.Store.SaveStarted);

        rig.Store.Release();
        await rig.ViewModel.WhenIdleAsync();

        Assert.Equal(4, rig.Store.Saved.Count);
        Assert.Equal(ThemePreference.Light, rig.Store.Saved[0].Theme);
        Assert.Equal(ThemePreference.System, rig.Store.Current.Theme);
        Assert.Equal(FontScale.ExtraLarge, rig.Store.Current.FontScale);
        Assert.Equal(0, rig.ViewModel.ThemeIndex);
        Assert.Equal(2, rig.ViewModel.FontScaleIndex);
    }

    [Fact]
    public async Task A_failed_save_shows_a_message_and_puts_the_screen_back()
    {
        var rig = new SettingsRig();
        rig.Store.FailWith = new IOException("disk full");

        rig.ViewModel.ThemeIndex = 2;
        await rig.ViewModel.WhenIdleAsync();

        Assert.True(rig.ViewModel.HasErrorMessage);
        Assert.Equal(0, rig.ViewModel.ThemeIndex);
        Assert.Equal(ThemePreference.System, rig.Store.Current.Theme);
    }

    [Fact]
    public async Task Changes_made_elsewhere_show_up_without_being_saved_again()
    {
        var rig = new SettingsRig();

        rig.Store.ChangeFromOutside(rig.Store.Current with { Theme = ThemePreference.Dark, MinimizeToTray = false });
        await rig.ViewModel.WhenIdleAsync();

        Assert.Equal(2, rig.ViewModel.ThemeIndex);
        Assert.False(rig.ViewModel.MinimizeToTray);
        Assert.Empty(rig.Store.Saved);
    }

    // --- File types ----------------------------------------------------------------------------------------------

    [Fact]
    public void Email_and_images_are_shown_but_cannot_be_chosen()
    {
        var rig = new SettingsRig();

        Assert.Equal(6, rig.ViewModel.Categories.Count);
        Assert.All(rig.ViewModel.Categories.Where(c => c.Category is FileCategory.Email or FileCategory.Images), c =>
        {
            Assert.False(c.IsAvailable);
            Assert.False(c.IsChecked);
            Assert.Contains("之後的版本提供", c.Hint);
        });
        Assert.All(rig.ViewModel.Categories.Where(c => c.IsAvailable), c => Assert.True(c.IsChecked));
    }

    [Fact]
    public async Task The_last_file_type_cannot_be_unchecked()
    {
        // Email is stored as enabled by default but is not available in this version, so it does not count.
        var rig = new SettingsRig(new AppSettings
        {
            FirstRunCompleted = true,
            EnabledCategories = [FileCategory.Pdf, FileCategory.Email],
        });
        rig.Dialogs.ConfirmAnswer = true;
        var pdf = rig.Category(FileCategory.Pdf);

        pdf.IsChecked = false;
        await rig.ViewModel.WhenIdleAsync();

        Assert.True(pdf.IsChecked);
        Assert.Empty(rig.Store.Saved);
        Assert.Empty(rig.Dialogs.ConfirmRequests);
        Assert.True(rig.ViewModel.HasErrorMessage);
    }

    [Fact]
    public async Task Unchecking_a_file_type_asks_first_and_cancel_changes_nothing()
    {
        var rig = new SettingsRig();
        rig.Dialogs.ConfirmAnswer = false;
        var presentations = rig.Category(FileCategory.Presentations);

        presentations.IsChecked = false;
        await rig.ViewModel.WhenIdleAsync();

        var request = Assert.Single(rig.Dialogs.ConfirmRequests);
        Assert.Contains("取消後，AI 將查不到這類檔案的內容。", request.Lines);
        Assert.Contains("你的原始檔案不受影響。", request.Lines);
        Assert.True(presentations.IsChecked);
        Assert.Empty(rig.Store.Saved);
    }

    [Fact]
    public async Task Confirming_removes_only_that_file_type_and_keeps_the_others()
    {
        var rig = new SettingsRig();
        rig.Dialogs.ConfirmAnswer = true;

        rig.Category(FileCategory.Spreadsheets).IsChecked = false;
        await rig.ViewModel.WhenIdleAsync();

        var saved = Assert.Single(rig.Store.Saved);
        Assert.Equal(
            [FileCategory.Documents, FileCategory.Presentations, FileCategory.Pdf, FileCategory.Email],
            saved.EnabledCategories);
        Assert.False(rig.Category(FileCategory.Spreadsheets).IsChecked);
    }

    [Fact]
    public async Task Checking_a_file_type_again_needs_no_confirmation()
    {
        var rig = new SettingsRig(new AppSettings
        {
            FirstRunCompleted = true,
            EnabledCategories = [FileCategory.Documents],
        });

        rig.Category(FileCategory.Pdf).IsChecked = true;
        await rig.ViewModel.WhenIdleAsync();

        Assert.Empty(rig.Dialogs.ConfirmRequests);
        Assert.Equal([FileCategory.Documents, FileCategory.Pdf], rig.Store.Current.EnabledCategories);
    }

    [Fact]
    public async Task Changing_file_types_does_not_ask_for_a_rescan_because_the_indexing_service_notices_by_itself()
    {
        var rig = new SettingsRig();
        rig.Dialogs.ConfirmAnswer = true;

        rig.Category(FileCategory.Pdf).IsChecked = false;
        await rig.ViewModel.WhenIdleAsync();

        Assert.DoesNotContain(rig.Log.Entries, e => e.StartsWith("Rescan"));
    }

    // --- Start-up --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_start_up_switch_changes_the_system_entry_and_the_setting()
    {
        var rig = new SettingsRig();
        rig.Startup.IsEnabled = true;

        rig.ViewModel.LaunchAtStartup = false;
        await rig.ViewModel.WhenIdleAsync();
        Assert.Equal([false], rig.Startup.Calls);
        Assert.False(rig.Store.Current.LaunchAtStartup);

        rig.ViewModel.LaunchAtStartup = true;
        await rig.ViewModel.WhenIdleAsync();
        Assert.Equal([false, true], rig.Startup.Calls);
        Assert.True(rig.Store.Current.LaunchAtStartup);
    }

    [Fact]
    public async Task When_the_system_refuses_the_setting_stays_and_the_switch_goes_back()
    {
        var rig = new SettingsRig();
        rig.Startup.FailWith = new UnauthorizedAccessException();

        rig.ViewModel.LaunchAtStartup = false;
        await rig.ViewModel.WhenIdleAsync();

        Assert.True(rig.ViewModel.LaunchAtStartup);
        Assert.True(rig.Store.Current.LaunchAtStartup);
        Assert.Empty(rig.Store.Saved);
        Assert.True(rig.ViewModel.HasErrorMessage);
    }

    [Fact]
    public void After_the_first_run_a_missing_or_outdated_entry_is_repaired_on_start()
    {
        var rig = new SettingsRig(new AppSettings { FirstRunCompleted = true, LaunchAtStartup = true });
        // The entry points elsewhere (program moved): IsEnabled reports false for it.
        Assert.Equal([], rig.Startup.Calls);

        var repaired = new FakeStartupRegistration { IsEnabled = false };
        _ = NewViewModel(rig, repaired);
        Assert.Equal([true], repaired.Calls);
    }

    [Fact]
    public void An_entry_is_removed_when_the_setting_is_off()
    {
        var rig = new SettingsRig(new AppSettings { FirstRunCompleted = true, LaunchAtStartup = false });
        var startup = new FakeStartupRegistration { IsEnabled = true };

        _ = NewViewModel(rig, startup);

        Assert.Equal([false], startup.Calls);
    }

    [Fact]
    public void Nothing_is_registered_before_the_first_run_is_finished()
    {
        var rig = new SettingsRig(new AppSettings { FirstRunCompleted = false, LaunchAtStartup = true });
        var startup = new FakeStartupRegistration { IsEnabled = false };

        _ = NewViewModel(rig, startup);
        Assert.Empty(startup.Calls);

        // The wizard finishes: now the entry is created.
        rig.Store.ChangeFromOutside(rig.Store.Current with { FirstRunCompleted = true });
        Assert.Equal([true], startup.Calls);
    }

    private static SettingsViewModel NewViewModel(SettingsRig rig, FakeStartupRegistration startup) => new(
        rig.Store,
        rig.Indexing,
        rig.Data,
        new FakeAppPaths(),
        rig.Dialogs,
        startup,
        rig.Launcher,
        rig.Clipboard,
        rig.Clients,
        new SyncDispatcher(),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsViewModel>.Instance);

    // --- Clear all data ------------------------------------------------------------------------------------------

    private static void Script(SettingsRig rig, bool acknowledge, bool rebuild, bool confirm)
    {
        rig.Dialogs.OnShow = dialog =>
        {
            var vm = Assert.IsType<ClearDataDialogViewModel>(dialog);
            vm.RebuildNow = rebuild;
            vm.Acknowledged = acknowledge;
            if (confirm)
            {
                vm.ConfirmCommand.Execute(null);
            }
            else
            {
                vm.CancelCommand.Execute(null);
            }
        };
    }

    [Fact]
    public void The_clear_dialog_cannot_be_confirmed_before_the_understood_box_is_ticked()
    {
        var dialog = new ClearDataDialogViewModel();

        Assert.Equal("確定要清除全部資料嗎？", dialog.Title);
        Assert.Equal("我了解需要重新建立，可能要數小時", dialog.AcknowledgeText);
        Assert.Equal("清除後立即重新建立", dialog.RebuildText);
        Assert.True(dialog.RebuildNow);
        Assert.Equal(3, dialog.Lines.Count);
        Assert.False(dialog.CanConfirm);
        Assert.False(dialog.ConfirmCommand.CanExecute(null));

        dialog.ConfirmCommand.Execute(null);
        Assert.False(dialog.Confirmed);

        dialog.Acknowledged = true;
        Assert.True(dialog.ConfirmCommand.CanExecute(null));
        dialog.ConfirmCommand.Execute(null);
        Assert.True(dialog.Confirmed);
    }

    [Fact]
    public async Task Clearing_pauses_clears_rescans_and_resumes_in_that_order()
    {
        var rig = new SettingsRig();
        rig.Data.DatabaseBytes = (long)(1.8 * 1024 * 1024 * 1024);
        rig.Data.DatabaseBytesAfterClear = 0;
        Script(rig, acknowledge: true, rebuild: true, confirm: true);

        await rig.ViewModel.ClearAllDataCommand.ExecuteAsync(null);

        Assert.Equal(["Pause", "Clear", "Rescan:all", "Resume"], rig.Log.Entries);
        Assert.Equal("已清除 1.8 GB 資料，正在重新建立。", rig.ViewModel.InfoMessage);
        Assert.False(rig.ViewModel.IsClearing);
        Assert.True(rig.ViewModel.IsAdvancedEnabled);
    }

    [Fact]
    public async Task Clearing_without_rebuilding_does_not_ask_for_a_rescan()
    {
        var rig = new SettingsRig();
        rig.Data.DatabaseBytes = 5 * 1024 * 1024;
        Script(rig, acknowledge: true, rebuild: false, confirm: true);

        await rig.ViewModel.ClearAllDataCommand.ExecuteAsync(null);

        Assert.Equal(["Pause", "Clear", "Resume"], rig.Log.Entries);
        Assert.Equal("已清除 5.0 MB 資料。", rig.ViewModel.InfoMessage);
    }

    [Fact]
    public async Task Cancelling_the_clear_dialog_does_nothing()
    {
        var rig = new SettingsRig();
        Script(rig, acknowledge: true, rebuild: true, confirm: false);

        await rig.ViewModel.ClearAllDataCommand.ExecuteAsync(null);

        Assert.Empty(rig.Log.Entries);
        Assert.Null(rig.ViewModel.InfoMessage);
    }

    [Fact]
    public async Task A_dialog_closed_with_Esc_does_nothing()
    {
        var rig = new SettingsRig();
        rig.Dialogs.OnShow = _ => { };

        await rig.ViewModel.ClearAllDataCommand.ExecuteAsync(null);

        Assert.Empty(rig.Log.Entries);
    }

    [Fact]
    public async Task A_failed_clear_still_resumes_and_shows_an_error()
    {
        var rig = new SettingsRig();
        rig.Data.ClearFailure = new InvalidOperationException("disk error");
        Script(rig, acknowledge: true, rebuild: true, confirm: true);

        await rig.ViewModel.ClearAllDataCommand.ExecuteAsync(null);

        Assert.Equal(["Pause", "Clear", "Resume"], rig.Log.Entries);
        Assert.True(rig.ViewModel.HasErrorMessage);
        Assert.False(rig.ViewModel.HasInfoMessage);
        Assert.False(rig.ViewModel.IsClearing);
    }

    // --- Exclusions ----------------------------------------------------------------------------------------------

    private static Exclusion Excluded(long id, string path, bool isFolder) =>
        new(id, path, isFolder, new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero));

    [Fact]
    public async Task The_exclusion_dialog_lists_every_entry_and_restoring_removes_it_and_asks_for_a_rescan()
    {
        var rig = new SettingsRig();
        rig.Data.Exclusions.Add(Excluded(1, "/docs/private", true));
        rig.Data.Exclusions.Add(Excluded(2, "/docs/plan.docx", false));
        ExclusionsDialogViewModel? dialog = null;
        rig.Dialogs.OnShow = d =>
        {
            dialog = Assert.IsType<ExclusionsDialogViewModel>(d);
            Assert.Equal(2, dialog.Items.Count);
            Assert.Equal("資料夾", dialog.Items.Single(i => i.Exclusion.Id == 1).TypeText);
            Assert.Equal("檔案", dialog.Items.Single(i => i.Exclusion.Id == 2).TypeText);
            dialog.RestoreCommand.Execute(dialog.Items.Single(i => i.Exclusion.Id == 1));
        };

        await rig.ViewModel.ManageExclusionsCommand.ExecuteAsync(null);

        Assert.Equal(["RemoveExclusion:1", "Rescan:all"], rig.Log.Entries);
        Assert.Single(dialog!.Items);
        // The page shows the new count after the dialog closed.
        Assert.Equal("管理（1）", rig.ViewModel.ExclusionsButtonText);
    }

    [Fact]
    public async Task An_empty_exclusion_list_says_so()
    {
        var rig = new SettingsRig();
        var dialog = new ExclusionsDialogViewModel(rig.Data, rig.Indexing, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        await dialog.LoadAsync();

        Assert.True(dialog.IsEmpty);
        Assert.Contains("沒有", dialog.Summary);
    }

    // --- Advanced ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Opening_the_advanced_section_reads_the_data_size_and_exclusion_count()
    {
        var rig = new SettingsRig();
        rig.Data.DatabaseBytes = (long)(1.8 * 1024 * 1024 * 1024);
        rig.Data.Exclusions.Add(Excluded(1, "/a", false));
        rig.Data.Exclusions.Add(Excluded(2, "/b", false));
        Assert.False(rig.ViewModel.IsAdvancedExpanded);

        rig.ViewModel.IsAdvancedExpanded = true;
        await WaitUntil(() => rig.ViewModel.ExclusionCount == 2);

        Assert.Equal("1.8 GB", rig.ViewModel.DataSizeText);
        Assert.Equal("管理（2）", rig.ViewModel.ExclusionsButtonText);
        Assert.Equal("/data/Contexo", rig.ViewModel.DataDirectory);
    }

    [Fact]
    public void The_folder_buttons_open_the_data_and_log_folders()
    {
        var rig = new SettingsRig();

        rig.ViewModel.OpenDataFolderCommand.Execute(null);
        rig.ViewModel.OpenLogsFolderCommand.Execute(null);

        Assert.Equal(["/data/Contexo", "/data/Contexo/logs"], rig.Launcher.OpenedFolders);
    }

    [Fact]
    public async Task The_chosen_AI_software_setup_text_is_copied()
    {
        var rig = new SettingsRig();
        Assert.Equal(["Claude Desktop", "Cursor"], rig.ViewModel.ClientNames);

        rig.ViewModel.SelectedClientIndex = 1;
        await rig.ViewModel.CopyClientSnippetCommand.ExecuteAsync(null);

        Assert.Equal("{\"cursor\":\"/app/Contexo.Mcp\"}", rig.Clipboard.Text);
        Assert.True(rig.ViewModel.HasInfoMessage);
    }

    [Fact]
    public async Task A_clipboard_that_is_not_available_shows_an_error()
    {
        var rig = new SettingsRig();
        rig.Clipboard.Available = false;

        await rig.ViewModel.CopyClientSnippetCommand.ExecuteAsync(null);

        Assert.True(rig.ViewModel.HasErrorMessage);
        Assert.Null(rig.Clipboard.Text);
    }

    [Fact]
    public async Task The_page_without_services_used_by_other_tests_does_nothing()
    {
        var page = new SettingsViewModel();

        page.ThemeIndex = 2;
        page.Categories[0].IsChecked = false;
        await page.ClearAllDataCommand.ExecuteAsync(null);
        page.OnNavigatedTo();

        Assert.Equal("設定", page.Title);
    }

    // --- Size text -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0L, "0.0 KB")]
    [InlineData(512L, "0.5 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048575L, "1.0 MB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(5_452_595L, "5.2 MB")]
    [InlineData(1_073_741_823L, "1.0 GB")]
    [InlineData(1_073_741_824L, "1.0 GB")]
    [InlineData(1_932_735_283L, "1.8 GB")]
    [InlineData(-5L, "0.0 KB")]
    public void Sizes_use_KB_below_one_megabyte_and_one_decimal(long bytes, string expected)
    {
        Assert.Equal(expected, DataSizeFormatter.Format(bytes));
    }
}
