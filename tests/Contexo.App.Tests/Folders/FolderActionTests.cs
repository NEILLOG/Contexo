using CommunityToolkit.Mvvm.Input;
using Contexo.App.Folders;
using Contexo.Core.Abstractions;

namespace Contexo.App.Tests.Folders;

public sealed class FolderActionTests
{
    // ---- Adding folders ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Adding_an_ordinary_folder_stores_it_and_asks_for_a_rescan()
    {
        var fx = new FoldersFixture();

        await fx.Folders.AddFolderAsync(@"D:\Share\業務");

        var folder = Assert.Single(fx.Store.Folders);
        Assert.Equal(@"D:\Share\業務", folder.Path);
        Assert.Equal(["AddFolder:D:\\Share\\業務", $"RequestRescan:{folder.Id}"], fx.Calls);
        Assert.Equal(["業務"], FoldersFixture.Describe(fx.Folders).Where(s => !s.StartsWith('#')));
        Assert.False(fx.Folders.HasNotice);
    }

    [Fact]
    public async Task A_folder_inside_a_watched_folder_is_not_added_and_a_note_is_shown()
    {
        var fx = new FoldersFixture();
        fx.Store.Add(@"C:\Users\chang\Documents", "文件");

        await fx.Folders.AddFolderAsync(@"C:\Users\chang\Documents\2025 台中案");

        Assert.Single(fx.Store.Folders);
        Assert.DoesNotContain(fx.Calls, c => c.StartsWith("AddFolder", StringComparison.Ordinal));
        Assert.Equal("『2025 台中案』已經包含在『文件』裡，不需要重複加入。", fx.Folders.NoticeText);
        Assert.Empty(fx.Dialogs.Requests);

        fx.Folders.DismissNoticeCommand.Execute(null);
        Assert.False(fx.Folders.HasNotice);
    }

    [Fact]
    public async Task A_folder_whose_name_only_starts_like_a_watched_folder_is_a_separate_folder()
    {
        var fx = new FoldersFixture();
        fx.Store.Add(@"C:\A\報價", "報價");

        await fx.Folders.AddFolderAsync(@"C:\A\報價單");

        Assert.Equal(2, fx.Store.Folders.Count);
        Assert.False(fx.Folders.HasNotice);
    }

