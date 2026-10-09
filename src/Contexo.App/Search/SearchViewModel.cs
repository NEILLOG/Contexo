using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Contexo.App.Folders;
using Contexo.App.Services;
using Contexo.App.ViewModels;
using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contexo.App.Search;

/// <summary>One of the clickable sample queries of the empty state: fills the box and searches.</summary>
public sealed record SearchExample(string Text, IAsyncRelayCommand Command);

/// <summary>
/// 「試試看搜尋」: runs the same search the AI software uses (no LLM) and lists the passages that were found.
/// The query text is never logged.
/// </summary>
public sealed partial class SearchViewModel : ViewModelBase, IPageLifecycle
{
    public const int TopK = 10;

    public const string DegradedText = "目前只使用關鍵字比對（語意搜尋尚未準備好）。";

    public const string NoResultsText = "沒有找到相關內容。可以換個說法，或確認檔案所在的資料夾已加入。";

    public const string NoDataText = "還沒有收錄任何資料。先到『資料夾』頁面加入資料夾。";

    public const string SearchFailedText = "搜尋時發生問題，請稍後再試一次。";

    public const string OpenFailedText = "無法開啟這個檔案，請確認它還在原來的位置。";

    private readonly ISearchService _search;
    private readonly IKnowledgeStore _store;
    private readonly IShellLauncher _launcher;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly ILogger<SearchViewModel> _logger;
    private readonly Func<string, bool> _fileExists;
    private CancellationTokenSource? _current;

    public SearchViewModel(
        ISearchService search,
        IKnowledgeStore store,
        IShellLauncher launcher,
        IDialogService dialogs,
        INavigationService navigation,
        ILogger<SearchViewModel> logger,
        Func<string, bool>? fileExists = null)
    {
        _search = search;
        _store = store;
        _launcher = launcher;
        _dialogs = dialogs;
        _navigation = navigation;
        _logger = logger;
        _fileExists = fileExists ?? File.Exists;
        Examples = new[] { "去年給客戶的報價單", "採購驗收標準", "會議決議" }
            .Select(text => new SearchExample(text, new AsyncRelayCommand(() => UseExampleAsync(text))))
            .ToList();
    }

    /// <summary>
    /// Creates a page that is only shown, never used: the shell and view-locator tests built the empty T15 page this way.
    /// Searching with it throws. The dependency injection container only sees the public constructor above.
    /// </summary>
    internal SearchViewModel()
        : this(null!, null!, null!, null!, null!, NullLogger<SearchViewModel>.Instance)
    {
    }

    public string Title => "試試看搜尋";

    public string Description => "輸入一句話，看看 AI 會找到哪些內容。這裡只列出找到的段落，不會產生回答。";

    /// <summary>Clickable sample queries of the empty state.</summary>
    public IReadOnlyList<SearchExample> Examples { get; }

