using CommunityToolkit.Mvvm.ComponentModel;
using Contexo.App.About;
using Contexo.App.AiClients;
using Contexo.App.Folders;
using Contexo.App.Search;
using Contexo.App.Services;
using Contexo.App.Settings;
using Contexo.App.ViewModels;
using Contexo.Core.Abstractions;

namespace Contexo.App.Shell;

/// <summary>One entry of the left navigation list.</summary>
public sealed record NavigationItem(string Title, ViewModelBase Page);

/// <summary>
/// Main window view model: navigation list, current page, status bar, first-run wizard and start-up error switch.
/// Page view models may implement <see cref="IPageLifecycle"/>; it is called on navigation and when the window hides / shows.
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly NavigationService _navigation;
    private bool _windowVisible;

    [ObservableProperty]
    public partial ViewModelBase CurrentPage { get; private set; }

    [ObservableProperty]
    public partial NavigationItem? SelectedItem { get; set; }

    [ObservableProperty]
    public partial bool IsFirstRun { get; private set; }

    [ObservableProperty]
    public partial bool IsStartupError { get; private set; }

    public ShellViewModel(
        FoldersViewModel folders,
        FirstRunViewModel firstRun,
        SearchViewModel search,
        AiClientsViewModel aiClients,
        SettingsViewModel settings,
        AboutViewModel about,
        StatusBarViewModel statusBar,
        NavigationService navigation,
        DialogHostViewModel dialogHost,
        ISettingsStore settingsStore,
        IUiDispatcher dispatcher)
    {
        _navigation = navigation;
        _settings = settingsStore;
        _dispatcher = dispatcher;
        StatusBar = statusBar;
        DialogHost = dialogHost;
        FontScaleFactor = FontScaleFactors.For(_settings.Current.FontScale);

        NavigationItems =
        [
            new NavigationItem("資料夾", folders),
            new NavigationItem("試試看搜尋", search),
            new NavigationItem("AI 軟體", aiClients),
            new NavigationItem("設定", settings),
            new NavigationItem("關於與問題回報", about),
        ];

        IsFirstRun = !_settings.Current.FirstRunCompleted;
        if (IsFirstRun)
        {
            CurrentPage = firstRun;
        }
        else
        {
            SelectedItem = NavigationItems[0];
            CurrentPage = NavigationItems[0].Page;
        }

        _settings.Changed += OnSettingsChanged;
        _navigation.NavigationRequested += OnNavigationRequested;
    }

    public IReadOnlyList<NavigationItem> NavigationItems { get; }

    public StatusBarViewModel StatusBar { get; }

    public DialogHostViewModel DialogHost { get; }

    /// <summary>1.0 / 1.12 / 1.25 for the three text sizes; the main window scales its whole content by this.</summary>
    [ObservableProperty]
    public partial double FontScaleFactor { get; private set; }

    public string Title => "文脈 Contexo";

    /// <summary>The left navigation is hidden during the first-run wizard and on the start-up error screen.</summary>
    public bool IsNavigationVisible => !IsFirstRun && !IsStartupError;

    partial void OnIsFirstRunChanged(bool value) => OnPropertyChanged(nameof(IsNavigationVisible));

    partial void OnIsStartupErrorChanged(bool value) => OnPropertyChanged(nameof(IsNavigationVisible));

    partial void OnSelectedItemChanged(NavigationItem? value)
    {
        if (value is not null && !IsFirstRun && !IsStartupError)
        {
            SetCurrentPage(value.Page);
        }
    }

    /// <summary>Switches to a page by its navigation item.</summary>
    public void NavigateTo(NavigationItem item) => SelectedItem = item;

    /// <summary>Replaces every page with the start-up error screen (database could not be opened).</summary>
    public void ShowStartupError(StartupErrorViewModel error)
    {
        IsStartupError = true;
        SelectedItem = null;
        SetCurrentPage(error);
    }

    /// <summary>The main window became visible (first shown, or restored from the tray).</summary>
    public void OnWindowShown()
    {
        if (_windowVisible)
        {
            return;
        }

        _windowVisible = true;
        (CurrentPage as IPageLifecycle)?.OnNavigatedTo();
    }

    /// <summary>The main window was hidden to the tray; counts as leaving the current page.</summary>
    public void OnWindowHidden()
    {
        if (!_windowVisible)
        {
            return;
        }

        _windowVisible = false;
        (CurrentPage as IPageLifecycle)?.OnNavigatedFrom();
    }

    internal void SetCurrentPage(ViewModelBase page)
    {
        if (ReferenceEquals(page, CurrentPage))
        {
            return;
        }

        var old = CurrentPage;
        if (_windowVisible)
        {
            (old as IPageLifecycle)?.OnNavigatedFrom();
        }

        CurrentPage = page;
        if (_windowVisible)
        {
            (page as IPageLifecycle)?.OnNavigatedTo();
        }
    }

    private void OnNavigationRequested(object? sender, Type viewModelType)
    {
        if (IsFirstRun || IsStartupError)
        {
            return;
        }

        var item = NavigationItems.FirstOrDefault(i => i.Page.GetType() == viewModelType);
        if (item is not null)
        {
            _dispatcher.Post(() => SelectedItem = item);
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        var factor = FontScaleFactors.For(settings.FontScale);
        _dispatcher.Post(() => FontScaleFactor = factor);

        if (!IsFirstRun || !settings.FirstRunCompleted)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            if (!IsFirstRun)
            {
                return;
            }

            IsFirstRun = false;
            if (IsStartupError)
            {
                return;
            }

            SelectedItem = NavigationItems[0];
            SetCurrentPage(NavigationItems[0].Page);
        });
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _navigation.NavigationRequested -= OnNavigationRequested;
        StatusBar.Dispose();
    }
}

public static class FontScaleFactors
{
    public static double For(FontScale scale) => scale switch
    {
        FontScale.Standard => 1.0,
        FontScale.Large => 1.12,
        FontScale.ExtraLarge => 1.25,
        _ => 1.0,
    };
}