    [Fact]
    public async Task Adding_the_same_folder_twice_only_shows_a_note()
    {
        var fx = new FoldersFixture();
        fx.Store.Add(@"C:\Users\chang\Documents", "文件");

        await fx.Folders.AddFolderAsync(@"c:\users\chang\documents\");

        Assert.Single(fx.Store.Folders);
        Assert.Equal("『文件』已經加入了。", fx.Folders.NoticeText);
    }

    [Fact]
    public async Task A_folder_containing_watched_folders_asks_to_merge_and_replaces_them_when_confirmed()
    {
        var fx = new FoldersFixture();
        var a = fx.Store.Add(@"D:\Share\業務\北區", "北區");
        var b = fx.Store.Add(@"D:\Share\業務\南區", "南區");
        fx.Store.Add(@"D:\Other", "其他");
        fx.Dialogs.Answers.Enqueue(true);

        await fx.Folders.AddFolderAsync(@"D:\Share\業務");

        var request = fx.Dialogs.Requests.Single();
        Assert.Equal("合併資料夾", request.Title);
        Assert.Contains("『業務』包含了已加入的『北區』、『南區』，要合併成一個嗎？", request.Lines);
        Assert.Equal(["其他", "業務"], fx.Store.Folders.Select(f => f.DisplayName).Order());
        var added = fx.Store.Folders.Single(f => f.DisplayName == "業務");
        // The sub folders are removed first, then the new folder is added and read.
        Assert.Equal([$"RemoveFolder:{a.Id}", $"RemoveFolder:{b.Id}", "AddFolder:D:\\Share\\業務", $"RequestRescan:{added.Id}"], fx.Calls);
    }

    [Fact]
    public async Task Declining_the_merge_changes_nothing()
    {
        var fx = new FoldersFixture();
        fx.Store.Add(@"D:\Share\業務\北區", "北區");
        fx.Dialogs.Answers.Enqueue(false);

        await fx.Folders.AddFolderAsync(@"D:\Share\業務");

        Assert.Equal(["北區"], fx.Store.Folders.Select(f => f.DisplayName));
        Assert.Empty(fx.Calls);
    }

    [Fact]
    public async Task A_missing_folder_is_not_added()
    {
        var fx = new FoldersFixture();
        fx.Tree.Missing.Add(PathRelations.Normalize(@"E:\不見了"));

        await fx.Folders.AddFolderAsync(@"E:\不見了");

        Assert.Empty(fx.Store.Folders);
        Assert.Equal("找不到『不見了』，可能已被移動或刪除。", fx.Folders.NoticeText);
    }

    [Fact]
    public async Task The_add_button_uses_the_folder_picker_and_ignores_a_cancelled_pick()
    {
        var fx = new FoldersFixture();

        fx.Picker.Result = null;
        await ((IAsyncRelayCommand)fx.Folders.AddFolderCommand).ExecuteAsync(null);
        Assert.Empty(fx.Store.Folders);

        fx.Picker.Result = @"D:\Share\採購";
        await ((IAsyncRelayCommand)fx.Folders.AddFolderCommand).ExecuteAsync(null);
        Assert.Equal(["採購"], fx.Store.Folders.Select(f => f.DisplayName));
    }

    [Fact]
    public async Task Dropped_folders_are_added_and_missing_ones_are_ignored()
    {
        var fx = new FoldersFixture();
        fx.Tree.Missing.Add(PathRelations.Normalize(@"D:\檔案.docx"));

        await fx.Folders.AddDroppedFoldersAsync([@"D:\Share\人事公告", @"D:\檔案.docx", @"D:\Share\合約"]);

        Assert.Equal(["人事公告", "合約"], fx.Store.Folders.Select(f => f.DisplayName));
    }

    // ---- Removing ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Removing_a_folder_explains_that_the_original_files_stay_and_cancel_keeps_the_folder()
    {
        var fx = new FoldersFixture();
        fx.Store.Add(@"D:\Share\業務", "業務");
        await fx.Folders.RefreshAsync();
        var row = fx.Folders.Rows.Single();
        fx.Dialogs.Answers.Enqueue(false);

        await ((IAsyncRelayCommand)row.RemoveCommand).ExecuteAsync(null);

        var request = fx.Dialogs.Requests.Single();
        Assert.Equal("移除資料夾", request.Title);
        Assert.Equal("移除資料夾", request.ConfirmText);
        Assert.True(request.IsDestructive);
        Assert.Contains("移除後，AI 將查不到這個資料夾的內容。", request.Lines);
        Assert.Contains("你的原始檔案不會被刪除，仍然留在原本的位置。", request.Lines);
        Assert.Contains("之後重新加入，需要重新讀取這個資料夾。", request.Lines);
        Assert.DoesNotContain(fx.Calls, c => c.StartsWith("RemoveFolder", StringComparison.Ordinal));
        Assert.Single(fx.Store.Folders);
    }

    [Fact]
    public async Task Confirming_the_removal_removes_the_folder_from_the_database_and_the_list()
    {
        var fx = new FoldersFixture();
        var folder = fx.Store.Add(@"D:\Share\業務", "業務");
        await fx.Folders.RefreshAsync();
        var row = fx.Folders.Rows.Single();
        fx.Dialogs.Answers.Enqueue(true);

        await ((IAsyncRelayCommand)row.RemoveCommand).ExecuteAsync(null);

        Assert.Equal([$"RemoveFolder:{folder.Id}"], fx.Calls);
        Assert.Empty(fx.Folders.ListItems);
        Assert.True(fx.Folders.IsEmptyHintVisible);
        Assert.Contains("原始檔案", fx.Folders.NoticeText);
    }

    // ---- Large disappearance ----------------------------------------------------------------------------------

    [Fact]
    public async Task Keeping_the_data_answers_false_and_the_question_names_the_folder_and_the_count()
    {
        var fx = new FoldersFixture();
        var folder = fx.Store.Add(@"E:\外接硬碟", "外接硬碟");
        await fx.Folders.RefreshAsync();
        fx.Dialogs.Answers.Enqueue(false);

        fx.Indexing.RaiseMassDeletion(new MassDeletionPending(folder.Id, 1204, 3000));
        await WaitForAsync(() => fx.Indexing.Resolved.Count == 1);

        var request = fx.Dialogs.Requests.Single();
        Assert.Equal("『外接硬碟』裡有 1,204 個檔案不見了", request.Title);
        Assert.Equal("移除這些資料", request.ConfirmText);
        Assert.Equal("保留", request.CancelText);
        Assert.True(request.IsDestructive);
        Assert.Contains("你的原始檔案不受影響。", request.Lines);
        Assert.Equal([(folder.Id, false)], fx.Indexing.Resolved);
    }

    [Fact]
    public async Task Confirming_removes_the_data_by_answering_true()
    {
        var fx = new FoldersFixture();
        var folder = fx.Store.Add(@"E:\外接硬碟", "外接硬碟");
        await fx.Folders.RefreshAsync();
        fx.Dialogs.Answers.Enqueue(true);

        await fx.Folders.HandleMassDeletionAsync(new MassDeletionPending(folder.Id, 25, 60));

        Assert.Equal([(folder.Id, true)], fx.Indexing.Resolved);
    }

    // ---- Failed files -----------------------------------------------------------------------------------------

    private static async Task<(FoldersFixture Fixture, WatchedFolder Folder)> WithFailedFilesAsync(int count, DocumentErrorCode code = DocumentErrorCode.Locked)
    {
        var fx = new FoldersFixture();
        var folder = fx.Store.Add(@"C:\Users\chang\Documents", "文件");
        for (var i = 1; i <= count; i++)
        {
            fx.Store.AddFailed(i, folder.Id, $@"C:\Users\chang\Documents\專案資料\2025 台中案\驗收報告_v{i}.docx", code);
        }

        await fx.Folders.RefreshAsync();
        return (fx, folder);
    }

    [Fact]
    public async Task The_failed_files_card_shows_five_files_and_a_link_to_all_of_them()
    {
        var (fx, _) = await WithFailedFilesAsync(7);

        Assert.True(fx.Folders.HasFailed);
        Assert.Equal("有 7 個檔案無法讀取", fx.Folders.FailedTitle);
        Assert.Equal(5, fx.Folders.TopFailed.Count);
        Assert.Equal(7, fx.Folders.AllFailed.Count);
        Assert.True(fx.Folders.HasMoreFailed);
        Assert.Equal("查看全部 7 個 ›", fx.Folders.ShowAllFailedText);

        var first = fx.Folders.TopFailed[0];
        Assert.Equal("驗收報告_v7.docx", first.FileName);
        Assert.Equal("文件 › 專案資料 › 2025 台中案", first.Location);
        Assert.Equal("正被其他程式開啟", first.ReasonText);
    }

    [Fact]
    public async Task With_five_or_fewer_failed_files_there_is_no_link_to_all_of_them()
    {
        var (fx, _) = await WithFailedFilesAsync(5);

        Assert.Equal(5, fx.Folders.TopFailed.Count);
        Assert.False(fx.Folders.HasMoreFailed);
    }

    [Fact]
    public async Task Show_all_opens_a_dialog_with_every_file()
    {
        var (fx, _) = await WithFailedFilesAsync(7);

        await ((IAsyncRelayCommand)fx.Folders.ShowAllFailedCommand).ExecuteAsync(null);

        var dialog = Assert.IsType<FailedFilesDialogViewModel>(Assert.Single(fx.Dialogs.Shown));
        Assert.Equal(7, dialog.Rows.Count);
    }

    [Theory]
    [InlineData(DocumentErrorCode.Locked, FailedFileAction.Retry, "重試")]
    [InlineData(DocumentErrorCode.Timeout, FailedFileAction.Retry, "重試")]
    [InlineData(DocumentErrorCode.Unknown, FailedFileAction.Retry, "重試")]
    [InlineData(DocumentErrorCode.AccessDenied, FailedFileAction.Retry, "重試")]
    [InlineData(DocumentErrorCode.PasswordProtected, FailedFileAction.Skip, "略過")]
    [InlineData(DocumentErrorCode.Corrupted, FailedFileAction.Skip, "略過")]
    [InlineData(DocumentErrorCode.TooLarge, FailedFileAction.Skip, "略過")]
    [InlineData(DocumentErrorCode.Unsupported, FailedFileAction.Skip, "略過")]
    public async Task The_single_action_depends_on_the_error_code(DocumentErrorCode code, FailedFileAction action, string text)
    {
        var (fx, _) = await WithFailedFilesAsync(1, code);

        var row = fx.Folders.TopFailed.Single();

        Assert.Equal(action, row.Action);
        Assert.Equal(text, row.ActionText);
    }

    [Fact]
    public async Task Retrying_a_file_asks_the_indexer_for_that_document_only()
    {
        var (fx, _) = await WithFailedFilesAsync(2);
        var row = fx.Folders.TopFailed.Single(r => r.DocumentId == 2);

        await ((IAsyncRelayCommand)row.RunActionCommand).ExecuteAsync(null);

        Assert.Equal(["RequestRetry:2"], fx.Calls);
        Assert.Empty(fx.Store.Exclusions);
    }

    [Fact]
    public async Task Retry_all_asks_the_indexer_to_retry_everything()
    {
        var (fx, _) = await WithFailedFilesAsync(3);

        fx.Folders.RetryAllCommand.Execute(null);

        Assert.Equal(["RequestRetry:all"], fx.Calls);
    }

    [Fact]
    public async Task Skipping_a_file_adds_it_to_the_exclusion_list_and_tells_where_to_undo_it()
    {
        var (fx, _) = await WithFailedFilesAsync(2, DocumentErrorCode.PasswordProtected);
        var row = fx.Folders.TopFailed[0];

        await ((IAsyncRelayCommand)row.RunActionCommand).ExecuteAsync(null);

        Assert.Equal([(row.Path, false)], fx.Store.Exclusions);
        Assert.Contains("設定", fx.Folders.NoticeText);
        Assert.Single(fx.Folders.AllFailed);
        Assert.Empty(fx.Dialogs.Requests);
    }

    [Fact]
    public async Task Excluding_a_failed_file_from_the_context_menu_needs_a_confirmation()
    {
        var (fx, _) = await WithFailedFilesAsync(1);
        var row = fx.Folders.TopFailed.Single();

        fx.Dialogs.Answers.Enqueue(false);
        await ((IAsyncRelayCommand)row.ExcludeFromAiCommand).ExecuteAsync(null);
        Assert.Empty(fx.Store.Exclusions);

        fx.Dialogs.Answers.Enqueue(true);
        await ((IAsyncRelayCommand)row.ExcludeFromAiCommand).ExecuteAsync(null);
        Assert.Equal([(row.Path, false)], fx.Store.Exclusions);
        Assert.Equal("不要讓 AI 讀這個檔案", fx.Dialogs.Requests[0].Title);
        Assert.Empty(fx.Folders.AllFailed);
    }

    [Fact]
    public async Task Open_and_show_in_file_manager_use_the_shell_launcher()
    {
        var (fx, _) = await WithFailedFilesAsync(1);
        var row = fx.Folders.TopFailed.Single();

        row.OpenCommand.Execute(null);
        row.RevealCommand.Execute(null);

        Assert.Equal(["OpenFile:" + row.Path, "Reveal:" + row.Path], fx.Launcher.Calls);
    }

    // ---- Sub folder picker ------------------------------------------------------------------------------------

    private static async Task<(SubfolderPickerViewModel Picker, FoldersFixture Fixture, WatchedFolder Folder)> OpenPickerAsync(params string[] excluded)
    {
        var fx = new FoldersFixture();
        var folder = fx.Store.Add(@"C:\Users\chang\Documents", "文件", excluded: excluded);
        fx.Tree.Set(folder.Path, "專案資料", "會議紀錄", "機密");
        fx.Tree.Set(folder.Path + "/專案資料", "2025 台中案", "2026 新竹案");
        fx.Tree.Set(folder.Path + "/專案資料/2025 台中案", "照片");
        var picker = new SubfolderPickerViewModel(folder, fx.Tree, fx.Store, fx.Indexing);
        await picker.LoadAsync();
        return (picker, fx, folder);
    }

    private static async Task ExpandAsync(SubfolderPickerViewModel picker, SubfolderNodeViewModel node)
    {
        node.IsExpanded = true;
        await picker.LoadTask;
    }

    [Fact]
    public async Task The_picker_lists_the_first_level_and_loads_deeper_levels_when_a_node_is_expanded()
    {
        var (picker, _, _) = await OpenPickerAsync();

        Assert.Equal("選擇子資料夾 · 文件", picker.Title);
        Assert.Equal("取消勾選的資料夾，AI 不會讀取裡面的檔案。", picker.Description);
        Assert.Equal(["專案資料", "會議紀錄", "機密"], picker.Roots.Select(n => n.Name));
        Assert.All(picker.Roots, n => Assert.True(n.IsChecked));
        Assert.False(picker.IsLoading);

        var project = picker.Roots[0];
        Assert.True(project.Children.Single().IsPlaceholder);

        await ExpandAsync(picker, project);

        Assert.Equal(["2025 台中案", "2026 新竹案"], project.Children.Select(n => n.Name));
        Assert.Equal("專案資料/2025 台中案", project.Children[0].RelativePath);
    }

    [Fact]
    public async Task A_folder_with_no_children_loses_its_expander_after_loading()
    {
        var (picker, _, _) = await OpenPickerAsync();
        var meetings = picker.Roots[1];

        await ExpandAsync(picker, meetings);

        Assert.Empty(meetings.Children);
    }

    [Fact]
    public async Task Unticking_a_folder_stores_only_that_top_level_relative_path()
    {
        var (picker, fx, folder) = await OpenPickerAsync();
        await ExpandAsync(picker, picker.Roots[0]);
        await ExpandAsync(picker, picker.Roots[0].Children[0]);

        picker.Roots[0].Children[0].IsChecked = false; // 專案資料/2025 台中案 (its child 照片 follows)
        picker.Roots[2].IsChecked = false;             // 機密

        Assert.False(picker.Roots[0].Children[0].Children[0].IsChecked);
        Assert.False(picker.Roots[0].Children[0].Children[0].IsEnabled);
        Assert.Equal(["專案資料/2025 台中案", "機密"], picker.ComputeExclusions());

        await picker.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["專案資料/2025 台中案", "機密"], fx.Store.SavedExclusions[folder.Id]);
        Assert.Equal([$"SetExclusions:{folder.Id}", $"RequestRescan:{folder.Id}"], fx.Calls);
        Assert.True(picker.Saved);
    }

