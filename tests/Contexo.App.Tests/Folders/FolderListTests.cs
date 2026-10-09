using CommunityToolkit.Mvvm.Input;
using Contexo.App.About;
using Contexo.App.Folders;
using Contexo.Core.Abstractions;

namespace Contexo.App.Tests.Folders;

public sealed class FolderListTests
{
    /// <summary>15 folders like the sketch: 1 with a problem, 2 running (one still queued), 12 finished.</summary>
    private static async Task<FoldersFixture> FifteenFoldersAsync()
    {
        var fx = new FoldersFixture();
        var problem = fx.Store.Add(@"C:\Users\chang\OneDrive - 公司", "OneDrive - 公司共用", FolderState.Unavailable);
        var working = fx.Store.Add(@"D:\Projects\工程", "工程");
        var queued = fx.Store.Add(@"C:\Users\chang\Desktop", "桌面");
        var progress = new List<FolderProgress>
        {
            FoldersFixture.Progress(problem.Id, 1314, 1314, 0, FolderState.Unavailable),
            FoldersFixture.Progress(working.Id, 980, 612, 368),
            FoldersFixture.Progress(queued.Id, 41, 0, 41),
        };
        for (var i = 0; i < 12; i++)
        {
            var folder = fx.Store.Add($@"D:\Share\資料夾{i:00}", $"資料夾{i:00}");
            progress.Add(FoldersFixture.Progress(folder.Id, 100 + i, 100 + i, 0));
        }

        fx.Indexing.Raise(FoldersFixture.Snapshot(IndexingState.Indexing, 1000, 300, progress.ToArray()));
        await fx.Folders.RefreshAsync();
        return fx;
    }

    [Fact]
    public async Task Rows_are_grouped_problem_first_then_running_then_done_and_sorted_by_name_inside_a_group()
    {
        var fx = await FifteenFoldersAsync();

        var list = FoldersFixture.Describe(fx.Folders);

        Assert.Equal(
            ["# 有問題 · 1 個資料夾", "OneDrive - 公司共用", "# 處理中 · 2 個資料夾", "工程", "桌面", "# 已完成 · 12 個資料夾"],
            list);
    }

    [Fact]
    public async Task The_done_group_is_collapsed_to_one_line_under_the_all_filter_and_can_be_expanded()
    {
        var fx = await FifteenFoldersAsync();
        var header = fx.Folders.ListItems.OfType<FolderGroupHeader>().Last();

        Assert.True(header.CanToggle);
        Assert.Equal("展開", header.ToggleText);

        header.ToggleCommand!.Execute(null);

        var list = FoldersFixture.Describe(fx.Folders);
        Assert.Equal(6 + 12, list.Count);
        Assert.Equal("收合", fx.Folders.ListItems.OfType<FolderGroupHeader>().Last().ToggleText);
        Assert.Equal("資料夾00", list[6]);
        Assert.Equal("資料夾11", list[^1]);
    }

    [Fact]
    public async Task Summary_buttons_show_counts_and_filter_the_list()
    {
        var fx = await FifteenFoldersAsync();

        Assert.Equal(["全部 15", "有問題 1", "處理中 2", "已完成 12"], fx.Folders.Filters.Select(f => f.Label));

        fx.Folders.SelectedFilter = fx.Folders.Filters[1];
        Assert.Equal(["# 有問題 · 1 個資料夾", "OneDrive - 公司共用"], FoldersFixture.Describe(fx.Folders));

        fx.Folders.SelectedFilter = fx.Folders.Filters[2];
        Assert.Equal(["# 處理中 · 2 個資料夾", "工程", "桌面"], FoldersFixture.Describe(fx.Folders));

        // Under the "done" filter the group is not collapsed.
        fx.Folders.SelectedFilter = fx.Folders.Filters[3];
        var done = FoldersFixture.Describe(fx.Folders);
        Assert.Equal(13, done.Count);
        Assert.False(fx.Folders.ListItems.OfType<FolderGroupHeader>().Single().CanToggle);
    }

