using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Contexo.App.Search;
using Contexo.App.Services;
using Contexo.App.Shell;
using Contexo.App.ViewModels;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Contexo.Desktop.Views.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contexo.Desktop.Tests.Search;

public sealed class SearchViewTests
{
    private sealed class StubSearch : ISearchService
    {
        public SearchResponse Response { get; set; } = new([], false);

        public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken) => Task.FromResult(Response);
    }

    /// <summary>A knowledge store that only knows its statistics; every other member throws.</summary>
    private class StorePretender : DispatchProxy
    {
        public int ChunkCount { get; set; } = 100;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IKnowledgeStore.GetStatisticsAsync))
            {
                return Task.FromResult(new StoreStatistics(1, 3, 0, ChunkCount, 0));
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private sealed class NoLauncher : IShellLauncher
    {
        public void OpenFile(string path)
        {
        }

        public void RevealInFileManager(string path)
        {
        }

        public void OpenFolder(string path)
        {
        }
    }

    private sealed class NoNavigation : INavigationService
    {
        public void NavigateTo<TViewModel>() where TViewModel : ViewModelBase
        {
        }
    }

    private sealed class YesDialogs : IDialogService
    {
        public Task<bool> ConfirmAsync(ConfirmRequest request) => Task.FromResult(true);

        public Task ShowAsync(object dialogViewModel) => Task.CompletedTask;
    }

    private static readonly string[] ExistingFiles = ["/work/2025_台中案_報價單.xlsx", "/work/2025 年度採購簡報.pptx"];

    private static SearchResponse SampleResponse(bool degraded = false)
    {
        var table = HtmlTableRenderer.Render(new TableModel(2, 4,
        [
            new TableCell(0, 0, "品名"), new TableCell(0, 1, "數量"), new TableCell(0, 2, "報價金額"), new TableCell(0, 3, "有效期限"),
            new TableCell(1, 0, "監視系統建置"), new TableCell(1, 1, "1 式"), new TableCell(1, 2, "NT$ 1,280,000"), new TableCell(1, 3, "2025/12/31"),
        ], 1));
        return new SearchResponse(
        [
            new SearchHit(1, "/work/2025_台中案_報價單.xlsx", "2025_台中案_報價單.xlsx", SectionKind.Table, table,
                new SourceLocation { Sheet = "報價明細", CellRange = "A1:F24" }, 0.86, MatchKinds.Semantic | MatchKinds.Keyword, null),
            new SearchHit(2, "/work/2025 年度採購簡報.pptx", "2025 年度採購簡報.pptx", SectionKind.Slide,
                "三家廠商比價後，以 A 公司方案最低，已於 11 月寄出正式文件給業主確認。",
                new SourceLocation { Slide = 7, Title = "廠商比價結果" }, 0.79, MatchKinds.Semantic, null),
            new SearchHit(3, "/work/業務往來信件_1125.msg", "業務往來信件_1125.msg", SectionKind.Prose,
                "附上本季報價單，若有規格調整請於下週三前回覆。",
                new SourceLocation { EmbeddedPath = ["Quotation_2025Q4.pdf"], Page = 1 }, 0.71, MatchKinds.Keyword, null),
            new SearchHit(4, "/work/客戶清單.xlsx", "客戶清單.xlsx", SectionKind.TableSummary,
                "客戶清單.xlsx 工作表「客戶」：欄位 公司、聯絡人、電話；共 5,230 列。",
                new SourceLocation { Sheet = "客戶", CellRange = "A1:C5231" }, 0.52, MatchKinds.Semantic, "t12"),
        ], degraded);
    }

    private static (SearchViewModel Vm, StubSearch Search, StorePretender Store) CreateVm()
    {
        var search = new StubSearch();
        var store = DispatchProxy.Create<IKnowledgeStore, StorePretender>();
        var vm = new SearchViewModel(
            search, store, new NoLauncher(), new YesDialogs(), new NoNavigation(), NullLogger<SearchViewModel>.Instance,
            path => ExistingFiles.Contains(path));
        return (vm, search, (StorePretender)(object)store);
    }

    private static Window Show(SearchViewModel vm)
    {
        var window = new Window { Width = 1000, Height = 760, Content = new SearchView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static async Task Settle(Func<bool> done)
    {
        for (var i = 0; i < 300 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Assert.True(done(), "The page did not reach the expected state");
    }

    [AvaloniaFact]
    public void Initial_page_shows_title_search_box_and_three_examples()
    {
        var (vm, _, _) = CreateVm();
        var window = Show(vm);

        Assert.True(Find<Border>(window, "ExamplesPanel").IsVisible);
        Assert.False(Find<Border>(window, "NoDataPanel").IsVisible);
        Assert.False(Find<Border>(window, "NoResultsPanel").IsVisible);
        Assert.False(Find<Border>(window, "DegradedBanner").IsVisible);
        Assert.Equal(3, window.GetVisualDescendants().OfType<Button>().Count(b => vm.Examples.Any(e => e.Text.Equals(b.Content))));
        ScreenshotHelper.Capture(window, "search-initial");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Results_state_shows_cards_with_highlights_tags_and_buttons()
    {
        var (vm, search, _) = CreateVm();
        search.Response = SampleResponse();
        var window = Show(vm);

        vm.Query = "報價";
        await vm.SearchAsync("報價");
        await Settle(() => window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Inlines?.OfType<Run>().Any(r => r.Background is not null) == true));

        var list = Find<ItemsControl>(window, "ResultList");
        Assert.Equal(4, list.ItemCount);
        Assert.False(Find<Border>(window, "ExamplesPanel").IsVisible);
        Assert.False(Find<Border>(window, "DegradedBanner").IsVisible);

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("相關度 0.86", texts);
        Assert.Contains("工作表「報價明細」 · A1:F24", texts);
        Assert.Contains("第 7 張投影片「廠商比價結果」", texts);
        Assert.Contains("內嵌：Quotation_2025Q4.pdf › 第 1 頁", texts);
        Assert.Contains("語意", texts);
        Assert.Contains("關鍵字", texts);
        Assert.Contains("大型表格", texts);
        Assert.Contains("檔案已移動或刪除", window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));

        var marked = window.GetVisualDescendants().OfType<TextBlock>()
            .SelectMany(t => t.Inlines?.OfType<Run>() ?? [])
            .Where(r => r.Background is not null)
            .Select(r => r.Text)
            .ToList();
        Assert.Equal(["報價", "報價"], marked.Take(2));

        var open = window.GetVisualDescendants().OfType<Button>().Where(b => "開啟原檔".Equals(b.Content)).ToList();
        Assert.Equal(4, open.Count);
        Assert.Equal([true, true, false, false], open.Select(b => b.IsEffectivelyEnabled));

        ScreenshotHelper.Capture(window, "search-results", 1000, 760);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Degraded_state_shows_the_keyword_only_notice_above_the_results()
    {
        var (vm, search, _) = CreateVm();
        search.Response = SampleResponse(degraded: true);
        var window = Show(vm);

        await vm.SearchAsync("報價");
        await Settle(() => Find<Border>(window, "DegradedBanner").IsVisible);

        Assert.Contains("目前只使用關鍵字比對（語意搜尋尚未準備好）。", Find<Border>(window, "DegradedBanner").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        ScreenshotHelper.Capture(window, "search-degraded", 1000, 760);
        window.Close();
    }

    [AvaloniaFact]
    public async Task No_results_state_shows_the_hint()
    {
        var (vm, _, _) = CreateVm();
        var window = Show(vm);

        await vm.SearchAsync("完全找不到的詞");
        await Settle(() => Find<Border>(window, "NoResultsPanel").IsVisible);

        Assert.False(Find<Border>(window, "ExamplesPanel").IsVisible);
        Assert.Contains("沒有找到相關內容。可以換個說法，或確認檔案所在的資料夾已加入。", Find<Border>(window, "NoResultsPanel").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        ScreenshotHelper.Capture(window, "search-no-results", 1000, 520);
        window.Close();
    }

    [AvaloniaFact]
    public async Task No_data_state_shows_the_hint_and_the_button_to_the_folders_page()
    {
        var (vm, _, store) = CreateVm();
        store.ChunkCount = 0;
        var window = Show(vm);

        await vm.RefreshDataStateAsync();
        await Settle(() => Find<Border>(window, "NoDataPanel").IsVisible);

        Assert.False(Find<Border>(window, "ExamplesPanel").IsVisible);
        Assert.Contains("還沒有收錄任何資料。先到『資料夾』頁面加入資料夾。", Find<Border>(window, "NoDataPanel").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.True(Find<Button>(window, "GoToFoldersButton").IsEffectivelyEnabled);
        ScreenshotHelper.Capture(window, "search-no-data", 1000, 520);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Pressing_enter_in_the_box_searches()
    {
        var (vm, search, _) = CreateVm();
        search.Response = SampleResponse();
        var window = Show(vm);

        var box = Find<TextBox>(window, "SearchBox");
        box.Focus();
        box.Text = "報價單";
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        await Settle(() => vm.Results.Count == 4);
        Assert.Equal("報價單", vm.Query);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Each_card_has_the_exclude_menu_item()
    {
        var (vm, search, _) = CreateVm();
        search.Response = SampleResponse();
        var window = Show(vm);
        await vm.SearchAsync("報價");
        Dispatcher.UIThread.RunJobs();

        var card = window.GetVisualDescendants().OfType<Border>().First(b => b.ContextMenu is not null);
        var item = Assert.IsType<MenuItem>(Assert.Single(card.ContextMenu!.Items));
        Assert.Equal("不要讓 AI 讀這個檔案", item.Header);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Dark_theme_and_large_text_still_render_the_results()
    {
        var (vm, search, _) = CreateVm();
        search.Response = SampleResponse();
        await vm.SearchAsync("報價");
        TestShell.ApplyTheme(ThemePreference.Dark);
        try
        {
            var window = Show(vm);
            Dispatcher.UIThread.RunJobs();
            ScreenshotHelper.Capture(window, "search-results-dark", 1000, 760);
            window.Close();
        }
        finally
        {
            TestShell.ApplyTheme(ThemePreference.Light);
        }
    }
}
