using Contexo.App.About;
using Contexo.App.AiClients;
using Contexo.App.Folders;
using Contexo.App.Search;
using Contexo.App.Services;
using Contexo.App.Settings;
using Contexo.App.Shell;
using Contexo.App.ViewModels;
using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contexo.App.Tests.Shell;

public sealed class ShellViewModelTests
{
    private readonly FakeSettingsStore _settings = new();
    private readonly NavigationService _navigation = new();

    private ShellViewModel Create(bool firstRunCompleted = true)
    {
        if (firstRunCompleted)
        {
            _settings.SaveAsync(new AppSettings { FirstRunCompleted = true }, CancellationToken.None).GetAwaiter().GetResult();
        }

        var status = new StatusBarViewModel(
            new FakeIndexingService(),
            new FakeAiClientStatusService(),
            new InlineDispatcher(),
            new ManualTimeProvider(),
            NullLogger<StatusBarViewModel>.Instance);

        return new ShellViewModel(
            new FoldersViewModel(),
            new FirstRunViewModel(),
            new SearchViewModel(),
            new AiClientsViewModel(),
            new SettingsViewModel(),
            new AboutViewModel(),
            status,
            _navigation,
            new DialogHostViewModel(new InlineDispatcher()),
            _settings,
            new InlineDispatcher());
    }

    [Fact]
    public void Lists_the_five_pages_in_order_and_opens_folders()
    {
        using var shell = Create();

        Assert.Equal(["資料夾", "試試看搜尋", "AI 軟體", "設定", "關於與問題回報"], shell.NavigationItems.Select(i => i.Title));
        Assert.IsType<FoldersViewModel>(shell.CurrentPage);
        Assert.Same(shell.NavigationItems[0], shell.SelectedItem);
        Assert.True(shell.IsNavigationVisible);
    }

    [Fact]
    public void Selecting_an_item_switches_the_page()
    {
        using var shell = Create();

        shell.NavigateTo(shell.NavigationItems[3]);

        Assert.IsType<SettingsViewModel>(shell.CurrentPage);
    }

    [Fact]
    public void Shows_the_wizard_on_first_run_and_hides_navigation()
    {
        using var shell = Create(firstRunCompleted: false);

        Assert.True(shell.IsFirstRun);
        Assert.IsType<FirstRunViewModel>(shell.CurrentPage);
        Assert.False(shell.IsNavigationVisible);
    }

    [Fact]
    public async Task Leaves_the_wizard_when_first_run_is_completed()
    {
        using var shell = Create(firstRunCompleted: false);

        await _settings.SaveAsync(new AppSettings { FirstRunCompleted = true }, CancellationToken.None);

        Assert.False(shell.IsFirstRun);
        Assert.IsType<FoldersViewModel>(shell.CurrentPage);
        Assert.True(shell.IsNavigationVisible);
    }

    [Fact]
    public void Navigation_service_switches_pages_by_view_model_type()
    {
        using var shell = Create();

        _navigation.NavigateTo<AboutViewModel>();

        Assert.IsType<AboutViewModel>(shell.CurrentPage);
        Assert.Same(shell.NavigationItems[4], shell.SelectedItem);
    }

    [Fact]
    public void Navigation_requests_are_ignored_during_first_run()
    {
        using var shell = Create(firstRunCompleted: false);

        _navigation.NavigateTo<AboutViewModel>();

        Assert.IsType<FirstRunViewModel>(shell.CurrentPage);
    }

    [Fact]
    public void Startup_error_replaces_every_page()
    {
        using var shell = Create();

        shell.ShowStartupError(new StartupErrorViewModel());
        _navigation.NavigateTo<AboutViewModel>();

        Assert.IsType<StartupErrorViewModel>(shell.CurrentPage);
        Assert.False(shell.IsNavigationVisible);
    }

    private sealed class LifecyclePage : ViewModelBase, IPageLifecycle
    {
        public List<string> Calls { get; } = [];

        public void OnNavigatedTo() => Calls.Add("to");

        public void OnNavigatedFrom() => Calls.Add("from");
    }

    [Fact]
    public void Lifecycle_follows_navigation_and_window_visibility()
    {
        var first = new LifecyclePage();
        var second = new LifecyclePage();
        using var shell = Create();
        shell.SetCurrentPage(first);

        shell.OnWindowShown();
        shell.SetCurrentPage(second);
        shell.OnWindowHidden();
        shell.OnWindowHidden(); // idempotent
        shell.OnWindowShown();

        Assert.Equal(["to", "from"], first.Calls);
        Assert.Equal(["to", "from", "to"], second.Calls);
    }

    [Fact]
    public void Lifecycle_is_not_called_while_the_window_is_hidden()
    {
        var page = new LifecyclePage();
        using var shell = Create();

        shell.SetCurrentPage(page);
        shell.SetCurrentPage(new LifecyclePage());

        Assert.Empty(page.Calls);
    }
}
