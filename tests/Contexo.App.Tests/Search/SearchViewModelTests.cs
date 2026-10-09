using Contexo.App.Folders;
using Contexo.App.Search;
using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contexo.App.Tests.Search;

public sealed class SearchViewModelTests
{
    private readonly FakeSearchService _search = new();
    private readonly FakeKnowledgeStore _store = new();
    private readonly FakeShellLauncher _launcher = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeNavigation _navigation = new();
    private readonly HashSet<string> _existing = new(StringComparer.Ordinal) { "/docs/a.docx", "/docs/b.xlsx", "/docs/c.pdf" };

    private SearchViewModel Create() =>
        new(_search, _store, _launcher, _dialogs, _navigation, NullLogger<SearchViewModel>.Instance, _existing.Contains);

    private static SearchHit Hit(string path, double score = 0.9, string text = "報價單內容", MatchKinds by = MatchKinds.Keyword | MatchKinds.Semantic,
        SectionKind kind = SectionKind.Prose, SourceLocation? location = null, long id = 1, string? tableId = null) =>
        new(id, path, Path.GetFileName(path), kind, text, location ?? SourceLocation.None, score, by, tableId);

    [Fact]
    public async Task Empty_or_blank_query_does_not_search()
    {
        var vm = Create();

        await vm.SearchAsync(null);
        await vm.SearchAsync("");
        await vm.SearchAsync("   ");
        vm.Query = "  ";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Empty(_search.Requests);
        Assert.False(vm.HasSearched);
        Assert.True(vm.ShowExamples);
    }

    [Fact]
    public async Task Search_asks_for_ten_results_with_the_trimmed_query()
    {
        _search.Response = new SearchResponse([Hit("/docs/a.docx")], false);
        var vm = Create();

        await vm.SearchAsync("  報價單  ");

        var request = Assert.Single(_search.Requests);
        Assert.Equal("報價單", request.Query);
        Assert.Equal(10, request.TopK);
        Assert.Null(request.PathPrefixes);
        Assert.Single(vm.Results);
        Assert.True(vm.HasSearched);
        Assert.False(vm.ShowExamples);
    }

    [Fact]
    public async Task Searching_state_is_on_while_waiting_and_off_afterwards()
    {
        _search.Manual = true;
        var vm = Create();
        Assert.True(vm.CanSearch);

        var task = vm.SearchAsync("報價單");
        await _search.WaitForCallsAsync(1);

        Assert.True(vm.IsSearching);
        Assert.False(vm.CanSearch);

        _search.Complete(0, new SearchResponse([Hit("/docs/a.docx")], false));
        await task;

        Assert.False(vm.IsSearching);
        Assert.True(vm.CanSearch);
        Assert.Single(vm.Results);
    }

    [Fact]
    public async Task A_new_search_cancels_the_previous_one_and_only_the_new_results_show()
    {
        _search.Manual = true;
        var vm = Create();

        var first = vm.SearchAsync("第一個");
        await _search.WaitForCallsAsync(1);
        var second = vm.SearchAsync("第二個");
        await _search.WaitForCallsAsync(2);

        Assert.True(_search.Tokens[0].IsCancellationRequested);
        Assert.False(_search.Tokens[1].IsCancellationRequested);
        await first;
        Assert.True(vm.IsSearching);
        Assert.Empty(vm.Results);

        _search.Complete(1, new SearchResponse([Hit("/docs/b.xlsx")], false));
        await second;

        Assert.False(vm.IsSearching);
        Assert.Equal("b.xlsx", Assert.Single(vm.Results).FileName);
    }

    [Fact]
    public async Task Degraded_response_shows_the_keyword_only_notice()
    {
        _search.Response = new SearchResponse([Hit("/docs/a.docx", by: MatchKinds.Keyword)], true);
        var vm = Create();

        await vm.SearchAsync("報價單");

        Assert.True(vm.Degraded);
        Assert.True(vm.ShowDegraded);
        Assert.Equal("目前只使用關鍵字比對（語意搜尋尚未準備好）。", SearchViewModel.DegradedText);
        Assert.True(vm.HasResults);
        Assert.False(vm.ShowNoResults);
    }

