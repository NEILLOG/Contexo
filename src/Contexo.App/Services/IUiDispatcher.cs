namespace Contexo.App.Services;

public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> on the UI thread; never blocks the caller.</summary>
    void Post(Action action);
}

public interface IClipboardService
{
    /// <summary>Copies text to the clipboard. Returns false when the clipboard is unavailable.</summary>
    Task<bool> TrySetTextAsync(string text);
}

public interface IAppLifetime
{
    /// <summary>Shows and activates the main window (restores it from the tray).</summary>
    void ShowMainWindow();

    /// <summary>Really exits the application (does not just hide to the tray).</summary>
    void Exit();
}