    [Fact]
    public async Task The_search_box_appears_only_with_more_than_eight_folders_and_matches_name_and_path()
    {
        var fx = new FoldersFixture();
        for (var i = 0; i < 8; i++)
        {
            fx.Store.Add($@"D:\Share\f{i}", $"f{i}");
        }

        await fx.Folders.RefreshAsync();
        Assert.False(fx.Folders.ShowSearchBox);

        fx.Store.Add(@"D:\Share\法務\合約", "合約");
        await fx.Folders.RefreshAsync();
        Assert.True(fx.Folders.ShowSearchBox);

        fx.Folders.SearchText = "合約";
        Assert.Equal(["# 已完成 · 1 個資料夾", "合約"], FoldersFixture.Describe(fx.Folders));

        fx.Folders.SearchText = @"法務";
        Assert.Contains("合約", FoldersFixture.Describe(fx.Folders));

        fx.Folders.SearchText = "找不到的字";
        Assert.Empty(fx.Folders.ListItems);
        Assert.True(fx.Folders.IsNoMatchVisible);
    }

    [Fact]
    public async Task Searching_shows_matching_finished_folders_even_though_the_group_is_collapsed()
    {
        var fx = await FifteenFoldersAsync();

        fx.Folders.SearchText = "資料夾03";

        Assert.Equal(["# 已完成 · 1 個資料夾", "資料夾03"], FoldersFixture.Describe(fx.Folders));
    }

    [Fact]
    public async Task A_folder_waiting_for_the_deletion_answer_counts_as_a_problem()
    {
        var fx = new FoldersFixture();
        var folder = fx.Store.Add(@"E:\外接硬碟", "外接硬碟");
        fx.Indexing.Raise(FoldersFixture.Snapshot(IndexingState.Idle, 50, 50, FoldersFixture.Progress(folder.Id, 50, 50, 0, FolderState.AwaitingDeletionConfirmation)));

        await fx.Folders.RefreshAsync();

        var row = fx.Folders.Rows.Single();
        Assert.Equal(FolderGroup.Problem, row.Group);
        Assert.Equal("等待您確認", row.StatusText);
        Assert.False(fx.Folders.HasErrorBanner);
    }

    [Fact]
    public async Task Row_texts_follow_the_sketch()
    {
        var fx = await FifteenFoldersAsync();
        fx.Folders.SelectedFilter = fx.Folders.Filters[0];
        var rows = fx.Folders.Rows.ToDictionary(r => r.Name);

        Assert.Equal("612 / 980", rows["工程"].CountText);
        Assert.Equal("處理中 62%", rows["工程"].StatusText);
        Assert.True(rows["工程"].IsRunning);
        Assert.Equal("0 / 41", rows["桌面"].CountText);
        Assert.Equal("排隊中", rows["桌面"].StatusText);
        Assert.Equal("1,314", rows["OneDrive - 公司共用"].CountText);
        Assert.Equal("無法存取", rows["OneDrive - 公司共用"].StatusText);
        Assert.True(rows["OneDrive - 公司共用"].IsWarn);
    }

    [Fact]
    public async Task Unavailable_folders_raise_the_error_banner_with_the_folder_name()
    {
        var fx = await FifteenFoldersAsync();

        Assert.True(fx.Folders.HasErrorBanner);
        Assert.Equal("發生錯誤：『OneDrive - 公司共用』目前無法存取，已暫停這個資料夾。", fx.Folders.ErrorBannerText);

        fx.Folders.ExportReportCommand.Execute(null);
        Assert.Equal([typeof(AboutViewModel)], fx.Navigation.Targets);
    }

    [Fact]
    public async Task A_folder_that_just_finished_shows_the_number_of_updated_files_for_five_minutes()
    {
        var fx = new FoldersFixture();
        var folder = fx.Store.Add(@"C:\Users\chang\Documents", "文件");
        await fx.Folders.RefreshAsync();

        fx.Indexing.Raise(FoldersFixture.Snapshot(IndexingState.Indexing, 3, 0, FoldersFixture.Progress(folder.Id, 2106, 2103, 3)));
        fx.Time.Advance(TimeSpan.FromSeconds(1));
        var done = new IndexingSnapshot(
            IndexingState.Idle, 3, 3, null, null,
            [FoldersFixture.Progress(folder.Id, 2106, 2106, 0)],
            [new RecentActivity(ActivityKind.Updated, 3, fx.Time.GetUtcNow())]);
        fx.Indexing.Raise(done);

        var row = fx.Folders.Rows.Single();
        Assert.Equal("剛更新 3 個檔案", row.StatusText);
        Assert.True(row.IsOk);
        Assert.Equal("2,106", row.CountText);

        fx.Time.Advance(FoldersViewModel.RecentWindow + TimeSpan.FromSeconds(1));
        fx.Indexing.Raise(done with { State = IndexingState.Idle });
        Assert.Equal("已完成", row.StatusText);
    }