    [Fact]
    public async Task Normal_response_does_not_show_the_degraded_notice()
    {
        _search.Response = new SearchResponse([Hit("/docs/a.docx")], false);
        var vm = Create();

        await vm.SearchAsync("報價單");

        Assert.False(vm.ShowDegraded);
    }

    [Fact]
    public async Task No_results_shows_the_no_results_notice()
    {
        var vm = Create();

        await vm.SearchAsync("不存在的東西");

        Assert.True(vm.ShowNoResults);
        Assert.False(vm.NoData);
        Assert.False(vm.ShowExamples);
        Assert.Equal("沒有找到相關內容。可以換個說法，或確認檔案所在的資料夾已加入。", SearchViewModel.NoResultsText);
    }

    [Fact]
    public async Task Empty_database_shows_the_no_data_notice_without_searching()
    {
        _store.ChunkCount = 0;
        var vm = Create();

        await vm.SearchAsync("報價單");

        Assert.True(vm.NoData);
        Assert.False(vm.ShowNoResults);
        Assert.False(vm.ShowExamples);
        Assert.Empty(_search.Requests);
        Assert.False(vm.IsSearching);
        Assert.Equal("還沒有收錄任何資料。先到『資料夾』頁面加入資料夾。", SearchViewModel.NoDataText);
    }

    [Fact]
    public async Task Opening_the_page_with_an_empty_database_shows_the_no_data_notice_and_it_clears_later()
    {
        _store.ChunkCount = 0;
        var vm = Create();
        Assert.True(vm.ShowExamples);

        vm.OnNavigatedTo();
        await vm.RefreshDataStateAsync();
        Assert.True(vm.NoData);
        Assert.False(vm.ShowExamples);

        _store.ChunkCount = 3;
        await vm.RefreshDataStateAsync();
        Assert.False(vm.NoData);
        Assert.True(vm.ShowExamples);
    }

    [Fact]
    public void Go_to_folders_navigates_to_the_folders_page()
    {
        var vm = Create();

        vm.GoToFoldersCommand.Execute(null);

        Assert.Equal([typeof(FoldersViewModel)], _navigation.Visited);
    }

    [Fact]
    public async Task Example_fills_the_box_and_searches()
    {
        _search.Response = new SearchResponse([Hit("/docs/a.docx")], false);
        var vm = Create();

        Assert.Equal(3, vm.Examples.Count);
        await vm.Examples[1].Command.ExecuteAsync(null);

        Assert.Equal(vm.Examples[1].Text, vm.Query);
        Assert.Equal(vm.Examples[1].Text, Assert.Single(_search.Requests).Query);
        Assert.Single(vm.Results);
    }

    [Fact]
    public async Task Failure_in_the_search_service_shows_a_message_and_does_not_throw()
    {
        _search.Failure = new InvalidOperationException("boom with 秘密查詢");
        var vm = Create();

        await vm.SearchAsync("報價單");

        Assert.True(vm.HasError);
        Assert.Equal(SearchViewModel.SearchFailedText, vm.ErrorMessage);
        Assert.False(vm.IsSearching);
        Assert.False(vm.ShowNoResults);
    }