    public ObservableCollection<SearchResultItemViewModel> Results { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSearch))]
    public partial bool IsSearching { get; private set; }

    /// <summary>True once a search has completed (or found nothing to search), until the page is reset.</summary>
    [ObservableProperty]
    public partial bool HasSearched { get; private set; }

    /// <summary>True when the database has no readable content at all.</summary>
    [ObservableProperty]
    public partial bool NoData { get; private set; }

    [ObservableProperty]
    public partial bool Degraded { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    public bool CanSearch => !IsSearching;

    public bool ShowExamples => !HasSearched && !NoData && ErrorMessage is null;

    public bool ShowNoResults => HasSearched && !NoData && ErrorMessage is null && Results.Count == 0;

    public bool ShowDegraded => HasSearched && !NoData && Degraded && ErrorMessage is null;

    public bool HasError => ErrorMessage is not null;

    public bool HasResults => Results.Count > 0;

    partial void OnHasSearchedChanged(bool value) => RaiseStateChanged();

    partial void OnNoDataChanged(bool value) => RaiseStateChanged();

    partial void OnDegradedChanged(bool value) => OnPropertyChanged(nameof(ShowDegraded));

    partial void OnErrorMessageChanged(string? value) => RaiseStateChanged();

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(ShowExamples));
        OnPropertyChanged(nameof(ShowNoResults));
        OnPropertyChanged(nameof(ShowDegraded));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>Searches for <see cref="Query"/>. An empty query does nothing; a new search cancels the previous one.</summary>
    [RelayCommand]
    private Task Search() => SearchAsync(Query);

    private Task UseExampleAsync(string example)
    {
        Query = example;
        return SearchAsync(example);
    }

    [RelayCommand]
    private void GoToFolders() => _navigation.NavigateTo<FoldersViewModel>();

    /// <summary>Runs one search. Public so tests (and the example links) can await it.</summary>
    public async Task SearchAsync(string? query)
    {
        var text = query?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var previous = Interlocked.Exchange(ref _current, null);
        previous?.Cancel();
        var source = new CancellationTokenSource();
        _current = source;
        var token = source.Token;

        IsSearching = true;
        ErrorMessage = null;
        var timer = Stopwatch.StartNew();
        try
        {
            var stats = await _store.GetStatisticsAsync(token);
            token.ThrowIfCancellationRequested();
            if (stats.ChunkCount == 0)
            {
                Results.Clear();
                Degraded = false;
                NoData = true;
                HasSearched = true;
                return;
            }

            NoData = false;
            var response = await _search.SearchAsync(new SearchRequest(text, TopK), token);
            token.ThrowIfCancellationRequested();

            var terms = QueryHighlighter.ExtractTerms(text);
            var exists = await Task.Run(() => response.Hits.Select(h => SafeExists(h.FilePath)).ToArray(), token);
            token.ThrowIfCancellationRequested();

            Results.Clear();
            for (var i = 0; i < response.Hits.Count; i++)
            {
                Results.Add(new SearchResultItemViewModel(response.Hits[i], terms, exists[i], OpenFile, RevealFile, ExcludeAsync));
            }

            Degraded = response.Degraded;
            HasSearched = true;
            RaiseStateChanged();
            _logger.LogInformation("Search finished: {Count} hits in {Elapsed} ms, degraded={Degraded}", Results.Count, timer.ElapsedMilliseconds, response.Degraded);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer search replaced this one; it owns the state now.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Search failed ({ErrorType})", ex.GetType().Name);
            Results.Clear();
            ErrorMessage = SearchFailedText;
            HasSearched = true;
        }
        finally
        {
            if (ReferenceEquals(_current, source))
            {
                _current = null;
                IsSearching = false;
            }

            source.Dispose();
        }
    }

    private bool SafeExists(string path)
    {
        try
        {
            return _fileExists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OpenFile(SearchResultItemViewModel item) => Launch(() => _launcher.OpenFile(item.FilePath), "open");

    private void RevealFile(SearchResultItemViewModel item) => Launch(() => _launcher.RevealInFileManager(item.FilePath), "reveal");

    private void Launch(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not {Action} a file ({ErrorType})", what, ex.GetType().Name);
            ErrorMessage = OpenFailedText;
        }
    }

    /// <summary>Right-click 「不要讓 AI 讀這個檔案」: asks first, then excludes the file and drops its results.</summary>
    private async Task ExcludeAsync(SearchResultItemViewModel item)
    {
        var confirmed = await _dialogs.ConfirmAsync(new ConfirmRequest(
            "不要讓 AI 讀這個檔案？",
            [
                $"「{item.FileName}」的內容會從 Contexo 移除，AI 之後就查不到它。",
                "原本的檔案不會被刪除或更動。",
            ],
            "不要讓 AI 讀取"));
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _store.AddExclusionAsync(item.FilePath, false, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not exclude a file ({ErrorType})", ex.GetType().Name);
            ErrorMessage = "沒辦法設定，請稍後再試一次。";
            return;
        }

        foreach (var hit in Results.Where(r => string.Equals(r.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            Results.Remove(hit);
        }

        RaiseStateChanged();
    }

    /// <summary>Checks whether anything has been read yet, so the empty-database hint shows before the first search.</summary>
    public async Task RefreshDataStateAsync()
    {
        try
        {
            var stats = await _store.GetStatisticsAsync(CancellationToken.None);
            NoData = stats.ChunkCount == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read statistics ({ErrorType})", ex.GetType().Name);
        }
    }

    public void OnNavigatedTo() => _ = RefreshDataStateAsync();

    public void OnNavigatedFrom()
    {
    }
}
