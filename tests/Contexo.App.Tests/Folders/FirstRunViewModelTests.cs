using CommunityToolkit.Mvvm.Input;
using Contexo.App.Folders;
using Contexo.App.Tests.Shell;
using Contexo.Core.Abstractions;

namespace Contexo.App.Tests.Folders;

public sealed class FirstRunViewModelTests
{
    private const string Documents = @"C:\Users\chang\Documents";
    private const string OneDrive = @"C:\Users\chang\OneDrive - 公司共用";
    private const string Desktop = @"C:\Users\chang\Desktop";
    private const string Downloads = @"C:\Users\chang\Downloads";

    private sealed class Wizard
    {
        public Wizard(bool withClients = false)
        {
            Store = new FolderTestStore();
            Calls = Store.Calls;
            Settings = new FolderTestSettings(Calls);
            Indexing = new FolderTestIndexing(Calls);
            Clients = new FolderTestClients();
            Picker = new FolderTestPicker();
            Counter = new FolderTestCounter();
            Tree = new FolderTestTree();
            Counter.Counts[PathRelations.Normalize(Documents)] = new FileCount(2100, false);
            Counter.Counts[PathRelations.Normalize(OneDrive)] = new FileCount(1300, false);
            Counter.Counts[PathRelations.Normalize(Desktop)] = new FileCount(40, false);
            Counter.Counts[PathRelations.Normalize(Downloads)] = new FileCount(860, false);
            if (withClients)
            {
                Clients.Statuses.Add(new AiClientStatus("claude-desktop", "Claude Desktop", ClientConnectionState.NotAdded, null, null, null));
                Clients.Statuses.Add(new AiClientStatus("cursor", "Cursor", ClientConnectionState.NeedsRepair, null, null, null));
                Clients.Statuses.Add(new AiClientStatus("vscode", "VS Code", ClientConnectionState.NotInstalled, null, null, null));
                Clients.Statuses.Add(new AiClientStatus("lm-studio", "LM Studio", ClientConnectionState.Connected, null, null, null));
            }

            ViewModel = new FirstRunViewModel(
                Store,
                Settings,
                Indexing,
                Clients,
                Picker,
                new InlineDispatcher(),
                null,
                new FolderTestKnownFolders(
                    new KnownFolder("documents", "文件", Documents, true),
                    new KnownFolder("desktop", "桌面", Desktop, false),
                    new KnownFolder("onedrive", "OneDrive - 公司共用", OneDrive, true),
                    new KnownFolder("downloads", "下載", Downloads, false)),
                Counter,
                Tree);
        }

        public List<string> Calls { get; }

        public FolderTestStore Store { get; }

        public FolderTestSettings Settings { get; }

        public FolderTestIndexing Indexing { get; }

        public FolderTestClients Clients { get; }

        public FolderTestPicker Picker { get; }

        public FolderTestCounter Counter { get; }

        public FolderTestTree Tree { get; }

        public FirstRunViewModel ViewModel { get; }
    }

    private static async Task<Wizard> StartAsync(bool withClients = false)
    {
        var wizard = new Wizard(withClients);
        await wizard.ViewModel.InitializeAsync();
        await wizard.ViewModel.CountingTask;
        return wizard;
    }

    [Fact]
    public async Task Common_locations_are_listed_with_downloads_unticked_and_the_others_as_described()
    {
        var wizard = await StartAsync();

        var locations = wizard.ViewModel.Locations;

        Assert.Equal(["文件", "桌面", "OneDrive - 公司共用", "下載"], locations.Select(l => l.Name));
        Assert.Equal([true, false, true, false], locations.Select(l => l.IsChecked));
        Assert.False(locations.Single(l => l.Name == "下載").IsChecked);
        Assert.Equal(["約 2,100 個檔案", "約 40 個檔案", "約 1,300 個檔案", "約 860 個檔案"], locations.Select(l => l.CountText));
    }

