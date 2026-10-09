using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Contexo.App.About;
using Contexo.App.Folders;
using Contexo.App.Shell;
using Contexo.Core.Abstractions;
using Contexo.Desktop.Views.Folders;
using Microsoft.Extensions.DependencyInjection;

namespace Contexo.Desktop.Tests.Folders;

public sealed class FoldersViewTests
{
    public static TheoryData<ThemePreference, FontScale> Looks => new()
    {
        { ThemePreference.Light, FontScale.Standard },
        { ThemePreference.Dark, FontScale.Standard },
        { ThemePreference.Light, FontScale.ExtraLarge },
        { ThemePreference.Dark, FontScale.Large },
    };

    private static string Suffix(ThemePreference theme, FontScale scale) => $"{theme.ToString().ToLowerInvariant()}-{scale.ToString().ToLowerInvariant()}";

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static async Task<(FoldersHarness Harness, MainWindow Window)> OpenFoldersAsync(ThemePreference theme, FontScale scale, bool fill = true)
    {
        var harness = await FoldersHarness.CreateAsync(new AppSettings { FirstRunCompleted = true, FontScale = scale });
        if (fill)
        {
            await harness.FillLikeTheSketchAsync();
        }

        TestShell.ApplyTheme(theme);
        var window = harness.CreateWindow();
        window.Show();
        harness.Folders.OnNavigatedTo();
        await harness.Folders.PendingRefresh;
        await FoldersHarness.SettleAsync();
        return (harness, window);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Looks))]
    public async Task The_folder_page_with_fifteen_folders_renders(ThemePreference theme, FontScale scale)
    {
        var (harness, window) = await OpenFoldersAsync(theme, scale);
        using var lifetime = harness;

        var view = window.GetVisualDescendants().OfType<FoldersView>().Single();
        Assert.True(Find<Border>(view, "ErrorBanner").IsVisible);
        Assert.Equal("發生錯誤：『OneDrive - 公司共用』目前無法存取，已暫停這個資料夾。", Find<TextBlock>(view, "ErrorBannerText").Text);
        Assert.True(Find<Border>(view, "ProgressCard").IsVisible);
        Assert.Equal("1,284 / 3,420 個檔案 · 預估還要約 42 分鐘", Find<TextBlock>(view, "ProgressCount").Text);
        Assert.True(Find<TextBox>(view, "SearchBox").IsVisible);
        Assert.Equal(["全部 15", "有問題 1", "處理中 2", "已完成 12"], harness.Folders.Filters.Select(f => f.Label));

        // Problem and running rows are shown; the 12 finished folders are folded into one line.
        Assert.Equal(3, view.GetVisualDescendants().OfType<FolderRowView>().Count());
        Assert.Equal(5, Find<ItemsControl>(view, "FailedList").GetVisualDescendants().OfType<FailedFileRowView>().Count());
        Assert.Equal("有 7 個檔案無法讀取", Find<TextBlock>(view, "FailedTitle").Text);
        Assert.True(Find<Button>(view, "ShowAllFailedButton").IsVisible);

        ScreenshotHelper.Capture(window, $"folders-collapsed-{Suffix(theme, scale)}");

        // Unfold the finished group: 15 rows.
        harness.Folders.ListItems.OfType<FolderGroupHeader>().Last().ToggleCommand!.Execute(null);
        await FoldersHarness.SettleAsync();
        Assert.Equal(15, view.GetVisualDescendants().OfType<FolderRowView>().Count());
        ScreenshotHelper.Capture(window, $"folders-expanded-{Suffix(theme, scale)}");

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_empty_folder_page_explains_what_to_do()
    {
        var (harness, window) = await OpenFoldersAsync(ThemePreference.Light, FontScale.Standard, fill: false);
        using var lifetime = harness;

        var view = window.GetVisualDescendants().OfType<FoldersView>().Single();
        Assert.True(Find<Border>(view, "EmptyHint").IsVisible);
        Assert.False(Find<Border>(view, "FolderListCard").IsVisible);
        Assert.False(Find<Border>(view, "ProgressCard").IsVisible);
        ScreenshotHelper.Capture(window, "folders-empty");
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_folder_inside_a_watched_folder_shows_the_note_and_the_pause_button_is_there()
    {
        var (harness, window) = await OpenFoldersAsync(ThemePreference.Light, FontScale.Standard);
        using var lifetime = harness;
        var view = window.GetVisualDescendants().OfType<FoldersView>().Single();
        var inner = Path.Combine(harness.Root, "業務", "北區");
        Directory.CreateDirectory(inner);

        await harness.Folders.AddFolderAsync(inner);
        await FoldersHarness.SettleAsync();

        Assert.True(Find<Border>(view, "NoticeBar").IsVisible);
        Assert.Equal("『北區』已經包含在『業務』裡，不需要重複加入。", Find<TextBlock>(view, "NoticeText").Text);
        Assert.Equal("暫停", Find<Button>(view, "PauseButton").Content);
        ScreenshotHelper.Capture(window, "folders-notice");
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(ThemePreference.Light)]
    [InlineData(ThemePreference.Dark)]
    public async Task The_subfolder_dialog_shows_the_tree_and_loads_deeper_levels_on_expand(ThemePreference theme)
    {
        var (harness, window) = await OpenFoldersAsync(theme, FontScale.Standard);
        using var lifetime = harness;
        var business = Path.Combine(harness.Root, "業務");
        foreach (var sub in new[] { "專案資料/2025 台中案", "專案資料/2026 新竹案", "會議紀錄", "機密", "歸檔" })
        {
            Directory.CreateDirectory(Path.Combine(business, sub.Replace('/', Path.DirectorySeparatorChar)));
        }

        var row = harness.Folders.Rows.Single(r => r.Name == "業務");
        _ = ((IAsyncRelayCommand)row.PickSubfoldersCommand).ExecuteAsync(null);
        var dialog = await WaitForDialogAsync<SubfolderPickerViewModel>(harness);
        await dialog.LoadTask;
        await FoldersHarness.SettleAsync();

        Assert.IsType<SubfolderPickerView>(Find<ContentControl>(window, "DialogContent").Presenter?.Child);
        Assert.Equal("選擇子資料夾 · 業務", harness.Dialogs.Title);
        Assert.Equal(["專案資料", "會議紀錄", "機密", "歸檔"], dialog.Roots.Select(n => n.Name).Order(StringComparer.Ordinal).Select(n => n));

        dialog.Roots.Single(n => n.Name == "機密").IsChecked = false;
        var project = dialog.Roots.Single(n => n.Name == "專案資料");
        project.IsExpanded = true;
        await dialog.LoadTask;
        await FoldersHarness.SettleAsync();
        Assert.Equal(["2025 台中案", "2026 新竹案"], project.Children.Select(n => n.Name).Order(StringComparer.Ordinal));
        var tree = window.GetVisualDescendants().OfType<TreeView>().Single();
        Assert.Contains(tree.GetVisualDescendants().OfType<CheckBox>(), c => c.IsChecked == false);
        ScreenshotHelper.Capture(window, $"folders-subfolder-dialog-{theme.ToString().ToLowerInvariant()}");

        dialog.SaveCommand.Execute(null);
        await FoldersHarness.SettleAsync();

        Assert.False(harness.Dialogs.IsOpen);
        var saved = (await harness.Store.GetFoldersAsync(CancellationToken.None)).Single(f => f.DisplayName == "業務");
        Assert.Equal(["機密"], saved.ExcludedSubfolders);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Esc_closes_the_subfolder_dialog_without_saving()
    {
        var (harness, window) = await OpenFoldersAsync(ThemePreference.Light, FontScale.Standard);
        using var lifetime = harness;
        Directory.CreateDirectory(Path.Combine(harness.Root, "業務", "機密"));
        var row = harness.Folders.Rows.Single(r => r.Name == "業務");
        _ = ((IAsyncRelayCommand)row.PickSubfoldersCommand).ExecuteAsync(null);
        var dialog = await WaitForDialogAsync<SubfolderPickerViewModel>(harness);
        await dialog.LoadTask;
        dialog.Roots.Single().IsChecked = false;

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await FoldersHarness.SettleAsync();

        Assert.False(harness.Dialogs.IsOpen);
        var folder = (await harness.Store.GetFoldersAsync(CancellationToken.None)).Single(f => f.DisplayName == "業務");
        Assert.Empty(folder.ExcludedSubfolders);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(ThemePreference.Light)]
    [InlineData(ThemePreference.Dark)]
    public async Task The_remove_confirmation_is_a_danger_dialog_and_cancel_keeps_the_folder(ThemePreference theme)
    {
        var (harness, window) = await OpenFoldersAsync(theme, FontScale.Standard);
        using var lifetime = harness;
        var row = harness.Folders.Rows.Single(r => r.Name == "業務");

        var removing = ((IAsyncRelayCommand)row.RemoveCommand).ExecuteAsync(null);
        await FoldersHarness.SettleAsync();

        Assert.True(harness.Dialogs.IsOpen);
        Assert.Equal("移除資料夾", harness.Dialogs.Title);
        var confirm = Find<Button>(window, "ConfirmButton");
        Assert.Equal("移除資料夾", confirm.Content);
        Assert.Contains("Danger", confirm.Classes);
        ScreenshotHelper.Capture(window, $"folders-remove-dialog-{theme.ToString().ToLowerInvariant()}");

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await FoldersHarness.SettleAsync();
        await removing;

        Assert.Contains(await harness.Store.GetFoldersAsync(CancellationToken.None), f => f.DisplayName == "業務");
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_mass_deletion_question_is_a_dialog_where_keep_is_the_cancel_answer()
    {
        var (harness, window) = await OpenFoldersAsync(ThemePreference.Light, FontScale.Standard);
        using var lifetime = harness;
        var folder = (await harness.Store.GetFoldersAsync(CancellationToken.None)).Single(f => f.DisplayName == "業務");

        var asking = harness.Folders.HandleMassDeletionAsync(new MassDeletionPending(folder.Id, 1204, 3000));
        await FoldersHarness.SettleAsync();

        Assert.True(harness.Dialogs.IsOpen);
        Assert.Equal("『業務』裡有 1,204 個檔案不見了", harness.Dialogs.Title);
        Assert.Equal("保留", Find<Button>(window, "CancelButton").Content);
        ScreenshotHelper.Capture(window, "folders-mass-deletion-dialog");

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await FoldersHarness.SettleAsync();
        await asking;
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_all_failed_files_dialog_lists_every_file()
    {
        var (harness, window) = await OpenFoldersAsync(ThemePreference.Light, FontScale.Standard);
        using var lifetime = harness;

        var showing = ((IAsyncRelayCommand)harness.Folders.ShowAllFailedCommand).ExecuteAsync(null);
        await FoldersHarness.SettleAsync();

        Assert.True(harness.Dialogs.IsOpen);
        Assert.Equal("無法讀取的檔案", harness.Dialogs.Title);
        var list = Find<ItemsControl>(window, "AllFailedList");
        Assert.Equal(7, list.GetVisualDescendants().OfType<FailedFileRowView>().Count());
        ScreenshotHelper.Capture(window, "folders-failed-files-dialog");

        ((FailedFilesDialogViewModel)harness.Dialogs.Current!).CloseCommand.Execute(null);
        await FoldersHarness.SettleAsync();
        await showing;
        Assert.False(harness.Dialogs.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_window_accepts_dropped_folders_only_while_the_folder_page_is_shown()
    {
        var (harness, window) = await OpenFoldersAsync(ThemePreference.Light, FontScale.Standard);
        using var lifetime = harness;

        Assert.True(DragDrop.GetAllowDrop(window));

        harness.Navigation.NavigateTo<AboutViewModel>();
        await FoldersHarness.SettleAsync();

        Assert.False(DragDrop.GetAllowDrop(window));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_product_container_can_build_the_pages_with_the_new_constructors()
    {
        using var harness = await FoldersHarness.CreateAsync();

        Assert.NotNull(harness.Folders);
        Assert.NotNull(harness.FirstRun);
        Assert.Same(harness.Folders, harness.Shell.NavigationItems[0].Page);
    }

    // ---- Wizard ------------------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(ThemePreference.Light, FontScale.Standard)]
    [InlineData(ThemePreference.Dark, FontScale.Standard)]
    [InlineData(ThemePreference.Light, FontScale.ExtraLarge)]
    public async Task The_wizard_shows_three_steps(ThemePreference theme, FontScale scale)
    {
        using var harness = await FoldersHarness.CreateAsync(new AppSettings { FirstRunCompleted = false, FontScale = scale });
        TestShell.ApplyTheme(theme);
        var window = harness.CreateWindow();
        window.Show();
        harness.FirstRun.OnNavigatedTo();
        await harness.FirstRun.CountingTask;
        await FoldersHarness.SettleAsync();
        var view = window.GetVisualDescendants().OfType<FirstRunView>().Single();
        var suffix = Suffix(theme, scale);

        // Step 1
        Assert.True(Find<StackPanel>(view, "Step1Panel").IsVisible);
        Assert.False(Find<StackPanel>(view, "Step2Panel").IsVisible);
        var locations = Find<ItemsControl>(view, "LocationList");
        var boxes = locations.GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.Equal([true, false, true, false], boxes.Select(b => b.IsChecked == true));
        Assert.Contains("預估第一次建立需要", Find<TextBlock>(view, "EstimateText").Text, StringComparison.Ordinal);
        ScreenshotHelper.Capture(window, $"wizard-step1-{suffix}");

        // Step 2
        await ((IAsyncRelayCommand)harness.FirstRun.NextCommand).ExecuteAsync(null);
        await FoldersHarness.SettleAsync();
        Assert.True(Find<StackPanel>(view, "Step2Panel").IsVisible);
        var categories = Find<ItemsControl>(view, "CategoryList").GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.Equal(5, categories.Count);
        Assert.False(categories[4].IsEnabled);
        ScreenshotHelper.Capture(window, $"wizard-step2-{suffix}");

        // Step 3
        await ((IAsyncRelayCommand)harness.FirstRun.NextCommand).ExecuteAsync(null);
        await FoldersHarness.SettleAsync();
        Assert.True(Find<StackPanel>(view, "Step3Panel").IsVisible);
        Assert.Equal(2, Find<ItemsControl>(view, "ClientList").GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("Card")));
        Assert.Equal("完成", Find<Button>(view, "NextButton").Content);
        ScreenshotHelper.Capture(window, $"wizard-step3-{suffix}");

        window.Close();
    }

    [AvaloniaFact]
    public async Task Finishing_the_wizard_adds_the_ticked_folders_saves_the_settings_and_opens_the_folder_page()
    {
        using var harness = await FoldersHarness.CreateAsync(new AppSettings { FirstRunCompleted = false });
        TestShell.ApplyTheme(ThemePreference.Light);
        var window = harness.CreateWindow();
        window.Show();
        harness.FirstRun.OnNavigatedTo();
        await harness.FirstRun.CountingTask;
        await FoldersHarness.SettleAsync();

        await harness.FirstRun.FinishAsync();
        await FoldersHarness.SettleAsync();

        Assert.True(harness.Settings.Current.FirstRunCompleted);
        Assert.False(harness.Shell.IsFirstRun);
        Assert.IsType<FoldersViewModel>(harness.Shell.CurrentPage);
        var names = (await harness.Store.GetFoldersAsync(CancellationToken.None)).Select(f => f.DisplayName).Order(StringComparer.Ordinal);
        Assert.Equal(["Documents", "OneDrive"], names);
        window.Close();
    }

    private static async Task<T> WaitForDialogAsync<T>(FoldersHarness harness) where T : class
    {
        for (var i = 0; i < 300 && harness.Dialogs.Current is not T; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Dispatcher.UIThread.RunJobs();
        return Assert.IsType<T>(harness.Dialogs.Current);
    }
}