    [Fact]
    public async Task Unticking_a_parent_after_its_child_keeps_only_the_parent()
    {
        var (picker, _, _) = await OpenPickerAsync();
        await ExpandAsync(picker, picker.Roots[0]);

        picker.Roots[0].Children[1].IsChecked = false;
        picker.Roots[0].IsChecked = false;

        Assert.Equal(["專案資料"], picker.ComputeExclusions());
        Assert.All(picker.Roots[0].Children, c => Assert.False(c.IsChecked));
    }

    [Fact]
    public async Task Ticking_a_parent_again_ticks_its_whole_subtree()
    {
        var (picker, _, _) = await OpenPickerAsync();
        await ExpandAsync(picker, picker.Roots[0]);
        picker.Roots[0].IsChecked = false;

        picker.Roots[0].IsChecked = true;

        Assert.All(picker.Roots[0].Children, c => Assert.True(c.IsChecked && c.IsEnabled));
        Assert.Empty(picker.ComputeExclusions());
    }

    [Fact]
    public async Task Stored_exclusions_start_unticked_and_children_of_an_unticked_folder_are_unticked()
    {
        var (picker, _, _) = await OpenPickerAsync("專案資料", "機密");

        Assert.False(picker.Roots[0].IsChecked);
        Assert.True(picker.Roots[1].IsChecked);
        Assert.False(picker.Roots[2].IsChecked);

        await ExpandAsync(picker, picker.Roots[0]);
        Assert.All(picker.Roots[0].Children, c => Assert.False(c.IsChecked));
        Assert.Equal(["專案資料", "機密"], picker.ComputeExclusions());
        Assert.False(picker.HasChanges);
    }