    [Fact]
    public async Task A_folder_with_too_many_files_shows_the_limit()
    {
        var wizard = new Wizard();
        wizard.Counter.Counts[PathRelations.Normalize(Documents)] = new FileCount(FirstRunViewModel.CountLimit, true);

        await wizard.ViewModel.InitializeAsync();
        await wizard.ViewModel.CountingTask;

        Assert.Equal("超過 10,000 個檔案", wizard.ViewModel.Locations[0].CountText);
        Assert.StartsWith("預估第一次建立需要至少", wizard.ViewModel.EstimateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task While_counting_the_row_says_so_and_a_failed_count_is_left_blank()
    {
        var wizard = new Wizard();
        wizard.Counter.Counts.Remove(PathRelations.Normalize(Desktop));
        var gate = new TaskCompletionSource<FileCount>();
        var slow = new DelayedCounter(gate.Task);
        var model = new FirstRunViewModel(
            wizard.Store, wizard.Settings, wizard.Indexing, wizard.Clients, wizard.Picker, new InlineDispatcher(), null,
            new FolderTestKnownFolders(new KnownFolder("documents", "文件", Documents, true)), slow, wizard.Tree);

        await model.InitializeAsync();
        Assert.Equal("計算中…", model.Locations[0].CountText);
        Assert.StartsWith("正在計算", model.EstimateText, StringComparison.Ordinal);

        gate.SetResult(new FileCount(10, false));
        await model.CountingTask;
        Assert.Equal("約 10 個檔案", model.Locations[0].CountText);

        await wizard.ViewModel.InitializeAsync();
        await wizard.ViewModel.CountingTask;
        Assert.Equal("", wizard.ViewModel.Locations.Single(l => l.Name == "桌面").CountText);
    }

    private sealed class DelayedCounter(Task<FileCount> result) : IFolderFileCounter
    {
        public Task<FileCount> CountAsync(string path, IReadOnlySet<string> extensions, int limit, CancellationToken cancellationToken) => result;
    }

    [Fact]
    public async Task The_estimate_is_one_second_per_file_for_the_ticked_folders_only()
    {
        var wizard = await StartAsync();

        // 文件 2,100 + OneDrive 1,300 = 3,400 files = 56.7 minutes
        Assert.Equal("預估第一次建立需要約 55 分鐘，期間可以照常使用電腦。", wizard.ViewModel.EstimateText);

        wizard.ViewModel.Locations.Single(l => l.Name == "下載").IsChecked = true;
        // + 860 = 4,260 files = 1.18 hours
        Assert.Equal("預估第一次建立需要約 1～2 小時，期間可以照常使用電腦。", wizard.ViewModel.EstimateText);

        foreach (var location in wizard.ViewModel.Locations)
        {
            location.IsChecked = false;
        }

        Assert.Contains("還沒有選資料夾也沒關係", wizard.ViewModel.EstimateText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10, "不到 1 分鐘")]
    [InlineData(59, "不到 1 分鐘")]
    [InlineData(60, "約 1 分鐘")]
    [InlineData(300, "約 5 分鐘")]
    [InlineData(600, "約 10 分鐘")]
    [InlineData(1000, "約 15 分鐘")]
    [InlineData(3400, "約 55 分鐘")]
    [InlineData(3599, "約 1 小時")]
    [InlineData(3600, "約 1 小時")]
    [InlineData(4260, "約 1～2 小時")]
    [InlineData(7200, "約 2 小時")]
    [InlineData(9000, "約 2～3 小時")]
    [InlineData(40000, "約 11～12 小時")]
    [InlineData(172800, "約 2 天")]
    public void First_build_estimate_gives_a_range_in_plain_words(int files, string expected)
    {
        Assert.Equal(expected, TimeText.FirstBuildEstimate(files));
    }

    [Theory]
    [InlineData(20, "不到 1 分鐘")]
    [InlineData(42 * 60, "42 分鐘")]
    [InlineData(130 * 60, "2 小時 10 分鐘")]
    [InlineData(120 * 60, "2 小時")]
    public void Remaining_time_is_in_minutes_or_hours(int seconds, string expected)
    {
        Assert.Equal(expected, TimeText.Remaining(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public async Task Other_folders_can_be_picked_dropped_or_ticked_again()
    {
        var wizard = await StartAsync();
        wizard.Counter.Counts[PathRelations.Normalize(@"D:\Share\業務")] = new FileCount(5, false);
        wizard.Picker.Result = @"D:\Share\業務";

        await ((IAsyncRelayCommand)wizard.ViewModel.PickOtherCommand).ExecuteAsync(null);
        await wizard.ViewModel.CountingTask;

        var added = wizard.ViewModel.Locations[^1];
        Assert.Equal("業務", added.Name);
        Assert.True(added.IsChecked);
        Assert.Equal("約 5 個檔案", added.CountText);

        // A listed folder is only ticked, not listed twice.
        wizard.ViewModel.AddCustomFolder(Downloads + @"\");
        Assert.Equal(5, wizard.ViewModel.Locations.Count);
        Assert.True(wizard.ViewModel.Locations.Single(l => l.Name == "下載").IsChecked);

        wizard.Tree.Missing.Add(PathRelations.Normalize(@"D:\檔案.docx"));
        wizard.ViewModel.AddDroppedFolders([@"D:\檔案.docx"]);
        Assert.Equal(5, wizard.ViewModel.Locations.Count);
    }

    [Fact]
    public async Task The_steps_move_forward_and_back_and_the_indicator_follows()
    {
        var wizard = await StartAsync();
        var model = wizard.ViewModel;

        Assert.Equal(["1 選資料夾", "2 檔案類型", "3 加入 AI 軟體"], model.Steps.Select(s => s.Label));
        Assert.True(model.IsStep1);
        Assert.False(model.CanGoBack);
        Assert.Equal("下一步", model.NextButtonText);

        await ((IAsyncRelayCommand)model.NextCommand).ExecuteAsync(null);
        Assert.True(model.IsStep2);
        Assert.True(model.Steps[1].IsCurrent);
        Assert.False(model.Steps[0].IsCurrent);
        Assert.True(model.CanGoBack);

        await ((IAsyncRelayCommand)model.NextCommand).ExecuteAsync(null);
        Assert.True(model.IsStep3);
        Assert.Equal("完成", model.NextButtonText);

        model.BackCommand.Execute(null);
        Assert.True(model.IsStep2);
    }

    [Fact]
    public async Task File_types_default_to_the_four_supported_ones_and_pictures_are_disabled()
    {
        var wizard = await StartAsync();

        var categories = wizard.ViewModel.Categories;

        Assert.Equal(["文件", "簡報", "試算表", "PDF", "圖片"], categories.Select(c => c.Name));
        Assert.Equal([true, true, true, true, false], categories.Select(c => c.IsChecked));
        var pictures = categories.Single(c => c.Category == FileCategory.Images);
        Assert.False(pictures.IsEnabled);
        Assert.Contains("處理時間較長", pictures.Description, StringComparison.Ordinal);
        Assert.Contains("之後的版本提供", pictures.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Next_is_blocked_on_step_two_when_no_file_type_is_ticked()
    {
        var wizard = await StartAsync();
        await ((IAsyncRelayCommand)wizard.ViewModel.NextCommand).ExecuteAsync(null);

        foreach (var category in wizard.ViewModel.Categories)
        {
            category.IsChecked = false;
        }

        Assert.False(wizard.ViewModel.CanGoNext);
        await ((IAsyncRelayCommand)wizard.ViewModel.NextCommand).ExecuteAsync(null);
        Assert.True(wizard.ViewModel.IsStep2);
    }

    [Fact]
    public async Task Step_three_lists_installed_programs_and_adding_asks_to_restart_them()
    {
        var wizard = await StartAsync(withClients: true);
        var model = wizard.ViewModel;
        await ((IAsyncRelayCommand)model.NextCommand).ExecuteAsync(null);
        await ((IAsyncRelayCommand)model.NextCommand).ExecuteAsync(null);

        Assert.Equal(["Claude Desktop", "Cursor", "LM Studio"], model.Clients.Select(c => c.Name));
        Assert.True(model.HasClients);
        Assert.Equal(["加入", "修復", "加入"], model.Clients.Select(c => c.ActionText));
        Assert.Equal([true, true, false], model.Clients.Select(c => c.CanAdd));

        var claude = model.Clients[0];
        await ((IAsyncRelayCommand)claude.AddCommand).ExecuteAsync(null);

        Assert.Equal(["claude-desktop"], wizard.Clients.Added);
        Assert.False(claude.CanAdd);
        Assert.Equal("請完全關閉 Claude Desktop 後重新開啟", claude.Message);
    }

    [Fact]
    public async Task A_failed_add_shows_a_friendly_message_and_keeps_the_button()
    {
        var wizard = await StartAsync(withClients: true);
        wizard.Clients.FailAdd = true;
        await wizard.ViewModel.LoadClientsAsync();

        var first = wizard.ViewModel.Clients[0];
        await ((IAsyncRelayCommand)first.AddCommand).ExecuteAsync(null);

        Assert.True(first.CanAdd);
        Assert.Contains("沒有加入成功", first.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_any_installed_program_step_three_says_so_and_can_be_skipped()
    {
        var wizard = await StartAsync(withClients: false);
        var model = wizard.ViewModel;
        await ((IAsyncRelayCommand)model.NextCommand).ExecuteAsync(null);
        await ((IAsyncRelayCommand)model.NextCommand).ExecuteAsync(null);

        Assert.False(model.HasClients);
        Assert.True(model.HasNoClients);
        Assert.Equal("目前沒有偵測到支援的 AI 軟體，之後可以在「AI 軟體」頁面加入。", model.NoClientsText);

        await ((IAsyncRelayCommand)model.NextCommand).ExecuteAsync(null);
        Assert.True(wizard.Settings.Current.FirstRunCompleted);
    }

    [Fact]
    public async Task Finishing_adds_folders_then_saves_settings_then_asks_for_a_full_scan()
    {
        var wizard = await StartAsync();
        var model = wizard.ViewModel;
        model.Categories.Single(c => c.Category == FileCategory.Pdf).IsChecked = false;

        await model.FinishAsync();

        // Ticked by default: documents and OneDrive. Desktop and downloads stay out.
        Assert.Equal(
            [$"AddFolder:{Documents}", $"AddFolder:{OneDrive}", "SaveSettings", "RequestRescan:all"],
            wizard.Calls);
        var settings = wizard.Settings.Current;
        Assert.True(settings.FirstRunCompleted);
        Assert.Equal([FileCategory.Documents, FileCategory.Presentations, FileCategory.Spreadsheets, FileCategory.Email], settings.EnabledCategories);
        Assert.Equal(["Documents", "OneDrive - 公司共用"], wizard.Store.Folders.Select(f => f.DisplayName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_ticked_folder_inside_another_ticked_folder_is_not_added_separately()
    {
        var wizard = await StartAsync();
        wizard.Counter.Counts[PathRelations.Normalize(OneDrive + @"\文件")] = new FileCount(10, false);
        wizard.ViewModel.AddCustomFolder(OneDrive + @"\文件");

        await wizard.ViewModel.FinishAsync();

        Assert.DoesNotContain(wizard.Calls, c => c.EndsWith(@"\文件", StringComparison.Ordinal));
        Assert.Equal(2, wizard.Calls.Count(c => c.StartsWith("AddFolder", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_folder_that_cannot_be_added_keeps_the_wizard_open_with_a_message()
    {
        var wizard = await StartAsync();
        wizard.Tree.Missing.Add(PathRelations.Normalize(OneDrive));

        await wizard.ViewModel.FinishAsync();

        Assert.False(wizard.Settings.Current.FirstRunCompleted);
        Assert.DoesNotContain("SaveSettings", wizard.Calls);
        Assert.True(wizard.ViewModel.HasError);
        Assert.Contains("OneDrive - 公司共用", wizard.ViewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.False(wizard.ViewModel.IsBusy);
    }

    [Fact]
    public async Task Finishing_with_no_folder_still_completes_the_first_run()
    {
        var wizard = await StartAsync();
        foreach (var location in wizard.ViewModel.Locations)
        {
            location.IsChecked = false;
        }

        await wizard.ViewModel.FinishAsync();

        Assert.Equal(["SaveSettings", "RequestRescan:all"], wizard.Calls);
        Assert.True(wizard.Settings.Current.FirstRunCompleted);
    }
}