    [Fact]
    public async Task The_progress_card_shows_counts_estimate_and_the_current_file_only_while_working()
    {
        var fx = new FoldersFixture();
        await fx.Folders.RefreshAsync();
        Assert.False(fx.Folders.IsProgressVisible);

        fx.Indexing.Raise(new IndexingSnapshot(IndexingState.Indexing, 3420, 1284, @"C:\Docs\2025 年度採購簡報.pptx", TimeSpan.FromMinutes(42), [], []));

        Assert.True(fx.Folders.IsProgressVisible);
        Assert.Equal("1,284 / 3,420 個檔案 · 預估還要約 42 分鐘", fx.Folders.ProgressCountText);
        Assert.Equal("目前：2025 年度採購簡報.pptx", fx.Folders.CurrentFileText);
        Assert.Equal(1284 * 100.0 / 3420, fx.Folders.ProgressValue, 3);
        Assert.Equal("暫停", fx.Folders.PauseButtonText);

        fx.Folders.PauseOrResumeCommand.Execute(null);
        fx.Time.Advance(TimeSpan.FromSeconds(1));
        fx.Indexing.Raise(fx.Indexing.Current);
        Assert.Equal("繼續", fx.Folders.PauseButtonText);
        Assert.Equal("已暫停", fx.Folders.ProgressChipText);

        fx.Time.Advance(TimeSpan.FromSeconds(1));
        fx.Indexing.Raise(IndexingSnapshot.Initial);
        Assert.False(fx.Folders.IsProgressVisible);
    }

    [Fact]
    public async Task Snapshot_events_are_throttled_like_the_status_bar()
    {
        var fx = new FoldersFixture();
        await fx.Folders.RefreshAsync();

        fx.Indexing.Raise(new IndexingSnapshot(IndexingState.Indexing, 100, 10, null, null, [], []));
        Assert.Equal("10 / 100 個檔案 · 正在估算剩餘時間", fx.Folders.ProgressCountText);

        fx.Indexing.Raise(new IndexingSnapshot(IndexingState.Indexing, 100, 20, null, null, [], []));
        fx.Indexing.Raise(new IndexingSnapshot(IndexingState.Indexing, 100, 30, null, null, [], []));
        Assert.Equal("10 / 100 個檔案 · 正在估算剩餘時間", fx.Folders.ProgressCountText);

        fx.Time.Advance(FoldersViewModel.ThrottleInterval);
        Assert.Equal("30 / 100 個檔案 · 正在估算剩餘時間", fx.Folders.ProgressCountText);
    }

    [Fact]
    public async Task Dispose_stops_listening_to_the_indexer()
    {
        var fx = new FoldersFixture();
        Assert.Equal(1, fx.Indexing.SnapshotSubscribers);

        fx.Folders.Dispose();

        Assert.Equal(0, fx.Indexing.SnapshotSubscribers);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Row_menu_commands_reach_the_right_services()
    {
        var fx = new FoldersFixture();
        var folder = fx.Store.Add(@"C:\Users\chang\Documents", "文件");
        await fx.Folders.RefreshAsync();
        var row = fx.Folders.Rows.Single();

        row.OpenInFileManagerCommand.Execute(null);
        row.RevealInFileManagerCommand.Execute(null);
        row.RescanCommand.Execute(null);

        Assert.Equal([@"OpenFolder:C:\Users\chang\Documents", @"Reveal:C:\Users\chang\Documents"], fx.Launcher.Calls);
        Assert.Contains($"RequestRescan:{folder.Id}", fx.Calls);
    }

    [Fact]
    public async Task Excluding_a_folder_asks_first_and_does_nothing_when_cancelled()
    {
        var fx = new FoldersFixture();
        fx.Store.Add(@"C:\Users\chang\Documents", "文件");
        await fx.Folders.RefreshAsync();
        var row = fx.Folders.Rows.Single();

        fx.Dialogs.Answers.Enqueue(false);
        await ((IAsyncRelayCommand)row.ExcludeFromAiCommand).ExecuteAsync(null);
        Assert.Empty(fx.Store.Exclusions);
        Assert.Equal("不要讓 AI 讀這個資料夾", fx.Dialogs.Requests.Single().Title);

        fx.Dialogs.Answers.Enqueue(true);
        await ((IAsyncRelayCommand)row.ExcludeFromAiCommand).ExecuteAsync(null);
        Assert.Equal([(@"C:\Users\chang\Documents", true)], fx.Store.Exclusions);
    }
}
