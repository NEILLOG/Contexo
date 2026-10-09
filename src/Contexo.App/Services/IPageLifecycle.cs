namespace Contexo.App.Services;

/// <summary>
/// Optional interface for page view models. Called by the shell when the page becomes visible / hidden,
/// including when the main window is hidden to or restored from the tray.
/// </summary>
public interface IPageLifecycle
{
    void OnNavigatedTo();

    void OnNavigatedFrom();
}