    [Fact]
    public async Task Result_cards_carry_score_location_tags_and_file_state()
    {
        _search.Response = new SearchResponse(
        [
            Hit("/docs/b.xlsx", 0.856, "<table><tr><td>x</td></tr></table>", MatchKinds.Semantic, SectionKind.TableSummary,
                new SourceLocation { Sheet = "報價明細", CellRange = "A1:F24" }, 2, "t7"),
            Hit("/docs/gone.docx", 0.5, "報價單", MatchKinds.Keyword, id: 3),
        ], false);
        var vm = Create();

        await vm.SearchAsync("報價單");

        var table = vm.Results[0];
        Assert.Equal("相關度 0.86", table.ScoreText);
        Assert.Equal("工作表「報價明細」 · A1:F24", table.LocationText);
        Assert.True(table.IsSemanticMatch);
        Assert.False(table.IsKeywordMatch);
        Assert.True(table.IsLargeTable);
        Assert.True(table.FileExists);

        var gone = vm.Results[1];
        Assert.True(gone.FileMissing);
        Assert.False(gone.OpenCommand.CanExecute(null));
        Assert.False(gone.RevealCommand.CanExecute(null));
        Assert.True(gone.IsKeywordMatch);
        Assert.False(gone.IsSemanticMatch);
        Assert.False(gone.IsLargeTable);
        Assert.Equal("檔案已移動或刪除", gone.MissingText);
        Assert.Contains(gone.Fragments, f => f.Highlight && f.Text == "報價單");
    }

    [Fact]
    public async Task Open_and_reveal_use_the_shell_launcher()
    {
        _search.Response = new SearchResponse([Hit("/docs/a.docx")], false);
        var vm = Create();
        await vm.SearchAsync("報價單");

        vm.Results[0].OpenCommand.Execute(null);
        vm.Results[0].RevealCommand.Execute(null);

        Assert.Equal(["/docs/a.docx"], _launcher.Opened);
        Assert.Equal(["/docs/a.docx"], _launcher.Revealed);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Open_failure_is_shown_as_a_message()
    {
        _search.Response = new SearchResponse([Hit("/docs/a.docx")], false);
        _launcher.Throw = true;
        var vm = Create();
        await vm.SearchAsync("報價單");

        vm.Results[0].OpenCommand.Execute(null);

        Assert.Equal(SearchViewModel.OpenFailedText, vm.ErrorMessage);
        Assert.Single(vm.Results);
    }

    [Fact]
    public async Task Excluding_a_file_asks_first_then_removes_all_its_results()
    {
        _search.Response = new SearchResponse(
        [
            Hit("/docs/a.docx", id: 1),
            Hit("/docs/b.xlsx", id: 2),
            Hit("/docs/a.docx", id: 3),
        ], false);
        var vm = Create();
        await vm.SearchAsync("報價單");

        await vm.Results[0].ExcludeCommand.ExecuteAsync(null);

        var request = Assert.Single(_dialogs.Requests);
        Assert.Contains(request.Lines, l => l.Contains("a.docx"));
        Assert.Contains(request.Lines, l => l.Contains("不會被刪除"));
        Assert.Equal([("/docs/a.docx", false)], _store.Exclusions);
        Assert.Equal("b.xlsx", Assert.Single(vm.Results).FileName);
    }

    [Fact]
    public async Task Cancelling_the_exclusion_keeps_everything()
    {
        _search.Response = new SearchResponse([Hit("/docs/a.docx")], false);
        _dialogs.Answer = false;
        var vm = Create();
        await vm.SearchAsync("報價單");

        await vm.Results[0].ExcludeCommand.ExecuteAsync(null);

        Assert.Empty(_store.Exclusions);
        Assert.Single(vm.Results);
    }

    [Fact]
    public async Task Failed_exclusion_keeps_the_results_and_shows_a_message()
    {
        _search.Response = new SearchResponse([Hit("/docs/a.docx")], false);
        _store.ExclusionFailure = new IOException("locked");
        var vm = Create();
        await vm.SearchAsync("報價單");

        await vm.Results[0].ExcludeCommand.ExecuteAsync(null);

        Assert.Single(vm.Results);
        Assert.True(vm.HasError);
    }

    [Fact]
    public async Task Excluding_the_last_result_turns_into_the_no_results_notice()
    {
        _search.Response = new SearchResponse([Hit("/docs/a.docx")], false);
        var vm = Create();
        await vm.SearchAsync("報價單");

        await vm.Results[0].ExcludeCommand.ExecuteAsync(null);

        Assert.Empty(vm.Results);
        Assert.True(vm.ShowNoResults);
    }
}
