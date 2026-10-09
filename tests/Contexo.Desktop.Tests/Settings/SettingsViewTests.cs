using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Contexo.App.Settings;
using Contexo.Core.Abstractions;
using Contexo.Desktop.Views.Settings;

namespace Contexo.Desktop.Tests.Settings;

public sealed class SettingsViewTests
{
    private static async Task Pump(Func<bool> done)
    {
        for (var i = 0; i < 300 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(done(), "The condition was not reached in time");
    }

    private static async Task Finish(Task task)
    {
        await Pump(() => task.IsCompleted);
        await task;
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static (MainWindow Window, SettingsShell Test) Open(AppSettings? settings = null)
    {
        var test = new SettingsShell(settings ?? new AppSettings { FirstRunCompleted = true });
        var window = test.CreateWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, test);
    }

    [AvaloniaFact]
    public void The_settings_page_is_shown_by_the_navigation()
    {
        var (window, test) = Open();

        var host = Find<ContentControl>(window, "PageHost");
        Assert.IsType<SettingsView>(host.Presenter?.Child);
        Assert.Same(test.Page, test.Shell.CurrentPage);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(ThemePreference.Light, FontScale.Standard)]
    [InlineData(ThemePreference.Light, FontScale.Large)]
    [InlineData(ThemePreference.Light, FontScale.ExtraLarge)]
    [InlineData(ThemePreference.Dark, FontScale.Standard)]
    [InlineData(ThemePreference.Dark, FontScale.Large)]
    [InlineData(ThemePreference.Dark, FontScale.ExtraLarge)]
    public async Task The_settings_page_renders_in_every_theme_and_text_size(ThemePreference theme, FontScale scale)
    {
        var (window, test) = Open();
        test.Data.DatabaseBytes = (long)(1.8 * 1024 * 1024 * 1024);
        test.Data.Exclusions.Add(new Exclusion(1, "/docs/private", true, DateTimeOffset.UtcNow));

        await test.Settings.SaveAsync(
            new AppSettings { FirstRunCompleted = true, Theme = theme, FontScale = scale },
            CancellationToken.None);
        TestShell.ApplyTheme(theme);
        test.Page.IsAdvancedExpanded = true;
        await Pump(() => test.Page.ExclusionCount == 1);

        // The segmented buttons follow the stored settings.
        Assert.Equal((int)theme, Find<ListBox>(window, "ThemeChoice").SelectedIndex);
        Assert.Equal((int)scale, Find<ListBox>(window, "FontScaleChoice").SelectedIndex);
        Assert.Equal("1.8 GB", Find<TextBlock>(window, "DataSizeText").Text);

        var path = ScreenshotHelper.Capture(window, $"settings-{theme.ToString().ToLowerInvariant()}-{scale.ToString().ToLowerInvariant()}");
        Assert.True(new FileInfo(path).Length > 1000);
        window.Close();
    }

    [AvaloniaFact]
    public void The_advanced_section_is_collapsed_at_first_and_the_collapsed_page_is_captured()
    {
        var (window, test) = Open();

        Assert.False(Find<StackPanel>(window, "AdvancedBody").IsEffectivelyVisible);
        ScreenshotHelper.Capture(window, "settings-collapsed");

        Find<Button>(window, "AdvancedToggle").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(test.Page.IsAdvancedExpanded);
        Assert.True(Find<StackPanel>(window, "AdvancedBody").IsEffectivelyVisible);

        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().First(v => v.Content is StackPanel);
        window.UpdateLayout();
        scroller.Offset = new Avalonia.Vector(0, scroller.Extent.Height);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        ScreenshotHelper.Capture(window, "settings-advanced");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Choosing_a_segment_saves_the_setting_at_once()
    {
        var (window, test) = Open();

        Find<ListBox>(window, "ThemeChoice").SelectedIndex = 2;
        Find<ListBox>(window, "FontScaleChoice").SelectedIndex = 1;
        await Pump(() => test.Settings.Current is { Theme: ThemePreference.Dark, FontScale: FontScale.Large });

        Assert.Equal(1.12, test.Shell.FontScaleFactor, 3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Switches_and_the_size_drop_down_save_the_setting_at_once()
    {
        var (window, test) = Open();

        Find<ToggleSwitch>(window, "TraySwitch").IsChecked = false;
        Find<ToggleSwitch>(window, "IdleSwitch").IsChecked = false;
        Find<ComboBox>(window, "FileSizeChoice").SelectedIndex = 3;
        await Pump(() => test.Settings.Current is { MinimizeToTray: false, FullSpeedOnlyWhenIdle: false, MaxFileSizeMb: null });

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_start_up_switch_calls_the_registration()
    {
        var (window, test) = Open();

        Find<ToggleSwitch>(window, "StartupSwitch").IsChecked = false;
        await Pump(() => !test.Settings.Current.LaunchAtStartup);

        Assert.Equal([false], test.Startup.Calls);
        window.Close();
    }

    [AvaloniaFact]
    public void Email_and_images_are_shown_disabled()
    {
        var (window, _) = Open();

        var boxes = Find<ItemsControl>(window, "CategoryList").GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.Equal(6, boxes.Count);
        foreach (var box in boxes)
        {
            var option = Assert.IsType<CategoryOption>(box.DataContext);
            Assert.Equal(option.IsAvailable, box.IsEffectivelyEnabled);
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task Unchecking_a_file_type_shows_a_confirmation_in_the_window()
    {
        var (window, test) = Open();
        var pdf = Find<ItemsControl>(window, "CategoryList").GetVisualDescendants().OfType<CheckBox>()
            .Single(b => b.DataContext is CategoryOption { Category: FileCategory.Pdf });

        pdf.IsChecked = false;
        await Pump(() => test.Dialogs.IsOpen);
        Assert.Contains("PDF", test.Dialogs.Title);
        ScreenshotHelper.Capture(window, "settings-uncheck-confirm");

        Find<Button>(window, "CancelButton").Command!.Execute(null);
        await Pump(() => !test.Dialogs.IsOpen);
        await test.Page.WhenIdleAsync();

        Assert.True(pdf.IsChecked);
        Assert.Contains(FileCategory.Pdf, test.Settings.Current.EnabledCategories);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_clear_dialog_needs_the_understood_box_and_runs_the_steps_in_order()
    {
        var (window, test) = Open();
        test.Data.DatabaseBytes = (long)(1.8 * 1024 * 1024 * 1024);
        test.Page.IsAdvancedExpanded = true;
        Dispatcher.UIThread.RunJobs();

        var running = test.Page.ClearAllDataCommand.ExecuteAsync(null);
        await Pump(() => test.Dialogs.IsOpen);

        Assert.IsType<ClearDataDialogView>(Find<ContentControl>(window, "DialogContent").Presenter?.Child);
        Assert.Equal("確定要清除全部資料嗎？", test.Dialogs.Title);
        var confirm = Find<Button>(window, "ConfirmButton");
        var acknowledge = Find<CheckBox>(window, "AcknowledgeBox");
        var rebuild = Find<CheckBox>(window, "RebuildBox");
        Assert.False(confirm.IsEffectivelyEnabled);
        Assert.True(rebuild.IsChecked);
        Assert.Equal("我了解需要重新建立，可能要數小時", acknowledge.Content);
        Assert.Equal("清除後立即重新建立", rebuild.Content);
        ScreenshotHelper.Capture(window, "settings-clear-dialog-unchecked");

        // Enter never confirms a dangerous action.
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(test.Dialogs.IsOpen);

        acknowledge.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(confirm.IsEffectivelyEnabled);
        ScreenshotHelper.Capture(window, "settings-clear-dialog-checked");

        confirm.Command!.Execute(null);
        await Finish(running);

        Assert.Equal(["Pause", "Clear", "Rescan:all", "Resume"], test.Log.Entries);
        Assert.Equal("已清除 1.8 GB 資料，正在重新建立。", test.Page.InfoMessage);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("已清除 1.8 GB 資料，正在重新建立。", Find<Border>(window, "InfoBanner").GetVisualDescendants().OfType<TextBlock>().Single().Text);
        ScreenshotHelper.Capture(window, "settings-cleared");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Esc_closes_the_clear_dialog_without_clearing()
    {
        var (window, test) = Open();

        var running = test.Page.ClearAllDataCommand.ExecuteAsync(null);
        await Pump(() => test.Dialogs.IsOpen);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await Finish(running);

        Assert.Empty(test.Log.Entries);
        Assert.False(test.Dialogs.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_exclusion_dialog_lists_entries_and_restores_them()
    {
        var (window, test) = Open();
        test.Data.Exclusions.Add(new Exclusion(1, "/Users/demo/Documents/private", true, new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero)));
        test.Data.Exclusions.Add(new Exclusion(2, "/Users/demo/Documents/plan.docx", false, new DateTimeOffset(2026, 9, 20, 14, 5, 0, TimeSpan.Zero)));
        test.Page.IsAdvancedExpanded = true;
        await Pump(() => test.Page.ExclusionCount == 2);
        Assert.Equal("管理（2）", Find<Button>(window, "ManageExclusionsButton").Content);

        var running = test.Page.ManageExclusionsCommand.ExecuteAsync(null);
        await Pump(() => test.Dialogs.IsOpen);

        Assert.IsType<ExclusionsDialogView>(Find<ContentControl>(window, "DialogContent").Presenter?.Child);
        Assert.Equal("排除的檔案與資料夾", test.Dialogs.Title);
        var list = Find<ItemsControl>(window, "ExclusionList");
        Assert.Equal(2, list.ItemCount);
        ScreenshotHelper.Capture(window, "settings-exclusions-dialog");

        var restore = list.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "恢復"));
        restore.Command!.Execute(restore.CommandParameter);
        await Pump(() => list.ItemCount == 1);
        Assert.Equal(["RemoveExclusion:2", "Rescan:all"], test.Log.Entries);

        Find<Button>(window, "CloseButton").Command!.Execute(null);
        await Finish(running);
        Assert.Equal("管理（1）", test.Page.ExclusionsButtonText);
        window.Close();
    }
}