    [Fact]
    public async Task Exclusions_deeper_than_the_opened_part_of_the_tree_are_kept()
    {
        var (picker, fx, folder) = await OpenPickerAsync("專案資料/2026 新竹案/草稿", "機密");

        picker.Roots[1].IsChecked = false; // 會議紀錄
        await picker.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["專案資料/2026 新竹案/草稿", "會議紀錄", "機密"], fx.Store.SavedExclusions[folder.Id]);
    }

    [Fact]
    public async Task Saving_without_changes_does_not_write_or_rescan()
    {
        var (picker, fx, _) = await OpenPickerAsync("機密");
        var closed = false;
        picker.CloseRequested += (_, _) => closed = true;

        await picker.SaveCommand.ExecuteAsync(null);

        Assert.Empty(fx.Calls);
        Assert.False(picker.Saved);
        Assert.True(closed);
    }

    [Fact]
    public async Task Cancel_closes_without_saving()
    {
        var (picker, fx, _) = await OpenPickerAsync();
        var closed = false;
        picker.CloseRequested += (_, _) => closed = true;
        picker.Roots[0].IsChecked = false;

        picker.CancelCommand.Execute(null);

        Assert.True(closed);
        Assert.Empty(fx.Calls);
    }

    [Fact]
    public async Task Clicking_a_folder_row_opens_the_picker_dialog_and_refreshes_after_saving()
    {
        var fx = new FoldersFixture();
        var folder = fx.Store.Add(@"C:\Users\chang\Documents", "文件");
        fx.Tree.Set(folder.Path, "機密");
        await fx.Folders.RefreshAsync();
        var row = fx.Folders.Rows.Single();

        await ((IAsyncRelayCommand)row.PickSubfoldersCommand).ExecuteAsync(null);

        var dialog = Assert.IsType<SubfolderPickerViewModel>(Assert.Single(fx.Dialogs.Shown));
        Assert.Equal("選擇子資料夾 · 文件", dialog.Title);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "Timed out waiting for the condition");
    }
}
